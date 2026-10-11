using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace s3molib
{
    /// <summary>
    /// A copy of the game's own Mods folder (Documents\Electronic Arts\The Sims 3\Mods), made before S3MO takes it
    /// over: building replaces its Resource.cfg, after which mods still in Mods\Packages aren't loaded anymore.
    ///
    /// Backups go to "Game Mods Backup\&lt;date time&gt;\Mods" in the S3MO folder. ModBuilds is left out (S3MO makes it
    /// and can always make it again). A backup is written under a ".incomplete" name and only renamed when every
    /// file is in, so an interrupted copy is never mistaken for a backup.
    /// </summary>
    public static class GameModsBackup
    {
        public const string FolderName = "Game Mods Backup";
        private const string IncompleteSuffix = ".incomplete";
        private const string BuildFolderName = "ModBuilds";

        public sealed class Contents
        {
            /// <summary>Everything that would be copied (all files except ModBuilds).</summary>
            public List<FileInfo> Files { get; } = new List<FileInfo>();
            public long TotalBytes { get; internal set; }
            /// <summary>Files S3MO didn't make. When there are none, there's nothing worth backing up.</summary>
            public int UserFileCount { get; internal set; }
            /// <summary>A few of those files, relative to the Mods folder, to show the user.</summary>
            public List<string> Examples { get; } = new List<string>();
        }

        public sealed class Result
        {
            public string BackupFolder { get; internal set; }
            public int FilesCopied { get; internal set; }
            public long BytesCopied { get; internal set; }
            public List<string> Failed { get; } = new List<string>();
        }

        public static string BackupRoot(string s3moFolder) => Path.Combine(s3moFolder, FolderName);

        /// <summary>True when a finished backup exists (an interrupted one doesn't count).</summary>
        public static bool HasBackup(string s3moFolder)
        {
            string root = BackupRoot(s3moFolder);
            if (!Directory.Exists(root))
                return false;
            foreach (string dir in Directory.EnumerateDirectories(root))
                if (!dir.EndsWith(IncompleteSuffix, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        /// <summary>Lists what's in the game's Mods folder, apart from ModBuilds.</summary>
        public static Contents Scan(string gameModsFolder)
        {
            var c = new Contents();
            if (!Directory.Exists(gameModsFolder))
                return c;

            string builds = Path.Combine(gameModsFolder, BuildFolderName) + Path.DirectorySeparatorChar;
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (string path in Directory.EnumerateFiles(gameModsFolder, "*", options))
            {
                if (path.StartsWith(builds, StringComparison.OrdinalIgnoreCase))
                    continue;
                FileInfo f = new FileInfo(path);
                c.Files.Add(f);
                c.TotalBytes += f.Length;

                string relative = Path.GetRelativePath(gameModsFolder, path);
                if (relative.Equals("Resource.cfg", StringComparison.OrdinalIgnoreCase) && IsWrittenByS3mo(path))
                    continue;
                c.UserFileCount++;
                if (c.Examples.Count < 3)
                    c.Examples.Add(relative);
            }
            return c;
        }

        private static readonly Regex PriorityLine = new Regex(@"^Priority\s+-?\d+$", RegexOptions.CultureInvariant);

        /// <summary>Resource.cfg as S3MO writes it: "Priority N", then the ModBuilds line (custom lines may follow).</summary>
        public static bool IsWrittenByS3mo(string resourceCfgPath)
        {
            try
            {
                var lines = new List<string>();
                foreach (string line in File.ReadLines(resourceCfgPath))
                {
                    if (line.Trim().Length > 0)
                        lines.Add(line.Trim());
                    if (lines.Count == 2)
                        break;
                }
                return lines.Count == 2 && PriorityLine.IsMatch(lines[0]) && lines[1] == "PackedFile \"ModBuilds/*.package\"";
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }

        /// <summary>
        /// Copies the files found by <see cref="Scan"/> into a new dated backup. Files that can't be copied (in use,
        /// say) are listed in the result; the rest is still backed up.
        /// </summary>
        public static Result Create(string gameModsFolder, string s3moFolder, Contents contents, Action<BuildProgress> progress = null, CancellationToken cancellationToken = default, DateTime? now = null)
        {
            string root = BackupRoot(s3moFolder);
            Directory.CreateDirectory(root);

            // Leftovers of an interrupted backup
            foreach (string dir in Directory.EnumerateDirectories(root, "*" + IncompleteSuffix))
            {
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }

            string name = (now ?? DateTime.Now).ToString("yyyy-MM-dd HH.mm");
            string final = Path.Combine(root, name);
            for (int i = 2; Directory.Exists(final); i++)
                final = Path.Combine(root, $"{name} ({i})");
            string work = final + IncompleteSuffix;
            string target = Path.Combine(work, "Mods");
            Directory.CreateDirectory(target);

            var result = new Result();
            var report = new BuildProgress { BytesTotal = Math.Max(1, contents.TotalBytes) };
            int done = 0;
            foreach (FileInfo f in contents.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string relative = Path.GetRelativePath(gameModsFolder, f.FullName);
                string to = Path.Combine(target, relative);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    File.Copy(f.FullName, to, overwrite: true);
                    File.SetLastWriteTimeUtc(to, f.LastWriteTimeUtc);
                    result.FilesCopied++;
                    result.BytesCopied += f.Length;
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    result.Failed.Add($"{relative}: {ex.Message}");
                }

                done++;
                report.BytesDone += f.Length;
                report.Message = $"Backing up the game's Mods folder: {done:N0} of {contents.Files.Count:N0} files";
                progress?.Invoke(report);
            }

            File.WriteAllText(Path.Combine(work, "About this backup.txt"), string.Join(Environment.NewLine, new[]
            {
                $"A copy of the game's Mods folder, made by Sims 3 Mod Organizer on {(now ?? DateTime.Now):f}.",
                $"Copied from: {gameModsFolder}",
                "",
                "The ModBuilds folder isn't included: Sims 3 Mod Organizer makes it when you build.",
                "",
                "To go back to this state: close the game and the organizer, delete the Mods folder in",
                "Documents\\Electronic Arts\\The Sims 3 (or rename it), and copy the Mods folder from here in its place.",
                "",
            }), Encoding.UTF8);

            Directory.Move(work, final);
            result.BackupFolder = final;
            return result;
        }
    }
}
