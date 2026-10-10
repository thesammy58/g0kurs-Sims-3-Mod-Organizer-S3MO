using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ns;

namespace s3molib
{
    /// <summary>A folder inside a mod (the mod folder itself is the root, with an empty path).</summary>
    public sealed class FolderNode
    {
        public string Name { get; }
        /// <summary>Path relative to the mod folder, e.g. "Female\Hair"; empty for the mod folder itself.</summary>
        public string RelativePath { get; }
        public FolderNode Parent { get; }
        public List<FolderNode> Children { get; } = new List<FolderNode>();
        public int Depth => Parent == null ? 0 : Parent.Depth + 1;

        internal FolderNode(string name, string relativePath, FolderNode parent)
        {
            Name = name;
            RelativePath = relativePath;
            Parent = parent;
        }
    }

    public class ModModel
    {
        public string FolderPath { get; private set; }
        public string Name { get; private set; }
        public bool Enabled { get; private set; }
        public int Priority { get; private set; }

        public List<PackageModel> PackageModels => new List<PackageModel>(_packageModelLookup.Values);

        /// <summary>The packages without copying (don't modify while iterating).</summary>
        public IReadOnlyCollection<PackageModel> Packages => _packageModelLookup.Values;

        public bool TryGetPackageModel(string name, out PackageModel packageModel) => _packageModelLookup.TryGetValue(name, out packageModel);
        private Dictionary<string, PackageModel> _packageModelLookup = new Dictionary<string, PackageModel>(StringComparer.OrdinalIgnoreCase);

        public List<ModModel> ConflictingModModels => new List<ModModel>(_conflictingModModels);
        public IReadOnlyCollection<ModModel> ConflictingMods => _conflictingModModels;
        private HashSet<ModModel> _conflictingModModels = new HashSet<ModModel>();

        internal void SetConflicts(HashSet<ModModel> conflicts) => _conflictingModModels = conflicts ?? new HashSet<ModModel>();

        public bool HasPackageConflictsInside
        {
            get
            {
                foreach (PackageModel p in _packageModelLookup.Values)
                    if (p.HasConflictsInModModel)
                        return true;
                return false;
            }
        }

        /// <summary>The mod folder and its subfolders, sorted by name.</summary>
        public FolderNode RootFolder { get; private set; }
        private readonly Dictionary<string, FolderNode> _folders = new Dictionary<string, FolderNode>(StringComparer.OrdinalIgnoreCase);

        public FolderNode FindFolder(string relativePath) => _folders.TryGetValue(relativePath ?? string.Empty, out FolderNode f) ? f : null;

        public ModModel(string path)
        {
            FolderPath = path;
            Name = Path.GetFileName(path);
            BuildFolderTree();

            // Packages may live in subfolders of the mod folder; they're identified by their path relative to the mod folder.
            foreach (string filePath in EnumeratePackages(path))
            {
                string relative = Path.GetRelativePath(path, filePath);
                PackageModel packageModel = new PackageModel(this, filePath, relative);
                _packageModelLookup[relative] = packageModel;
            }
        }

        private void BuildFolderTree()
        {
            RootFolder = new FolderNode(Name, string.Empty, null);
            _folders[string.Empty] = RootFolder;
            if (!Directory.Exists(FolderPath))
                return;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            var relatives = new List<string>();
            foreach (string dir in Directory.EnumerateDirectories(FolderPath, "*", options))
                relatives.Add(Path.GetRelativePath(FolderPath, dir));
            relatives.Sort((a, b) => a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b)); // parents first

            foreach (string rel in relatives)
            {
                string parentRel = Path.GetDirectoryName(rel) ?? string.Empty;
                if (!_folders.TryGetValue(parentRel, out FolderNode parent))
                    continue; // inside a skipped (hidden) folder
                var node = new FolderNode(Path.GetFileName(rel), rel, parent);
                parent.Children.Add(node);
                _folders[rel] = node;
            }
            foreach (FolderNode f in _folders.Values)
                f.Children.Sort((a, b) => StringLogicalComparer.Compare(a.Name, b.Name));
        }

        /// <summary>Folder (relative to the mod) that a package sits in; empty for the mod folder itself.</summary>
        public static string FolderOf(PackageModel p) => Path.GetDirectoryName(p.Name) ?? string.Empty;

        /// <summary>Packages directly in the given folder (not in its subfolders), sorted by name.</summary>
        public List<PackageModel> GetPackagesIn(string relativeFolder)
        {
            List<PackageModel> list = PackageModels.FindAll(p => string.Equals(FolderOf(p), relativeFolder ?? string.Empty, StringComparison.OrdinalIgnoreCase));
            list.Sort(PackageModel.CompareByName);
            return list;
        }

        /// <summary>Packages in the given folder and all its subfolders.</summary>
        public List<PackageModel> GetPackagesUnder(string relativeFolder)
        {
            if (string.IsNullOrEmpty(relativeFolder))
                return PackageModels;
            string prefix = relativeFolder + Path.DirectorySeparatorChar;
            return PackageModels.FindAll(p => p.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Non-package files directly in the given folder, relative to the mod folder.</summary>
        public List<string> GetOtherFilesIn(string relativeFolder)
        {
            var result = new List<string>();
            string dir = Path.Combine(FolderPath, relativeFolder ?? string.Empty);
            if (!Directory.Exists(dir))
                return result;
            var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (string f in Directory.EnumerateFiles(dir, "*", options))
                if (!f.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                    result.Add(Path.GetRelativePath(FolderPath, f));
            result.Sort(StringLogicalComparer.Compare);
            return result;
        }

        /// <summary>All .package files in the folder and its subfolders (case-insensitive, hidden folders skipped).</summary>
        public static IEnumerable<string> EnumeratePackages(string folder)
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            return Directory.EnumerateFiles(folder, "*.package", options);
        }

        /// <summary>Non-package files in the mod folder and subfolders, relative to the mod folder.</summary>
        public List<string> GetOtherFiles()
        {
            var result = new List<string>();
            if (!Directory.Exists(FolderPath))
                return result;

            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            };
            foreach (string f in Directory.EnumerateFiles(FolderPath, "*", options))
                if (!f.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                    result.Add(Path.GetRelativePath(FolderPath, f));
            result.Sort(StringLogicalComparer.Compare);
            return result;
        }

        /// <summary>Packages sorted the way they are merged (and listed).</summary>
        public List<PackageModel> GetSortedPackages()
        {
            List<PackageModel> list = PackageModels;
            list.Sort(PackageModel.CompareByName);
            return list;
        }

        public void Update(string name, bool enabled, int priority)
        {
            if (!Name.Equals(name))
            {
                string modsFolder = Path.GetDirectoryName(FolderPath);
                FolderPath = Path.Combine(modsFolder, name);

                foreach (PackageModel packageModel in _packageModelLookup.Values)
                    packageModel.Update(Path.Combine(FolderPath, packageModel.Name));
            }

            Name = name;
            Enabled = enabled;
            Priority = priority;
        }
    }

    public class ModModelComparer : IComparer<ModModel>
    {
        public enum SortBy
        {
            Name,
            Priority
        }

        private SortBy _mode = SortBy.Priority;

        public ModModelComparer(SortBy mode)
        {
            _mode = mode;
        }

        public int Compare(ModModel x, ModModel y)
        {
            switch (_mode)
            {
                case SortBy.Name:
                    return x.Name.CompareTo(y.Name);

                case SortBy.Priority:
                    return x.Priority.CompareTo(y.Priority);

                default:
                    return x.Priority.CompareTo(y.Priority);
            }
        }
    }
}
