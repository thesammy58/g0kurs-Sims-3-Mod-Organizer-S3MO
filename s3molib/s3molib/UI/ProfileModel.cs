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

            if (save)
                Save(profileName);
        }

        /// <summary>
        /// Reads every package index (from the cache where possible) and works out all conflicts in one pass.
        /// Safe to run on a background thread; call <see cref="ApplyAnalysis"/> on the UI thread with the result.
        /// </summary>
        public AnalysisResult Analyze(PackageCache cache, CancellationToken cancellationToken = default)
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
            result.Conflicts = ConflictAnalyzer.Analyze(_modModelLookup.Values);
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
                if (i < 0)
                    continue;
                lines[i] = lines[i].Substring(0, 1) + newName;

                File.WriteAllLines(modListPath, lines);
            }
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
        }

        /// <summary>
        /// The packages of enabled mods in merge order: mods by priority, then packages by name.
        /// Later entries win conflicts. Packages that couldn't be read are returned separately.
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
