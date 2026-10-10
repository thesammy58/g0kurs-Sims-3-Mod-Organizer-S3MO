using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace s3molib
{
    public class ProfileModel
    {
        private string _exePath;
        public string RootPath => _exePath;
        public string ModsFolderPath => Path.Combine(_exePath, "Mods");

        public List<ModModel> ModModels => new List<ModModel>(_modModelLookup.Values);

        public ModModel GetModModel(string modName) => _modModelLookup[modName];
        public bool TryGetModModel(string modName, out ModModel modModel) => _modModelLookup.TryGetValue(modName, out modModel);
        private Dictionary<string, ModModel> _modModelLookup = new Dictionary<string, ModModel>();

        // Folders inside mods that are switched off in this profile, as "Mod name\folder\subfolder".
        // Kept in Profiles\<profile>\disabledfolders.txt so modlist.txt stays readable by older versions.
        private const string DisabledFoldersFile = "disabledfolders.txt";
        private readonly HashSet<string> _disabledFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static string FolderKey(ModModel mod, string relativeFolder) => Path.Combine(mod.Name, relativeFolder);

        /// <param name="rootPath">Folder containing Mods and Profiles (defaults to the program folder).</param>
        public ProfileModel(string rootPath = null)
        {
            _exePath = rootPath ?? AppDomain.CurrentDomain.BaseDirectory;
            string modFolderPath = Path.Combine(_exePath, "Mods");
            if (!Directory.Exists(modFolderPath))
                Directory.CreateDirectory(modFolderPath);

            string profileFolderPath = Path.Combine(_exePath, "Profiles");
            if (!Directory.Exists(profileFolderPath))
                Directory.CreateDirectory(profileFolderPath);

            string[] modsPaths = Directory.GetDirectories(modFolderPath);

            foreach (string path in modsPaths)
            {
                string name = Path.GetFileName(path);
                _modModelLookup[name] = new ModModel(path);
            }
        }

        private string ProfileFolder(string profileName) => Path.Combine(_exePath, "Profiles", profileName);

        /// <summary>Read modlist.txt and populate modModelLookup based on the mod order, then save a new modlist.txt</summary>
        public void Setup(string profileName, bool save = true)
        {
            string profilePath = ProfileFolder(profileName);
            if (!Directory.Exists(profilePath))
                Directory.CreateDirectory(profilePath);

            string modListPath = Path.Combine(profilePath, "modlist.txt");
            string[] lines = File.Exists(modListPath) ? File.ReadAllLines(modListPath) : Array.Empty<string>();

            HashSet<string> remainingMods = new HashSet<string>(_modModelLookup.Keys);
            int i = 0;

            foreach (string line in lines)
            {
                if (line.Length < 2)
                    continue;

                string modName = line.Substring(1);

                if (!remainingMods.Remove(modName))
                    continue;

                bool enabled = line[0] == '+';
                _modModelLookup[modName].Update(modName, enabled, i++);
            }

            // New mods go to the bottom, disabled, in a stable (alphabetical) order.
            List<string> rest = new List<string>(remainingMods);
            rest.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (string remainingMod in rest)
                _modModelLookup[remainingMod].Update(remainingMod, false, i++);

            _disabledFolders.Clear();
            string disabledPath = Path.Combine(profilePath, DisabledFoldersFile);
            if (File.Exists(disabledPath))
            {
                foreach (string raw in File.ReadAllLines(disabledPath))
                {
                    string line = raw.Trim();
                    int sep = line.IndexOfAny(new[] { '\\', '/' });
                    if (sep <= 0 || !_modModelLookup.TryGetValue(line.Substring(0, sep), out ModModel mod))
                        continue;
                    string rel = line.Substring(sep + 1).Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                    if (mod.FindFolder(rel) != null)
                        _disabledFolders.Add(FolderKey(mod, rel));
                }
            }

            if (save)
                Save(profileName);
        }

        // ------------------------------------------------------------------ folders switched off inside mods

        /// <summary>The folder's own check box (not whether its mod or a parent folder is on).</summary>
        public bool IsFolderEnabled(ModModel mod, string relativeFolder) =>
            string.IsNullOrEmpty(relativeFolder) ? mod.Enabled : !_disabledFolders.Contains(FolderKey(mod, relativeFolder));

        public void SetFolderEnabled(ModModel mod, string relativeFolder, bool enabled)
        {
            if (string.IsNullOrEmpty(relativeFolder))
                return;
            if (enabled)
                _disabledFolders.Remove(FolderKey(mod, relativeFolder));
            else
                _disabledFolders.Add(FolderKey(mod, relativeFolder));
        }

        /// <summary>True when the mod and every folder on the way down to this one are switched on.</summary>
        public bool IsFolderEffectivelyEnabled(ModModel mod, string relativeFolder)
        {
            if (!mod.Enabled)
                return false;
            for (string dir = relativeFolder; !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (_disabledFolders.Contains(FolderKey(mod, dir)))
                    return false;
            return true;
        }

        /// <summary>True when some folder below this one (or below the mod, for an empty path) is switched off.</summary>
        public bool HasDisabledFoldersBelow(ModModel mod, string relativeFolder)
        {
            string prefix = FolderKey(mod, relativeFolder ?? string.Empty);
            if (!prefix.EndsWith(Path.DirectorySeparatorChar))
                prefix += Path.DirectorySeparatorChar;
            foreach (string key in _disabledFolders)
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>A copy of the switched-off folders, for work on another thread.</summary>
        public HashSet<string> SnapshotDisabledFolders() => new HashSet<string>(_disabledFolders, StringComparer.OrdinalIgnoreCase);

        /// <summary>True when the package sits in a folder that is switched off (its mod's own state isn't considered).</summary>
        public static bool IsInDisabledFolder(PackageModel p, ISet<string> disabledFolders)
        {
            if (disabledFolders.Count == 0)
                return false;
            for (string dir = ModModel.FolderOf(p); !string.IsNullOrEmpty(dir); dir = Path.GetDirectoryName(dir))
                if (disabledFolders.Contains(FolderKey(p.ModModel, dir)))
                    return true;
            return false;
        }

        /// <summary>True when the package goes into the build: its mod is on and none of its folders is switched off.</summary>
        public bool IsPackageIncluded(PackageModel p) => p.ModModel.Enabled && !IsInDisabledFolder(p, _disabledFolders);

        /// <summary>
        /// Reads every package index (from the cache where possible) and works out all conflicts in one pass.
        /// Safe to run on a background thread; call <see cref="ApplyAnalysis"/> on the UI thread with the result.
        /// </summary>
        /// <param name="disabledFolders">Snapshot from <see cref="SnapshotDisabledFolders"/> taken on the UI thread; packages in those folders are left out of the conflict check.</param>
        public AnalysisResult Analyze(PackageCache cache, CancellationToken cancellationToken = default, ISet<string> disabledFolders = null)
        {
            var result = new AnalysisResult();
            var all = new List<PackageModel>();
            foreach (ModModel m in _modModelLookup.Values)
                all.AddRange(m.Packages);

            cache?.BeginScan();
            cache?.ResetCounters();
            int failed = 0;

            Parallel.ForEach(all, new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount), CancellationToken = cancellationToken }, pm =>
            {
                try
                {
                    Package package = cache != null ? cache.GetPackage(pm.FilePath, out _) : Package.Load(pm.FilePath);
                    pm.SetPackage(package);
                }
                catch (Exception ex)
                {
                    pm.SetLoadError(ex.Message);
                    Interlocked.Increment(ref failed);
                    Logger.LogError($"Couldn't read {pm.ModModel.Name}\\{pm.Name}: {ex.Message}");
                }
            });

            cancellationToken.ThrowIfCancellationRequested();

            result.PackageCount = all.Count;
            result.FailedCount = failed;
            result.CacheHits = cache?.Hits ?? 0;
            ISet<string> excluded = disabledFolders ?? SnapshotDisabledFolders();
            result.Conflicts = ConflictAnalyzer.Analyze(_modModelLookup.Values, excluded.Count == 0 ? null : p => !IsInDisabledFolder(p, excluded));
            cache?.Save();
            return result;
        }

        /// <summary>Publishes the conflicts found by <see cref="Analyze"/> to the models (UI thread).</summary>
        public void ApplyAnalysis(AnalysisResult result) => result.Conflicts.ApplyTo(_modModelLookup.Values);

        public void Save(string profileName)
        {
            string profilePath = ProfileFolder(profileName);
            if (!Directory.Exists(profilePath))
                Directory.CreateDirectory(profilePath);

            string modListPath = Path.Combine(profilePath, "modlist.txt");

            List<ModModel> allModModels = new List<ModModel>(_modModelLookup.Values);
            allModModels.Sort(new ModModelComparer(ModModelComparer.SortBy.Priority));

            List<string> lines = new List<string>(allModModels.Count);
            foreach (ModModel mod in allModModels)
                lines.Add((mod.Enabled ? "+" : "-") + mod.Name);

            File.WriteAllLines(modListPath, lines);

            string disabledPath = Path.Combine(profilePath, DisabledFoldersFile);
            if (_disabledFolders.Count > 0 || File.Exists(disabledPath))
            {
                List<string> folders = new List<string>(_disabledFolders);
                folders.Sort(StringComparer.OrdinalIgnoreCase);
                File.WriteAllLines(disabledPath, folders.ConvertAll(f => f.Replace(Path.DirectorySeparatorChar, '\\')));
            }
        }

        /// <summary>Rewrites the mod-name part of switched-off folder entries ("Old\x" becomes "New\x").</summary>
        private static string RenameFolderEntry(string entry, string oldName, string newName)
        {
            if (entry.Length > oldName.Length && entry.StartsWith(oldName, StringComparison.OrdinalIgnoreCase) && (entry[oldName.Length] == '\\' || entry[oldName.Length] == '/'))
                return newName + entry.Substring(oldName.Length);
            return entry;
        }

        public void RenameMod(string oldName, string newName)
        {
            if (oldName.Equals(newName))
                return;

            ModModel mod = _modModelLookup[oldName];

            string modsFolderPath = Path.Combine(_exePath, "Mods");
            Directory.Move(Path.Combine(modsFolderPath, oldName), Path.Combine(modsFolderPath, newName));

            mod.Update(newName, mod.Enabled, mod.Priority);

            _modModelLookup.Remove(oldName);
            _modModelLookup.Add(newName, mod);

            string profilesFolderPath = Path.Combine(_exePath, "Profiles");
            string[] profiles = Directory.GetDirectories(profilesFolderPath);

            foreach (string path in profiles)
            {
                string modListPath = Path.Combine(path, "modlist.txt");

                if (!File.Exists(modListPath))
                    continue;

                string[] lines = File.ReadAllLines(modListPath);

                int i = Array.FindIndex(lines, l => l.Length > 1 && l.Substring(1).Equals(oldName));
                if (i >= 0)
                {
                    lines[i] = lines[i].Substring(0, 1) + newName;
                    File.WriteAllLines(modListPath, lines);
                }

                string disabledPath = Path.Combine(path, DisabledFoldersFile);
                if (File.Exists(disabledPath))
                {
                    string[] entries = File.ReadAllLines(disabledPath);
                    File.WriteAllLines(disabledPath, Array.ConvertAll(entries, e => RenameFolderEntry(e, oldName, newName)));
                }
            }

            var renamed = new List<string>(_disabledFolders);
            _disabledFolders.Clear();
            foreach (string e in renamed)
                _disabledFolders.Add(RenameFolderEntry(e, oldName, newName));
        }

        public string CreateEmptyMod()
        {
            string modsFolder = Path.Combine(_exePath, "Mods");
            string path = Path.Combine(modsFolder, "New Mod");
            if (Directory.Exists(path))
            {
                int i = 1;
                while (Directory.Exists(Path.Combine(modsFolder, $"New Mod ({i})"))) { i++; }
                path = Path.Combine(modsFolder, $"New Mod ({i})");
            }
            Directory.CreateDirectory(path);
            string modName = Path.GetFileName(path);

            ModModel modModel = new ModModel(path);
            modModel.Update(modName, false, _modModelLookup.Count);
            _modModelLookup.Add(modName, modModel);
            return modName;
        }

        public void DeleteMod(string modName)
        {
            ModModel modModel = _modModelLookup[modName];
            Directory.Delete(modModel.FolderPath, true);

            _modModelLookup.Remove(modName);
            _disabledFolders.RemoveWhere(e => RenameFolderEntry(e, modName, "?") != e);
        }

        /// <summary>
        /// The packages of enabled mods in merge order: mods by priority, then packages by name.
        /// Later entries win conflicts. Packages in switched-off folders are left out; packages that couldn't be read are returned separately.
        /// </summary>
        public List<PackageModel> GetPackagesInBuildOrder(out List<PackageModel> unreadable)
        {
            unreadable = new List<PackageModel>();
            List<ModModel> mods = ModModels.FindAll(m => m.Enabled);
            mods.Sort(new ModModelComparer(ModModelComparer.SortBy.Priority));

            List<PackageModel> result = new List<PackageModel>();
            foreach (ModModel mod in mods)
                foreach (PackageModel p in mod.GetSortedPackages())
                {
                    if (IsInDisabledFolder(p, _disabledFolders))
                        continue;
                    if (p.LoadError != null)
                        unreadable.Add(p);
                    else
                        result.Add(p);
                }
            return result;
        }
    }

    public sealed class AnalysisResult
    {
        public int PackageCount { get; internal set; }
        public int FailedCount { get; internal set; }
        public int CacheHits { get; internal set; }
        public ConflictResult Conflicts { get; internal set; }
    }
}
