using System;
using System.Collections.Generic;

namespace s3molib
{
    /// <summary>
    /// Finds every resource that appears in more than one package, in a single pass.
    ///
    /// The previous implementation compared each package against every other package and searched lists
    /// linearly, which grows with the square of the total resource count. Here every resource key is put in
    /// one dictionary; any key owned by two or more packages is a conflict. The three results the UI uses
    /// (mod-vs-mod, package-vs-package inside a mod, and the per-resource detail) all come out of that map.
    /// </summary>
    public static class ConflictAnalyzer
    {
        public static ConflictResult Analyze(IEnumerable<ModModel> mods)
        {
            // Stable order (priority, then package name) so the per-resource lists come out in priority order.
            List<ModModel> modList = new List<ModModel>(mods);
            modList.Sort(new ModModelComparer(ModModelComparer.SortBy.Priority));

            // Most keys have a single owner, so store the owner directly and only allocate a list on the first conflict.
            var owners = new Dictionary<ResourceKey, object>();
            int resourceCount = 0;

            foreach (ModModel mod in modList)
            {
                foreach (PackageModel pm in mod.GetSortedPackages())
                {
                    if (pm.LoadError != null || !pm.IsLoaded)
                        continue;

                    foreach (ResourceEntry e in pm.Package.Entries)
                    {
                        if (Package.IsIgnoredType(e.Type))
                            continue;
                        resourceCount++;

                        ref object slot = ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrAddDefault(owners, e.Key, out bool exists);
                        if (!exists)
                        {
                            slot = pm;
                        }
                        else if (slot is PackageModel single)
                        {
                            if (!ReferenceEquals(single, pm))
                                slot = new List<PackageModel>(2) { single, pm };
                        }
                        else
                        {
                            var list = (List<PackageModel>)slot;
                            if (!ReferenceEquals(list[list.Count - 1], pm)) // same key twice inside one package is not a conflict
                                list.Add(pm);
                        }
                    }
                }
            }

            var result = new ConflictResult { ResourceCount = resourceCount };

            foreach (var kv in owners)
            {
                if (kv.Value is not List<PackageModel> list)
                    continue;

                result.ConflictingKeyCount++;
                string resource = null;

                for (int i = 0; i < list.Count; i++)
                {
                    PackageModel p = list[i];
                    for (int j = i + 1; j < list.Count; j++)
                    {
                        PackageModel q = list[j];

                        if (ReferenceEquals(p.ModModel, q.ModModel))
                        {
                            result.InMod(p).Add(q);
                            result.InMod(q).Add(p);
                        }
                        else
                        {
                            result.Mods(p.ModModel).Add(q.ModModel);
                            result.Mods(q.ModModel).Add(p.ModModel);

                            resource ??= kv.Key.ToString();
                            result.Detail(p, resource).Add(q);
                            result.Detail(q, resource).Add(p);
                        }
                    }
                }
            }

            return result;
        }
    }

    public sealed class ConflictResult
    {
        public int ResourceCount { get; internal set; }
        public int ConflictingKeyCount { get; internal set; }

        internal readonly Dictionary<ModModel, HashSet<ModModel>> ModConflicts = new Dictionary<ModModel, HashSet<ModModel>>();
        internal readonly Dictionary<PackageModel, HashSet<PackageModel>> InModConflicts = new Dictionary<PackageModel, HashSet<PackageModel>>();
        internal readonly Dictionary<PackageModel, Dictionary<string, List<PackageModel>>> Detailed = new Dictionary<PackageModel, Dictionary<string, List<PackageModel>>>();

        internal HashSet<ModModel> Mods(ModModel m)
        {
            if (!ModConflicts.TryGetValue(m, out var set))
                ModConflicts[m] = set = new HashSet<ModModel>();
            return set;
        }

        internal HashSet<PackageModel> InMod(PackageModel p)
        {
            if (!InModConflicts.TryGetValue(p, out var set))
                InModConflicts[p] = set = new HashSet<PackageModel>();
            return set;
        }

        internal List<PackageModel> Detail(PackageModel p, string resource)
        {
            if (!Detailed.TryGetValue(p, out var byResource))
                Detailed[p] = byResource = new Dictionary<string, List<PackageModel>>();
            if (!byResource.TryGetValue(resource, out var list))
                byResource[resource] = list = new List<PackageModel>();
            return list;
        }

        /// <summary>Copies the results onto the models. Every model is reset, so stale conflicts disappear.</summary>
        internal void ApplyTo(IEnumerable<ModModel> mods)
        {
            foreach (ModModel m in mods)
            {
                ModConflicts.TryGetValue(m, out var modSet);
                m.SetConflicts(modSet);

                foreach (PackageModel p in m.Packages)
                {
                    InModConflicts.TryGetValue(p, out var inMod);
                    Detailed.TryGetValue(p, out var detail);
                    p.SetConflicts(inMod, detail);
                }
            }
        }
    }
}
