using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ns;

namespace s3molib
{
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

        public ModModel(string path)
        {
            FolderPath = path;
            Name = Path.GetFileName(path);

            // Packages may live in subfolders of the mod folder; they're identified by their path relative to the mod folder.
            foreach (string filePath in EnumeratePackages(path))
            {
                string relative = Path.GetRelativePath(path, filePath);
                PackageModel packageModel = new PackageModel(this, filePath, relative);
                _packageModelLookup[relative] = packageModel;
            }
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
