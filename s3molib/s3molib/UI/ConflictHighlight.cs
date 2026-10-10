using System;
using System.Collections.Generic;
using System.IO;
using ns;

namespace s3molib
{
    /// <summary>
    /// Which rows of the mod list conflict with a selected mod or folder. Rows are keyed like the mod list:
    /// the mod name for a mod, "Mod\Folder\Sub" for a folder inside it.
    ///
    /// Not only the other mod is marked but every folder on the way to the conflicting package, so opening that
    /// mod shows exactly which folder the conflict is in.
    /// </summary>
    public static class ConflictHighlight
    {
        public static string RowKey(string modName, string relativeFolder) =>
            string.IsNullOrEmpty(relativeFolder) ? modName : Path.Combine(modName, relativeFolder);

        /// <param name="mod">The selected mod.</param>
        /// <param name="selectedFolder">The selected folder relative to the mod; empty when the mod itself is selected.</param>
        /// <param name="losing">Rows holding packages the selection overwrites.</param>
        /// <param name="winning">Rows holding packages that overwrite the selection.</param>
        public static void Compute(ModModel mod, string selectedFolder, ISet<string> losing, ISet<string> winning)
        {
            selectedFolder ??= string.Empty;
            bool folderSelected = selectedFolder.Length > 0;
            var seen = new HashSet<PackageModel>();

            foreach (PackageModel p in mod.GetPackagesUnder(selectedFolder))
            {
                // Other mods: the one with the higher priority wins. Switched-off mods don't count.
                foreach (PackageModel q in p.GetConflictingPackagesInOtherMods())
                {
                    if (!q.ModModel.Enabled || q.ModModel.Priority == mod.Priority || !seen.Add(q))
                        continue;
                    MarkRowsContaining(q, q.ModModel.Priority < mod.Priority ? losing : winning, null);
                }

                // Other folders of the same mod, when a folder is selected: the package later by name wins.
                if (!folderSelected || !p.HasConflictsInModModel)
                    continue;
                foreach (PackageModel q in p.ConflictingPackageModelsInModModel)
                {
                    if (IsInFolder(q.Name, selectedFolder))
                        continue; // inside the selection itself
                    int order = StringLogicalComparer.Compare(q.Name, p.Name);
                    if (order != 0)
                        MarkRowsContaining(q, order < 0 ? losing : winning, selectedFolder);
                }
            }
        }

        /// <summary>
        /// Marks the mod row and the folder rows that contain a package. Within the selection's own mod, the
        /// folders that also contain the selection are left alone, except the folder the package sits in directly.
        /// </summary>
        private static void MarkRowsContaining(PackageModel q, ISet<string> rows, string sameModSelection)
        {
            string modName = q.ModModel.Name;
            string dir = ModModel.FolderOf(q);
            bool direct = true;
            while (true)
            {
                bool holdsSelection = sameModSelection != null && IsInFolder(sameModSelection, dir);
                if (direct || !holdsSelection)
                    rows.Add(RowKey(modName, dir));
                if (dir.Length == 0)
                    break;
                dir = Path.GetDirectoryName(dir) ?? string.Empty;
                direct = false;
            }
        }

        /// <summary>True when a path (relative to the mod) is the folder itself or inside it; everything is inside the mod folder ("").</summary>
        public static bool IsInFolder(string path, string folder) =>
            folder.Length == 0
            || path.Equals(folder, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
