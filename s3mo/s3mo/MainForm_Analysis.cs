using s3molib;
using System.Diagnostics;

namespace s3mo
{
    // Reading package indexes and finding conflicts. This replaces the three conflict BackgroundWorkers and the
    // MD5 worker: everything is one pass over a dictionary, and unchanged packages come from s3mo.cache.
    partial class MainForm
    {
        private int _analysisGeneration;
        private Task _analysisTask = Task.CompletedTask;
        private bool _scanning;

        /// <summary>True until the newest scan's results are on the models.</summary>
        private bool IsAnalyzing => _scanning;

        private void StartAnalysis()
        {
            int generation = ++_analysisGeneration;
            Task previous = _analysisTask;
            _scanning = true;
            refreshButton.Enabled = false;
            // Which folders are switched off is read here, on the UI thread, while the scan runs in the background.
            _analysisTask = RunAnalysisAsync(generation, _profileModel, _profileModel.SnapshotDisabledFolders(), previous);
        }

        private async Task RunAnalysisAsync(int generation, ProfileModel model, HashSet<string> disabledFolders, Task previous)
        {
            try { await previous; } catch { /* reported by that run */ }

            if (generation != _analysisGeneration)
                return; // a newer refresh is queued; let it do the work

            Stopwatch sw = Stopwatch.StartNew();
            ShowStatus("Scanning packages...");
            AnalysisResult result;
            try
            {
                result = await Task.Run(() => model.Analyze(_packageCache, default, disabledFolders));
            }
            catch (Exception ex)
            {
                Logger.LogError("Scanning packages failed: " + ex.Message);
                if (generation == _analysisGeneration)
                {
                    _scanning = false;
                    refreshButton.Enabled = true;
                    HideStatus();
                }
                return;
            }

            if (generation != _analysisGeneration || IsDisposed)
                return;

            model.ApplyAnalysis(result);
            _folderFlagCache.Clear();
            _scanning = false;
            refreshButton.Enabled = true;
            HideStatus();
            modListView.Invalidate();
            fileListView.Invalidate();
            RefreshSelectedModHighlight();

            string failed = result.FailedCount > 0 ? $" {result.FailedCount} package(s) couldn't be read and will be left out of builds (see errors above)." : "";
            Logger.Log($"Scanned {result.PackageCount} packages ({result.CacheHits} unchanged, read from cache) and checked {result.Conflicts.ResourceCount:N0} resources for conflicts in {sw.ElapsedMilliseconds} ms.{failed}");
            int modsWithConflicts = model.ModModels.Count(m => m.ConflictingMods.Count > 0);
            int modsWithInternal = model.ModModels.Count(m => m.HasPackageConflictsInside);
            Logger.Log(result.Conflicts.ConflictingKeyCount == 0
                ? "No conflicts found: no two packages contain the same resource."
                : $"{result.Conflicts.ConflictingKeyCount:N0} resources exist in more than one package: {modsWithConflicts} mod(s) overlap with other mods, {modsWithInternal} mod(s) have packages that overlap each other.");

            ValidateModBuild();
        }

        /// <summary>Waits for a running scan (the UI stays responsive) before something that needs conflict data.</summary>
        private async Task WaitForAnalysisAsync()
        {
            if (!IsAnalyzing)
                return;
            Logger.Log("Waiting for the package scan to finish...");
            while (IsAnalyzing)
            {
                try { await _analysisTask; } catch { }
                if (IsAnalyzing)
                    await Task.Delay(50); // a newer scan was queued while waiting
            }
        }

        private void ShowStatus(string text)
        {
            if (_isBuilding)
                return;
            progressLabel.Text = text;
            progressLabel.Visible = true;
        }

        private void HideStatus()
        {
            if (_isBuilding)
                return;
            progressLabel.Visible = false;
        }
    }
}
