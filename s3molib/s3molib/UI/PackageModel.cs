using System;
using System.Collections.Generic;
using System.IO;
using ns;

namespace s3molib
{
    public class PackageModel
    {
        /// <summary>Parent ModModel that owns this PackageModel</summary>
        public ModModel ModModel { get; private set; }
        public string FilePath { get; private set; }

        /// <summary>Path relative to the mod folder, e.g. "hair.package" or "Female\hair.package".</summary>
        public string Name { get; private set; }

        /// <summary>Set when the package could not be read; the package is then left out of conflict checks and builds.</summary>
        public string LoadError { get; private set; }

        public Package Package
        {
            get
            {
                if (_package == null)
                {
                    lock (_loadLock)
                    {
                        if (_package == null)
                        {
                            if (!File.Exists(FilePath))
                                throw new Exception($"{Name} is not found in path {Path.GetDirectoryName(FilePath)}.");
                            _package = Package.Load(FilePath);
                        }
                    }
                }
                return _package;
            }
        }
        private volatile Package _package = null;
        private readonly object _loadLock = new object();

        /// <summary>True once the package index is available without touching the disk.</summary>
        public bool IsLoaded => _package != null;

        internal void SetPackage(Package package)
        {
            _package = package;
            LoadError = null;
        }

        internal void SetLoadError(string error)
        {
            _package = null;
            LoadError = error;
        }

        public List<PackageModel> ConflictingPackageModelsInModModel => new List<PackageModel>(_conflictingPackageModelsInModModel);
        public bool HasConflictsInModModel => _conflictingPackageModelsInModModel.Count > 0;
        private HashSet<PackageModel> _conflictingPackageModelsInModModel = new HashSet<PackageModel>();

        public List<string> GetConflictingResources() => new List<string>(_resourceOtherPackageModelsLookup.Keys);

        /// <summary>Packages in other mods that share a resource with this one (may repeat a package).</summary>
        public IEnumerable<PackageModel> GetConflictingPackagesInOtherMods()
        {
            foreach (List<PackageModel> list in _resourceOtherPackageModelsLookup.Values)
                foreach (PackageModel p in list)
                    yield return p;
        }
        public List<PackageModel> GetConflictingPackageModelsByResource(string key) => _resourceOtherPackageModelsLookup[key];
        private Dictionary<string, List<PackageModel>> _resourceOtherPackageModelsLookup = new Dictionary<string, List<PackageModel>>();

        internal void SetConflicts(HashSet<PackageModel> inModModel, Dictionary<string, List<PackageModel>> byResource)
        {
            _conflictingPackageModelsInModModel = inModModel ?? new HashSet<PackageModel>();
            _resourceOtherPackageModelsLookup = byResource ?? new Dictionary<string, List<PackageModel>>();
        }

        public PackageModel(ModModel modModel, string path, string name)
        {
            ModModel = modModel;
            FilePath = path;
            Name = name;
        }

        public PackageModel(ModModel modModel, string path) : this(modModel, path, Path.GetFileName(path)) { }

        /// <summary>Called when the parent mod folder is renamed; the name relative to the mod folder stays the same.</summary>
        public void Update(string path)
        {
            FilePath = path;
            _package = null;
        }

        /// <summary>Sort order used everywhere packages of one mod are listed or merged.</summary>
        public static int CompareByName(PackageModel p1, PackageModel p2) => StringLogicalComparer.Compare(p1.Name, p2.Name);

        /// <summary>Display name without the ".package" extension (case-insensitive, safe for odd names).</summary>
        public static string StripExtension(string name)
        {
            int i = name.LastIndexOf(".package", StringComparison.OrdinalIgnoreCase);
            return i > 0 ? name.Substring(0, i) : name;
        }
    }
}
