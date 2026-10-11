using s3molib;
using System.Diagnostics;

namespace s3mo
{
    // Backing up the game's own Mods folder (Documents\Electronic Arts\The Sims 3\Mods) before S3MO takes it over.
    // Building replaces its Resource.cfg, after which mods still sitting in Mods\Packages aren't loaded anymore, so
    // before a build S3MO offers a copy as long as there's no backup yet and the folder has files S3MO didn't make.
    partial class MainForm
    {
        private const string AskToBackUpSetting = "AskToBackUpGameMods";

        /// <summary>What's running while <see cref="_isBuilding"/> is set, for the "please wait" message.</summary>
        private string _busyWith = "A build";

        private static string S3moFolder => AppDomain.CurrentDomain.BaseDirectory;

        private static string FormatSize(long bytes) =>
            bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.0} GB"
            : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):0} MB"
            : $"{Math.Max(1, bytes / 1024):0} KB";

        private enum BackupOutcome { Done, DoneWithProblems, NotDone }

        /// <summary>
        /// Before a build: when there's no backup yet and the game's Mods folder has files S3MO didn't make, offers
        /// to back it up first. Returns false when the build shouldn't go ahead (the dialog was closed, or the backup failed).
        /// </summary>
        private async Task<bool> OfferBackupBeforeBuildAsync(string gameMods)
        {
            if (Settings.TryGetSettings(AskToBackUpSetting, out string ask) && bool.TryParse(ask, out bool yes) && !yes)
                return true;
            if (!Directory.Exists(gameMods))
                return true;

            GameModsBackup.Contents? contents;
            this.Enabled = false;
            try
            {
                contents = await Task.Run(() => GameModsBackup.HasBackup(S3moFolder) ? null : GameModsBackup.Scan(gameMods));
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Couldn't look at the game's Mods folder: " + ex.Message);
                return true;
            }
            finally
            {
                this.Enabled = true;
            }
            if (contents == null || contents.UserFileCount == 0)
                return true;

            var backUp = new TaskDialogCommandLinkButton("Back up, then build",
                $"Copies {contents.Files.Count:N0} files ({FormatSize(contents.TotalBytes)}) to the \"{GameModsBackup.FolderName}\" folder next to s3mo.exe. S3MO's merged files (ModBuilds) are left out.");
            var without = new TaskDialogCommandLinkButton("Build without a backup");
            var page = new TaskDialogPage
            {
                Caption = "Sims 3 Mod Organizer",
                Heading = "Back up your Mods folder first?",
                Text = "Do you want to back up your entire Mods folder before building? An initial backup is highly recommended, " +
                       "so you can verify that the merge works without issues during your first use.\n\n" +
                       "Building replaces Resource.cfg in the game's Mods folder, so mods still in that folder (for example in Mods\\Packages) " +
                       "stop loading. Move them into S3MO's own Mods folder.",
                Icon = TaskDialogIcon.Information,
                AllowCancel = true,
                Verification = new TaskDialogVerificationCheckBox("Don't ask again"),
            };
            page.Buttons.Add(backUp);
            page.Buttons.Add(without);
            page.DefaultButton = backUp;

            TaskDialogButton choice = TaskDialog.ShowDialog(this, page);
            if (choice == backUp)
                return await BackUpGameModsAsync(gameMods, contents, beforeBuild: true) != BackupOutcome.NotDone;
            if (choice == without)
            {
                if (page.Verification.Checked)
                {
                    Settings.SetSettings(AskToBackUpSetting, false);
                    Settings.WriteSettings();
                    Logger.Log("S3MO won't offer a backup before building again. You can still make one from the folder button.");
                }
                return true;
            }
            return false; // dialog closed: no build
        }

        /// <summary>The "Back up game Mods folder" menu item: always makes a new backup.</summary>
        private async void BackUpGameModsNow()
        {
            if (_isBuilding || !TryGetBuildFolders(true, out string gameMods, out _))
                return;
            if (!Directory.Exists(gameMods))
            {
                MessageBox.Show($"There's no Mods folder at '{gameMods}' yet, so there's nothing to back up.", "Back up", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            this.Enabled = false;
            GameModsBackup.Contents contents;
            try { contents = await Task.Run(() => GameModsBackup.Scan(gameMods)); }
            finally { this.Enabled = true; }

            if (contents.Files.Count == 0)
            {
                MessageBox.Show("The game's Mods folder only has S3MO's merged files in it, so there's nothing to back up.", "Back up", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var r = MessageBox.Show($"Copy {contents.Files.Count:N0} files ({FormatSize(contents.TotalBytes)}) from the game's Mods folder to a new backup in \"{GameModsBackup.FolderName}\"? ModBuilds is left out.",
                "Back up the game's Mods folder", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
            if (r == DialogResult.OK)
                await BackUpGameModsAsync(gameMods, contents);
        }

        /// <param name="beforeBuild">Part of a build: no "finished" message (the build follows), and problems ask whether to build anyway.</param>
        private async Task<BackupOutcome> BackUpGameModsAsync(string gameMods, GameModsBackup.Contents contents, bool beforeBuild = false)
        {
            if (_isBuilding)
                return BackupOutcome.NotDone;

            // Room for the copy, with a little to spare
            try
            {
                var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(S3moFolder))!);
                long needed = contents.TotalBytes + 200L * 1024 * 1024;
                if (drive.IsReady && drive.AvailableFreeSpace < needed)
                {
                    MessageBox.Show($"The backup needs {FormatSize(needed)} of free space on drive {drive.Name}, but only {FormatSize(drive.AvailableFreeSpace)} is free. Free up some space and try again." +
                        (beforeBuild ? " Nothing was built." : ""), "Not enough space for the backup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return BackupOutcome.NotDone;
                }
            }
            catch (Exception) { /* network drives and the like: just try */ }

            _isBuilding = true; // keeps builds and "needs rebuild" checks from starting, and the window open
            _busyWith = "A backup";
            this.Enabled = false;
            progressLabel.Text = "Backing up the game's Mods folder...";
            progressLabel.Visible = true;
            progressBar.Value = 0;
            progressBar.Maximum = 1000;
            progressBar.Visible = true;
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                GameModsBackup.Result result = await Task.Run(() => GameModsBackup.Create(gameMods, S3moFolder, contents, p => _buildProgress = p));
                Logger.Log($"Backed up {result.FilesCopied:N0} files ({FormatSize(result.BytesCopied)}) from the game's Mods folder to {result.BackupFolder} in {sw.Elapsed.TotalSeconds:0} s.");

                if (result.Failed.Count > 0)
                {
                    foreach (string f in result.Failed)
                        Logger.LogWarning("Not backed up: " + f);
                    string text = $"The backup is in{Environment.NewLine}{result.BackupFolder}{Environment.NewLine}{Environment.NewLine}{result.Failed.Count} file(s) couldn't be copied, for example because they were open in another program:{Environment.NewLine}{Environment.NewLine}" +
                        string.Join(Environment.NewLine, result.Failed.Take(10)) + (result.Failed.Count > 10 ? $"{Environment.NewLine}..." : "");
                    if (!beforeBuild)
                    {
                        MessageBox.Show(text, "Backup finished with problems", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return BackupOutcome.DoneWithProblems;
                    }
                    var go = MessageBox.Show(text + $"{Environment.NewLine}{Environment.NewLine}Build anyway?", "Backup finished with problems", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    return go == DialogResult.Yes ? BackupOutcome.DoneWithProblems : BackupOutcome.NotDone;
                }

                if (!beforeBuild)
                    MessageBox.Show($"Copied {result.FilesCopied:N0} files ({FormatSize(result.BytesCopied)}) to{Environment.NewLine}{result.BackupFolder}{Environment.NewLine}{Environment.NewLine}" +
                        "\"About this backup.txt\" in that folder explains how to put it back.", "Backup finished", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return BackupOutcome.Done;
            }
            catch (Exception ex)
            {
                Logger.LogError("Backup failed: " + ex.Message);
                MessageBox.Show(ex.Message + (beforeBuild ? $"{Environment.NewLine}{Environment.NewLine}Nothing was built." : ""), "Backup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return BackupOutcome.NotDone;
            }
            finally
            {
                _isBuilding = false;
                _busyWith = "A build";
                _buildProgress = null;
                progressLabel.Visible = false;
                progressBar.Visible = false;
                this.Enabled = true;
            }
        }

        /// <summary>Adds "back up" and "open backups" to the folder button's menu.</summary>
        private void SetupBackupMenu()
        {
            var open = new ToolStripMenuItem("Open game Mods backups");
            open.Click += (s, e) =>
            {
                string root = GameModsBackup.BackupRoot(S3moFolder);
                if (Directory.Exists(root))
                    OpenInExplorer(root);
            };
            var backUp = new ToolStripMenuItem("Back up game Mods folder now...");
            backUp.Click += (s, e) => BackUpGameModsNow();

            openFolderContextMenuStrip.Items.Add(new ToolStripSeparator());
            openFolderContextMenuStrip.Items.Add(backUp);
            openFolderContextMenuStrip.Items.Add(open);
            openFolderContextMenuStrip.Opening += (s, e) => open.Enabled = Directory.Exists(GameModsBackup.BackupRoot(S3moFolder));
        }
    }
}
