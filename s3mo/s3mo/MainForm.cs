using ns;
using s3molib;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace s3mo
{
    public partial class MainForm : Form
    {
        private ProfileModel _profileModel = null!;
        private string _currentProfile = string.Empty;

        private readonly PackageCache _packageCache;
        private readonly System.Windows.Forms.Timer _uiTimer = new System.Windows.Forms.Timer();
        private readonly Dictionary<Button, Image> _toolbarIcons = new Dictionary<Button, Image>();

        public MainForm()
        {
            InitializeComponent();
            this.Text = "Sims 3 Mod Organizer v" + Program.Version;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string profilePath = Path.Combine(baseDir, "Profiles");
            if (!Directory.Exists(profilePath))
            {
                string path = Path.Combine(profilePath, "Default");
                Directory.CreateDirectory(path);
                _currentProfile = "Default";
            }

            if (Settings.TryGetSettings("Profile", out string obj) && obj.Length > 0)
                _currentProfile = (string)obj;
            else
                Settings.SetSettings("Profile", _currentProfile = "Default");

            string modsPath = Path.Combine(baseDir, "Mods");
            if (!Directory.Exists(modsPath))
                Directory.CreateDirectory(modsPath);

            _packageCache = new PackageCache(Path.Combine(baseDir, "s3mo.cache"), modsPath);

            // Log lines can come from background threads; they're queued and shown by this timer.
            _uiTimer.Interval = 100;
            _uiTimer.Tick += (s, e) => { FlushLog(); UpdateBuildProgress(); };
            Logger.InfoLoggedEvent += EnqueueLog;
            Logger.WarningLoggedEvent += EnqueueLog;
            Logger.ErrorLoggedEvent += EnqueueLog;

            helpButton.Image = Icons.Help;
            foreach (Button b in new[] { aboutButton, helpButton, installButton, optionsButton, profileButton, openFolderButton, clearCacheButton, refreshButton, buildButton, warningButton, playButton })
                if (b.Image != null)
                    _toolbarIcons[b] = b.Image;

            SetupBuildButtonMenu();
            SetupModListContextMenu();
            SetupBackupMenu();
            modListView.ExpandRequested += modListView_ExpandRequested;
            LoadExpandedRows();
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (_isBuilding)
            {
                MessageBox.Show($"{_busyWith} is still running. Please wait for it to finish.", "Please wait", MessageBoxButtons.OK, MessageBoxIcon.Information);
                e.Cancel = true;
                return;
            }

            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.SetSettings("WindowX", bounds.X);
            Settings.SetSettings("WindowY", bounds.Y);
            Settings.SetSettings("WindowWidth", bounds.Width);
            Settings.SetSettings("WindowHeight", bounds.Height);
            Settings.SetSettings("WindowMaximized", WindowState == FormWindowState.Maximized);

            Settings.SetSettings("WindowSplitContainer1SplitterDistance", splitContainer1.SplitterDistance);
            Settings.SetSettings("WindowSplitContainer2SplitterDistance", splitContainer2.SplitterDistance);
            Settings.SetSettings("WindowModListViewModHeaderWidth", modHeader.Width);
            Settings.SetSettings("WindowModListViewFlagHeaderWidth", flagHeader.Width);
            Settings.SetSettings("WindowModListViewPriorityHeaderWidth", priorityHeader.Width);
            Settings.SetSettings("WindowFileListViewFileHeaderWidth", fileHeader.Width);
            Settings.SetSettings("WindowFileListViewFlagHeaderWidth", fileFlagHeader.Width);
            Dpi.SaveLayoutDpi("Window", this);
            SaveExpandedRows();

            Logger.InfoLoggedEvent -= EnqueueLog;
            Logger.WarningLoggedEvent -= EnqueueLog;
            Logger.ErrorLoggedEvent -= EnqueueLog;
            _uiTimer.Stop();
        }

        private void MainForm_Load(object sender, EventArgs e)
        {
            // Saved sizes are scaled from the DPI they were saved at to the current one.
            Dpi.RestoreBounds(this, "Window", "WindowX", "WindowY", "WindowWidth", "WindowHeight");
            if (Settings.TryGetSettings("WindowMaximized", out string obj) && bool.TryParse(obj, out bool b))
                WindowState = b ? FormWindowState.Maximized : FormWindowState.Normal;
            Dpi.RestoreSplitter("Window", "WindowSplitContainer1SplitterDistance", splitContainer1);
            Dpi.RestoreSplitter("Window", "WindowSplitContainer2SplitterDistance", splitContainer2);
            Dpi.RestoreColumnWidth("Window", "WindowModListViewModHeaderWidth", modHeader, this);
            Dpi.RestoreColumnWidth("Window", "WindowModListViewFlagHeaderWidth", flagHeader, this);
            Dpi.RestoreColumnWidth("Window", "WindowModListViewPriorityHeaderWidth", priorityHeader, this);
            Dpi.RestoreColumnWidth("Window", "WindowFileListViewFileHeaderWidth", fileHeader, this);
            Dpi.RestoreColumnWidth("Window", "WindowFileListViewFlagHeaderWidth", fileFlagHeader, this);
            Dpi.KeepColumnsScaled(this, flagHeader, priorityHeader, fileFlagHeader);

            // The lists fill their panes; the name columns take whatever width the other columns leave.
            Dpi.FillWithColumns(modListView, modHeader);
            Dpi.FillWithColumns(fileListView, fileHeader);
            this.DpiChanged += (s, e) => BeginInvoke(() => { ApplyToolbarIcons(); FitNameColumns(); });

            modListView.SetDoubleBuffered();
            fileListView.SetDoubleBuffered();
            ApplyToolbarIcons();

            _uiTimer.Start();
            ResetModel();
        }

        /// <summary>Toolbar icons are 32px bitmaps; scale them with the window so they don't look tiny at 150-200%.</summary>
        private void ApplyToolbarIcons()
        {
            int size = LogicalToDeviceUnits(32);
            foreach (var kv in _toolbarIcons)
                kv.Key.Image = Dpi.Icon(kv.Value, size);
        }

        /// <summary>The name columns take the space the other columns leave.</summary>
        private void FitNameColumns()
        {
            Dpi.FitColumns(modListView, modHeader);
            Dpi.FitColumns(fileListView, fileHeader);
        }

        // ------------------------------------------------------------------ log

        private readonly ConcurrentQueue<string> _pendingLog = new ConcurrentQueue<string>();
        private const int MaxLogLines = 5000;

        private void EnqueueLog(string s) => _pendingLog.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + s);

        private void FlushLog()
        {
            if (_pendingLog.IsEmpty || logListBox.IsDisposed)
                return;

            logListBox.BeginUpdate();
            try
            {
                while (_pendingLog.TryDequeue(out string? line))
                    logListBox.Items.Add(line);
                while (logListBox.Items.Count > MaxLogLines)
                    logListBox.Items.RemoveAt(0);
                logListBox.TopIndex = logListBox.Items.Count - 1;
            }
            finally
            {
                logListBox.EndUpdate();
            }
        }

        // ------------------------------------------------------------------ model

        private void ResetModel()
        {
            _profileModel = new ProfileModel();
            _profileModel.Setup(_currentProfile);
            RefreshListViews(false);
            StartAnalysis();
        }

        private bool _populatingModList;

        private void RefreshListViews(bool setupModel = true)
        {
            if (setupModel)
                _profileModel.Setup(_currentProfile);

            _folderFlagCache.Clear();
            RebuildModRows();

            string[] profiles = Directory.GetDirectories(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Profiles"));
            Array.Sort(profiles, new NumericComparer());
            profileComboBox.Items.Clear();

            foreach (string path in profiles)
                profileComboBox.Items.Add(Path.GetFileName(path));

            profileComboBox.SelectedItem = _currentProfile;
        }

        /// <summary>Stores the mod order shown in the list (folder rows don't count) and renumbers the enabled mods.</summary>
        private void UpdateModModelPriority()
        {
            int realIndex = 0; // Used so that the order is preserved when saved in modlist.txt
            int displayIndex = 0;

            foreach (ListViewItem item in modListView.Items)
            {
                if (RowOf(item) is not ModRow row || !row.IsMod)
                    continue;
                ModModel mod = row.Mod;
                mod.Update(mod.Name, mod.Enabled, realIndex);

                if (mod.Enabled)
                    item.SubItems[2].Text = displayIndex++.ToString();
                else
                    item.SubItems[2].Text = string.Empty;

                realIndex++;
            }
            _profileModel.Save(_currentProfile);
            _folderFlagCache.Clear();
            modListView.Invalidate();

            ValidateModBuild();
        }

        private void profileComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            string profileName = (string)profileComboBox.SelectedItem;
            if (profileName == null || _currentProfile.Equals(profileName))
                return;

            _currentProfile = profileName;
            Settings.SetSettings("Profile", _currentProfile);
            RefreshListViews();
            StartAnalysis(); // each profile has its own switched-off folders, so conflicts can differ

        }

        private void aboutButton_Click(object sender, EventArgs e)
        {
            AboutForm form = new AboutForm();
            form.FormClosed += (s, e) => this.Enabled = true;
            this.Enabled = false;
            form.Show();
        }

        private HelpForm? _helpForm;

        /// <summary>Opens the quick guide next to the main window. It doesn't block the main window, and only one is open at a time.</summary>
        private void helpButton_Click(object sender, EventArgs e)
        {
            if (_helpForm != null && !_helpForm.IsDisposed)
            {
                if (_helpForm.WindowState == FormWindowState.Minimized)
                    _helpForm.WindowState = FormWindowState.Normal;
                _helpForm.Activate();
                return;
            }

            var icons = new Dictionary<string, Image>();
            void Add(string name, Button b) { if (_toolbarIcons.TryGetValue(b, out Image? img)) icons[name] = img; }
            Add("about", aboutButton); Add("install", installButton); Add("options", optionsButton); Add("profile", profileButton);
            Add("openFolder", openFolderButton); Add("clearCache", clearCacheButton); Add("refresh", refreshButton);
            Add("build", buildButton); Add("warning", warningButton); Add("play", playButton);

            _helpForm = new HelpForm(icons, Icons.Help) { Icon = this.Icon };
            _helpForm.FormClosed += (s, a) => _helpForm = null;

            // Beside the main window when there's room on its screen, otherwise centred over it
            Rectangle area = Screen.FromControl(this).WorkingArea;
            Size size = _helpForm.Size;
            Point at = Bounds.Right + size.Width <= area.Right ? new Point(Bounds.Right, Bounds.Top)
                     : Bounds.Left - size.Width >= area.Left ? new Point(Bounds.Left - size.Width, Bounds.Top)
                     : new Point(Bounds.Left + (Width - size.Width) / 2, Bounds.Top + (Height - size.Height) / 2);
            at.Y = Math.Max(area.Top, Math.Min(at.Y, area.Bottom - size.Height));
            _helpForm.Location = at;
            _helpForm.Show(this);
        }

        private void installButton_Click(object sender, EventArgs e)
        {
            if (openFileDialog.ShowDialog() != DialogResult.OK)
                return;

            this.Enabled = false;

            string tempPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "temp");
            if (!Directory.Exists(tempPath))
                Directory.CreateDirectory(tempPath);

            string[] paths = openFileDialog.FileNames;

            foreach (string path in paths)
            {
                try
                {
                    string extension = Path.GetExtension(path);

                    if (extension.Equals(".sims3pack", StringComparison.OrdinalIgnoreCase))
                        Sims3Pack.Unpack(path, tempPath);

                    else if (extension.Equals(".package", StringComparison.OrdinalIgnoreCase))
                        File.Copy(path, Path.Combine(tempPath, $"{Path.GetFileName(path)}"), true);

                    else if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        // Keep the folders inside the zip; they become subfolders of the mod.
                        ZipFile.ExtractToDirectory(path, tempPath, overwriteFiles: true);

                        foreach (string p in Directory.GetFiles(tempPath, "*.sims3pack", new EnumerationOptions { RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive }))
                        {
                            Sims3Pack.Unpack(p, Path.GetDirectoryName(p)!);
                            File.Delete(p);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Couldn't install file at '{path}' - {ex.Message}");
                }
            }

            InstallForm form = new InstallForm();
            form.FormClosed += (s, e) =>
            {
                this.Enabled = true;
                ResetModel();
            };
            form.Show();
        }

        private void optionsButton_Click(object sender, EventArgs e)
        {
            SettingsForm settingsForm = new SettingsForm();
            settingsForm.FormClosed += (s, e) => { this.Enabled = true; ValidateModBuild(); };
            this.Enabled = false;
            settingsForm.Show();
        }

        private void profileButton_Click(object sender, EventArgs e)
        {
            ProfileForm form = new ProfileForm(_currentProfile);
            this.Enabled = false;
            form.FormClosed += (s, e) =>
            {
                RefreshListViews();
                this.Enabled = true;
                StartAnalysis();
            };
            form.Show();
        }

        private void openFolderButton_MouseClick(object sender, MouseEventArgs e)
        {
            openFolderContextMenuStrip.Show(Cursor.Position);
        }

        #region openFolderToolStip

        private static void OpenInExplorer(string path)
        {
            ProcessStartInfo startInfo = new ProcessStartInfo();
            startInfo.Arguments = $"\"{path}\"";
            startInfo.FileName = "explorer.exe";
            try
            { Process.Start(startInfo); }
            catch (Exception ex)
            { MessageBox.Show(ex.Message); }
        }

        private void openGameFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Settings.TryGetSettings("GameExecutablePath", out string path) || path.Length == 0)
            {
                MessageBox.Show("No path entered for Sims 3 executable in settings");
                return;
            }
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                MessageBox.Show($"Folder at '{path}' does not exists");
                return;
            }

            OpenInExplorer(Path.GetDirectoryName(path)!);
        }

        private void openGameDocumentsFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            if (!Settings.TryGetSettings("DocumentsFolderPath", out string path) || path.Length == 0)
            {
                MessageBox.Show("No path entered for Sims 3 documents folder in settings");
                return;
            }
            if (!Directory.Exists(path))
            {
                MessageBox.Show($"Folder at '{path}' does not exists");
                return;
            }

            OpenInExplorer(path);
        }

        private void openS3MOFolderToolStripMenuItem_Click(object sender, EventArgs e)
        {
            OpenInExplorer(AppDomain.CurrentDomain.BaseDirectory);
        }

        #endregion

        private void clearCacheButton_Click(object sender, EventArgs e)
        {
            if (!Settings.TryGetSettings("DocumentsFolderPath", out string path) || path.Length == 0)
            {
                MessageBox.Show("No path entered for Sims 3 documents folder in settings");
                return;
            }
            if (!Directory.Exists(path))
            {
                MessageBox.Show($"Folder at '{path}' does not exists");
                return;
            }
            //if (!Path.GetFileName(path).Equals("The Sims 3"))  Commented so it works for foreign languages
            //{
            //    MessageBox.Show($"Folder at '{path}' is not a Sims 3 folder");
            //    return;
            //}

            List<string> deleteFileList = new List<string>();
            List<string> deleteFolderList = new List<string>();

            bool On(string key) => Settings.TryGetSettings(key, out string v) && bool.TryParse(v, out bool on) && on;
            IEnumerable<string> FilesIn(string folder, string pattern) => Directory.Exists(folder) ? Directory.GetFiles(folder, pattern) : Array.Empty<string>();

            if (On("ClearCASPartCache"))
                deleteFileList.Add(Path.Combine(path, "CASPartCache.package"));
            if (On("ClearCompositorCache"))
                deleteFileList.Add(Path.Combine(path, "compositorCache.package"));
            if (On("ClearScriptCache"))
                deleteFileList.Add(Path.Combine(path, "scriptCache.package"));
            if (On("ClearSimCompositorCache"))
                deleteFileList.Add(Path.Combine(path, "simCompositorCache.package"));
            if (On("ClearSocialCache"))
                deleteFileList.Add(Path.Combine(path, "socialCache.package"));
            if (On("ClearDCCacheDCC"))
                deleteFileList.Add(Path.Combine(path, "DCCache", "dcc.ent"));
            if (On("ClearDCCacheMissingdeps"))
                deleteFileList.Add(Path.Combine(path, "DCCache", "missingdeps.idx"));
            if (On("ClearDownloadsBin"))
                deleteFileList.AddRange(FilesIn(Path.Combine(path, "Downloads"), "*.bin"));
            if (On("ClearFeaturedItems"))
                deleteFolderList.Add(Path.Combine(path, "FeaturedItems"));
            if (On("ClearIGACache"))
                deleteFolderList.Add(Path.Combine(path, "IGACache"));
            if (On("ClearSavedSimsDownloadedSims"))
                deleteFileList.Add(Path.Combine(path, "SavedSims", "downloadedsims.index"));
            if (On("ClearSigsCacheBin"))
                deleteFileList.AddRange(FilesIn(Path.Combine(path, "SigsCache"), "*.bin"));
            if (On("ClearThumbnails"))
                deleteFolderList.Add(Path.Combine(path, "Thumbnails"));
            if (On("ClearWorldCaches"))
                deleteFolderList.Add(Path.Combine(path, "WorldCaches"));
            if (On("ClearNRaasScriptErrors"))
                deleteFileList.AddRange(FilesIn(path, "ScriptError*.xml"));

            foreach (string p in deleteFileList)
            {
                if (File.Exists(p))
                {
                    Logger.Log("Deleting File " + Path.GetFileName(p));
                    try { File.Delete(p); }
                    catch (Exception ex) { Logger.LogError($"Couldn't delete {p}: {ex.Message}"); }
                }
            }
            foreach (string p in deleteFolderList)
            {
                if (Directory.Exists(p))
                {
                    Logger.Log("Deleting Folder " + Path.GetFileName(p));
                    try { Directory.Delete(p, true); }
                    catch (Exception ex) { Logger.LogError($"Couldn't delete {p}: {ex.Message}"); }
                }
            }
        }

        private void refreshButton_Click(object sender, EventArgs e) => ResetModel();

        private void buildButton_Click(object sender, EventArgs e) => BuildMods(forceFull: (ModifierKeys & Keys.Shift) != 0);

        private void playButton_Click(object sender, EventArgs e)
        {
            if (!Settings.TryGetSettings("GameExecutablePath", out string path) || path.Length == 0)
            {
                MessageBox.Show($"No path is entered for game executable in settings");
                return;
            }
            if (!File.Exists(path))
            {
                MessageBox.Show($"'{path}' is not a valid path to the game executable.");
                return;
            }

            if (warningButton.Enabled)
            {
                var r = MessageBox.Show("Start game without rebuilding?", "Build Unmatch", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r == DialogResult.No)
                    return;
            }

            Process process = new Process();
            process.StartInfo.FileName = path;
            process.StartInfo.WorkingDirectory = Path.GetDirectoryName(path);
            try
            { process.Start(); }
            catch (Exception ex)
            { MessageBox.Show(ex.Message, "Couldn't start game process", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }
    }
}
