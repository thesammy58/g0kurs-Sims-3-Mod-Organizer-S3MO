using s3molib;
using System.Diagnostics;

namespace s3mo
{
    // Building ModBuilds and the "needs rebuild" indicator.
    //
    // The build compares the enabled packages with the state saved by the previous build and only writes what changed
    // (see s3molib/ModBuilder.cs). The "needs rebuild" warning is the same comparison without writing anything, so it is
    // exact: reordering mods that don't conflict no longer asks for a rebuild, and editing a package does.
    partial class MainForm
    {
        private bool _isBuilding;
        private int _validateGeneration;
        private Task _validationTask = Task.CompletedTask;
        private volatile BuildProgress? _buildProgress;

        private void SetupBuildButtonMenu()
        {
            var menu = new ContextMenuStrip(components);
            var full = new ToolStripMenuItem("Full rebuild (rewrite everything, removes unused space)");
            full.Click += (s, e) => BuildMods(forceFull: true);
            menu.Items.Add(full);
            buildButton.ContextMenuStrip = menu;
            toolTip.SetToolTip(buildButton, "Build Merge (right-click or Shift+click for a full rebuild)");
        }

        private bool TryGetBuildFolders(bool showErrors, out string sims3ModsFolder, out string buildFolder)
        {
            sims3ModsFolder = buildFolder = string.Empty;
            if (!Settings.TryGetSettings("DocumentsFolderPath", out string sims3FolderPath) || string.IsNullOrWhiteSpace(sims3FolderPath))
            {
                if (showErrors)
                    MessageBox.Show("No Sims 3 Documents folder path is entered in settings, aborting merge process.", "Invalid Folder Path", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            sims3ModsFolder = Path.Combine(sims3FolderPath, "Mods");
            buildFolder = Path.Combine(sims3ModsFolder, "ModBuilds");
            return true;
        }

        private static long GetBuildSizeLimit()
        {
            long megabytes = 1000;
            if (Settings.TryGetSettings("BuildSizeLimit", out string obj) && long.TryParse(obj, out long v) && v > 0)
                megabytes = v;
            return megabytes * 1_000_000;
        }

        /// <summary>Snapshot of the enabled packages in merge order (taken on the UI thread).</summary>
        private List<(string path, string id)> GetBuildList(out List<PackageModel> unreadable)
        {
            var list = new List<(string, string)>();
            foreach (PackageModel p in _profileModel.GetPackagesInBuildOrder(out unreadable))
                list.Add((p.FilePath, p.ModModel.Name + "\\" + p.Name));
            return list;
        }

        /// <summary>Background thread: turns the snapshot into build sources (re-reading any package that changed since the scan).</summary>
        private List<BuildSource> MakeSources(List<(string path, string id)> list, List<string> problems)
        {
            var sources = new List<BuildSource>(list.Count);
            foreach (var (path, id) in list)
            {
                try { sources.Add(_packageCache.GetBuildSource(path, id)); }
                catch (Exception ex) { problems?.Add($"{id}: {ex.Message}"); }
            }
            return sources;
        }

        // ------------------------------------------------------------------ "needs rebuild"

        private async void ValidateModBuild()
        {
            if (IsAnalyzing || _isBuilding || _profileModel == null)
                return; // called again when those finish

            int generation = ++_validateGeneration;

            if (!TryGetBuildFolders(false, out _, out string buildFolder))
            {
                warningButton.Enabled = true;
                toolTip.SetToolTip(warningButton, "Set the Sims 3 Documents folder in settings");
                return;
            }

            var list = GetBuildList(out _);
            long limit = GetBuildSizeLimit();
            BuildPlan plan;
            try
            {
                Task<BuildPlan> planning = Task.Run(() => ModBuilder.Plan(buildFolder, MakeSources(list, null), new BuildOptions { SizeLimit = limit }));
                _validationTask = planning;
                plan = await planning;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Couldn't check the build: " + ex.Message);
                return;
            }

            if (generation != _validateGeneration || IsDisposed)
                return;

            warningButton.Enabled = !plan.UpToDate;
            toolTip.SetToolTip(warningButton, plan.UpToDate ? "Build is up to date" : "Needs rebuild: " + DescribePlan(plan));
        }

        private static string DescribePlan(BuildPlan plan)
        {
            if (plan.FullRebuild)
                return $"full rebuild ({plan.Reason})";
            var parts = new List<string>();
            if (plan.Added > 0) parts.Add($"{plan.Added:N0} new");
            if (plan.Replaced > 0) parts.Add($"{plan.Replaced:N0} changed");
            if (plan.Removed > 0) parts.Add($"{plan.Removed:N0} removed");
            return string.Join(", ", parts) + " resource(s)";
        }

        // ------------------------------------------------------------------ build

        private async void BuildMods(bool forceFull)
        {
            if (_isBuilding)
                return;
            if (!TryGetBuildFolders(true, out string sims3ModsFolderPath, out string buildFolder))
                return;

            _isBuilding = true; // also keeps new "needs rebuild" checks from starting
            this.Enabled = false;
            await WaitForAnalysisAsync();
            // A "needs rebuild" check may still be reading ModBuilds; let it finish before files are replaced.
            try { await _validationTask; } catch { }

            Stopwatch stopwatch = Stopwatch.StartNew();
            Logger.Log(forceFull ? "Full rebuild started" : "Build started");

            try
            {
                Directory.CreateDirectory(sims3ModsFolderPath);
                WriteResourceCfg(sims3ModsFolderPath);

                var list = GetBuildList(out List<PackageModel> unreadable);
                var problems = new List<string>();
                foreach (PackageModel p in unreadable)
                    problems.Add($"{p.ModModel.Name}\\{p.Name}: {p.LoadError}");

                var options = new BuildOptions
                {
                    SizeLimit = GetBuildSizeLimit(),
                    ForceFullRebuild = forceFull,
                    Progress = p => _buildProgress = p,
                };

                progressLabel.Text = "Preparing build...";
                progressLabel.Visible = true;
                progressBar.Value = 0;
                progressBar.Maximum = 1000;
                progressBar.Visible = true;

                BuildResult result = await Task.Run(() => ModBuilder.Build(buildFolder, MakeSources(list, problems), options));

                BuildPlan plan = result.Plan;
                string what = plan.UpToDate ? "Build was already up to date"
                    : plan.FullRebuild ? $"Full build ({plan.Reason}): wrote {result.ResourcesWritten:N0} resources"
                    : $"Updated build: {DescribePlan(plan)}, kept {plan.Unchanged:N0}";
                Logger.Log($"{what} - {result.BytesWritten / 1048576.0:0.0} MB written, {result.OutputFileCount} merged file(s), {list.Count} packages, {stopwatch.ElapsedMilliseconds} ms.");

                if (problems.Count > 0)
                {
                    foreach (string p in problems)
                        Logger.LogWarning("Left out of the build: " + p);
                    MessageBox.Show($"{problems.Count} package(s) couldn't be read and were left out of the build:{Environment.NewLine}{Environment.NewLine}" +
                        string.Join(Environment.NewLine, problems.Take(15)) + (problems.Count > 15 ? $"{Environment.NewLine}..." : ""),
                        "Some packages were skipped", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception ex)
            {
                Logger.LogError("Build failed: " + ex.Message);
                MessageBox.Show(ex.Message, "Build failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _isBuilding = false;
                _buildProgress = null;
                progressLabel.Visible = false;
                progressBar.Visible = false;
                this.Enabled = true;
                ValidateModBuild();
            }
        }

        private static void WriteResourceCfg(string sims3ModsFolderPath)
        {
            string buildPriority = Settings.TryGetSettings("BuildPriority", out string obj) ? obj : "495";

            List<string> lines = new List<string>
            {
                $"Priority {buildPriority}",
                "PackedFile \"ModBuilds/*.package\"",
                "",
            };

            if (Settings.TryGetSettings("BuildResourceCFG", out string resourcecfg))
                lines.AddRange(resourcecfg.Split(" \\n "));

            File.WriteAllLines(Path.Combine(sims3ModsFolderPath, "Resource.cfg"), lines);
        }

        /// <summary>Called by the UI timer; shows the progress reported by the build thread.</summary>
        private void UpdateBuildProgress()
        {
            BuildProgress? p = _buildProgress;
            if (!_isBuilding || p == null)
                return;
            progressLabel.Text = p.Message;
            if (p.BytesTotal > 0)
                progressBar.Value = (int)Math.Clamp(p.BytesDone * 1000 / p.BytesTotal, 0, 1000);
        }
    }
}
