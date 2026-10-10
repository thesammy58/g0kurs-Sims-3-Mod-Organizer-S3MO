using ns;
using s3molib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using KUtility;

namespace s3mo
{
    public partial class ModForm : Form
    {
        ModModel _modModel = null!;

        PackageModel _packageModel = null!;
        Package _package = null!;

        List<ResourceConflictModel> _losingResourceModels = new List<ResourceConflictModel>();
        List<ResourceConflictModel> _winningResourceModels = new List<ResourceConflictModel>();

        public ModForm(ModModel modModel)
        {
            InitializeComponent();
            _modModel = modModel;
        }

        public ModForm(PackageModel packageModel)
        {
            InitializeComponent();
            _packageModel = packageModel;
            _package = _packageModel.Package;
        }

        private void ModForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (resourceDataBackgroundWorker.IsBusy)
                resourceDataBackgroundWorker.CancelAsync();

            Rectangle bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            Settings.SetSettings("ModFormX", bounds.X);
            Settings.SetSettings("ModFormY", bounds.Y);
            Settings.SetSettings("ModFormWidth", bounds.Width);
            Settings.SetSettings("ModFormHeight", bounds.Height);
            Dpi.SaveLayoutDpi("ModForm", this);
            Settings.SetSettings("ModFormMaximized", (WindowState == FormWindowState.Maximized ? true : false));
            Settings.SetSettings("ModFormTab", tabControl.TabPages.IndexOf(tabControl.SelectedTab));

            Settings.SetSettings("ModFormPackageSplitContainerSplitterDistance", packageSplitContainer.SplitterDistance);
            Settings.SetSettings("ModFormPackageHeaderWidth", packageHeader.Width);
            Settings.SetSettings("ModFormPackageFlagHeaderWidth", packageFlagHeader.Width);
            Settings.SetSettings("ModFormResourceSplitContainerSplitterDistance", resourceSplitContainer.SplitterDistance);
            Settings.SetSettings("ModFormResourceNameHeaderWidth", resourceNameHeader.Width);
            Settings.SetSettings("ModFormResourceTagHeaderWidth", resourceTagHeader.Width);
            Settings.SetSettings("ModFormResourceTypeHeaderWidth", resourceTypeHeader.Width);
            Settings.SetSettings("ModFormResourceGroupHeaderWidth", resourceGroupHeader.Width);
            Settings.SetSettings("ModFormResourceInstanceHeaderWidth", resourceInstanceHeader.Width);

            Settings.SetSettings("ModFormLosingTagHeaderWidth", losingTagHeader.Width);
            Settings.SetSettings("ModFormLosingResourceHeaderWidth", losingResourceHeader.Width);
            Settings.SetSettings("ModFormLosingPackageHeader1Width", losingPackageHeader1.Width);
            Settings.SetSettings("ModFormLosingBlankHeaderWidth", losingBlankHeader.Width);
            Settings.SetSettings("ModFormLosingModHeaderWidth", losingModHeader.Width);
            Settings.SetSettings("ModFormLosingPackageHeader2Width", losingPackageHeader2.Width);

            Settings.SetSettings("ModFormWinningTagHeaderWidth", winningTagHeader.Width);
            Settings.SetSettings("ModFormWinningResourceHeaderWidth", winningResourceHeader.Width);
            Settings.SetSettings("ModFormWinningPackageHeader1Width", winningPackageHeader1.Width);
            Settings.SetSettings("ModFormWinningBlankHeaderWidth", winningBlankHeader.Width);
            Settings.SetSettings("ModFormWinningModHeaderWidth", winningModHeader.Width);
            Settings.SetSettings("ModFormWinningPackageHeader2Width", winningPackageHeader2.Width);
        }

        private void ModForm_Load(object sender, EventArgs e)
        {
            Dpi.RestoreBounds(this, "ModForm", "ModFormX", "ModFormY", "ModFormWidth", "ModFormHeight");
            if (Settings.TryGetSettings("ModFormMaximized", out string obj) && bool.TryParse(obj, out bool b))
                WindowState = b ? FormWindowState.Maximized : FormWindowState.Normal;
            if (Settings.TryGetSettings("ModFormTab", out obj) && int.TryParse(obj, out int i) && i >= 0 && i < tabControl.TabPages.Count)
                tabControl.SelectedTab = tabControl.TabPages[i];

            foreach (var (key, column) in new (string, ColumnHeader)[]
            {
                ("ModFormResourceNameHeaderWidth", resourceNameHeader), ("ModFormResourceTagHeaderWidth", resourceTagHeader),
                ("ModFormResourceTypeHeaderWidth", resourceTypeHeader), ("ModFormResourceGroupHeaderWidth", resourceGroupHeader),
                ("ModFormResourceInstanceHeaderWidth", resourceInstanceHeader),
                ("ModFormLosingTagHeaderWidth", losingTagHeader), ("ModFormLosingResourceHeaderWidth", losingResourceHeader),
                ("ModFormLosingPackageHeader1Width", losingPackageHeader1), ("ModFormLosingBlankHeaderWidth", losingBlankHeader),
                ("ModFormLosingModHeaderWidth", losingModHeader), ("ModFormLosingPackageHeader2Width", losingPackageHeader2),
                ("ModFormWinningTagHeaderWidth", winningTagHeader), ("ModFormWinningResourceHeaderWidth", winningResourceHeader),
                ("ModFormWinningPackageHeader1Width", winningPackageHeader1), ("ModFormWinningBlankHeaderWidth", winningBlankHeader),
                ("ModFormWinningModHeaderWidth", winningModHeader), ("ModFormWinningPackageHeader2Width", winningPackageHeader2),
            })
                Dpi.RestoreColumnWidth("ModForm", key, column, this);
            Dpi.KeepColumnsScaled(this, resourceNameHeader, resourceTagHeader, resourceTypeHeader, resourceGroupHeader, resourceInstanceHeader,
                losingTagHeader, losingResourceHeader, losingBlankHeader, losingModHeader,
                winningTagHeader, winningResourceHeader, winningBlankHeader, winningModHeader, packageFlagHeader);

            // Lists fill their boxes; these columns take the remaining width.
            Dpi.FillWithColumns(losingListView, losingPackageHeader1, losingPackageHeader2);
            Dpi.FillWithColumns(winningListView, winningPackageHeader1, winningPackageHeader2);
            Dpi.FillWithColumns(resourceListView, resourceNameHeader);
            Dpi.FillWithColumns(packageListView, packageHeader);

            losingListView.SetDoubleBuffered();
            winningListView.SetDoubleBuffered();
            packageListView.SetDoubleBuffered();
            resourceListView.SetDoubleBuffered();

            if (_modModel != null)
            {
                Text = _modModel.Name;
                CalculateConflictData();
                PopulateModContent();

            }
            else if (_packageModel != null)
            {
                Text = _packageModel.Name;
                CalculateConflictDataForPackageModel();
                PopulateResourceContent();
            }
            else
                throw new Exception("Unknown model to display");
        }

        #region ConflictListViews

        private void CalculateConflictData()
        {
            if (!_modModel.Enabled)
                return;

            List<PackageModel> packageModels = _modModel.PackageModels;

            foreach (PackageModel packageModel in packageModels)
            {
                List<string> conflictingResources = packageModel.GetConflictingResources(); // It should be already sorted in priority

                if (conflictingResources.Count == 0)
                    continue;

                foreach (string resource in conflictingResources)
                {
                    List<PackageModel> conflictingPackageModels = packageModel.GetConflictingPackageModelsByResource(resource);

                    foreach (PackageModel conflictingPackageModel in conflictingPackageModels)
                    {
                        if (!conflictingPackageModel.ModModel.Enabled)
                            continue;

                        ResourceConflictModel resourceModel = new ResourceConflictModel(resource, packageModel.Name, conflictingPackageModel.ModModel.Name, conflictingPackageModel.Name);

                        if (packageModel.ModModel.Priority > conflictingPackageModel.ModModel.Priority)
                            _losingResourceModels.Add(resourceModel);
                        else if (packageModel.ModModel.Priority < conflictingPackageModel.ModModel.Priority)
                            _winningResourceModels.Add(resourceModel);
                        else
                            throw new Exception($"Conflict status unknown between {packageModel.Name} & {conflictingPackageModel.Name}");
                    }
                }
            }
            PopulateListView();
        }

        private void CalculateConflictDataForPackageModel()
        {
            List<PackageModel> conflictingPackageModels = _packageModel.ConflictingPackageModelsInModModel;

            foreach (PackageModel conflictingPackageModel in conflictingPackageModels)
            {
                int r = StringLogicalComparer.Compare(_packageModel.Name, conflictingPackageModel.Name);

                if (!_packageModel.Package.IsConflictingWith(conflictingPackageModel.Package, out var resourceEntries, out _, false))
                    continue;   // This shouldn't happen

                foreach (ResourceEntry resourceEntry in resourceEntries)
                {
                    string resource = resourceEntry.ToString();

                    ResourceConflictModel resourceModel = new ResourceConflictModel(resource, _packageModel.Name, "", conflictingPackageModel.Name);

                    if (r < 0)
                        _winningResourceModels.Add(resourceModel);
                    else if (r > 0)
                        _losingResourceModels.Add(resourceModel);
                    else
                        throw new Exception($"Conflict status unknown between {_packageModel.Name} & {conflictingPackageModel.Name}");
                }
            }

            losingGroupBox.Text = "The list below shows the resources in other packages in the same mod that will be replaced by this package";
            winningGroupBox.Text = "The list below shows the resources in other packages in the same mod that will replace this package";
            PopulateListView();
        }

        private void PopulateListView()
        {
            foreach (ResourceConflictModel resourceModel in _losingResourceModels)
            {
                ListViewItem item = new ListViewItem(resourceModel.ResourceKey);
                if (Helper.TagList.TryGetValue(resourceModel.ResourceKey.Substring(0, 10), out string? type))
                    item.SubItems.Add(type);
                else
                    item.SubItems.Add(string.Empty);
                item.SubItems.Add(PackageModel.StripExtension(resourceModel.PackageName));
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(resourceModel.OtherModName);
                item.SubItems.Add(PackageModel.StripExtension(resourceModel.OtherPackageName));
                losingListView.Items.Add(item);
            }

            foreach (ResourceConflictModel resourceModel in _winningResourceModels)
            {
                ListViewItem item = new ListViewItem(resourceModel.ResourceKey);
                if (Helper.TagList.TryGetValue(resourceModel.ResourceKey.Substring(0, 10), out string? type))
                    item.SubItems.Add(type);
                else
                    item.SubItems.Add(string.Empty);
                item.SubItems.Add(PackageModel.StripExtension(resourceModel.PackageName));
                item.SubItems.Add(string.Empty);
                item.SubItems.Add(resourceModel.OtherModName);
                item.SubItems.Add(PackageModel.StripExtension(resourceModel.OtherPackageName));
                winningListView.Items.Add(item);
            }
            Dpi.FitColumns(losingListView, losingPackageHeader1, losingPackageHeader2); // scroll bars may have appeared
            Dpi.FitColumns(winningListView, winningPackageHeader1, winningPackageHeader2);
        }

        private void losingListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void losingListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemIndex > 2)
            {
                ListViewItem item = losingListView.Items[e.ItemIndex];
                ListViewItem prevItem = losingListView.Items[e.ItemIndex - 1];

                ResourceConflictModel prevResourceModel = _losingResourceModels[e.ItemIndex - 1];
                ResourceConflictModel resourceModel = _losingResourceModels[e.ItemIndex];

                if (resourceModel.ResourceKey.Equals(prevResourceModel.ResourceKey))
                    e.SubItem!.BackColor = prevItem.BackColor;
                else
                    e.SubItem!.BackColor = prevItem.BackColor.Equals(SystemColors.Window) ? SystemColors.Control : SystemColors.Window;
            }
            else
                e.SubItem!.BackColor = (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;

            e.DrawDefault = true;
        }

        private void winningListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void winningListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.ItemIndex > 2)
            {
                ListViewItem item = winningListView.Items[e.ItemIndex];
                ListViewItem prevItem = winningListView.Items[e.ItemIndex - 1];

                ResourceConflictModel prevResourceModel = _winningResourceModels[e.ItemIndex - 1];
                ResourceConflictModel resourceModel = _winningResourceModels[e.ItemIndex];

                if (resourceModel.ResourceKey.Equals(prevResourceModel.ResourceKey))
                    e.SubItem!.BackColor = prevItem.BackColor;
                else
                    e.SubItem!.BackColor = prevItem.BackColor.Equals(SystemColors.Window) ? SystemColors.Control : SystemColors.Window;
            }
            else
                e.SubItem!.BackColor = (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;

            e.DrawDefault = true;
        }

        #endregion


        #region ResourceContent

        private List<ResourceModel> _resourceModels = new List<ResourceModel>();
        private Dictionary<string, string> _nameMapLookup = new();
        private System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> _ddsBitmaps = new();
        private void PopulateResourceContent()
        {
            if (_packageModel == null)
                return;

            resourceSplitContainer.Visible = true;
            packageSplitContainer.Visible = false;

            Dpi.RestoreSplitter("ModForm", "ModFormResourceSplitContainerSplitterDistance", resourceSplitContainer);

            _resourceModels.Clear();

            foreach (ResourceEntry resourceEntry in _package.ResourceEntries)
            {
                _resourceModels.Add(new ResourceModel(resourceEntry));

                if (resourceEntry.Type == 0x0166038C)
                {
                    Dictionary<ulong, string> nameMap = ResourceEntry.DecodeNameMap(resourceEntry);

                    foreach (var kvp in nameMap)
                    {
                        string instance = Helper.UInt64ToHexString(kvp.Key);
                        if (_nameMapLookup.ContainsKey(instance))
                            _nameMapLookup[instance] = kvp.Value;
                        else
                            _nameMapLookup.Add(instance, kvp.Value);
                    }
                }
            }

            _resourceModels.Sort((r1, r2) => r1.ResourceKey.CompareTo(r2.ResourceKey));

            foreach (ResourceModel resourceModel in _resourceModels)
            {
                ListViewItem item = new ListViewItem(resourceModel.ResourceKey);

                if (_nameMapLookup.TryGetValue(resourceModel.Instance, out string? name))
                    item.SubItems.Add(name);
                else
                    item.SubItems.Add(string.Empty);

                item.SubItems.Add(resourceModel.Tag);
                item.SubItems.Add(resourceModel.Type);
                item.SubItems.Add(resourceModel.Group);
                item.SubItems.Add(resourceModel.Instance);
                resourceListView.Items.Add(item);
            }
            Dpi.FitColumns(resourceListView, resourceNameHeader);

            resourceProgressPanel.Visible = true;
            resourceProgressBar.Maximum = _resourceModels.Count;
            resourceProgressBar.Value = 0;
            resourceDataBackgroundWorker.RunWorkerAsync();
        }

        private void resourceDataBackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            // One open file for all resources (it used to be opened again for every resource).
            using PackageReader reader = _package.OpenReader();

            for (int i = 0; i < _resourceModels.Count; i++)
            {
                if (resourceDataBackgroundWorker.CancellationPending)
                {
                    e.Cancel = true;
                    return;
                }

                if (i % 64 == 0)
                    resourceDataBackgroundWorker.ReportProgress(i);

                ResourceModel resourceModel = _resourceModels[i];
                byte[] data;
                try { data = reader.ReadUncompressed(resourceModel.ResourceEntry); }
                catch (Exception) { continue; } // unreadable resource: shown as empty
                resourceModel.SetData(data);

                if (Helper.DDSTypes.Contains(resourceModel.Type))
                {
                    DDSImage dds = null!;
                    try
                    { dds = new DDSImage(data); }
                    catch
                    { }

                    if (dds != null && dds.images.Length > 0)
                        _ddsBitmaps[resourceModel.ResourceKey] = dds.images[0];
                }
            }
        }

        private void resourceDataBackgroundWorker_ProgressChanged(object sender, ProgressChangedEventArgs e)
        {
            resourceProgressLabel.Text = $"Reading {_resourceModels[e.ProgressPercentage].Instance}";
            resourceProgressBar.Value = e.ProgressPercentage;

            if (resourceListView.SelectedItems.Count == 1 && e.ProgressPercentage > 0 && resourceListView.SelectedItems[0].Text.Equals(_resourceModels[e.ProgressPercentage - 1].ResourceKey))
                resourceListView_SelectedIndexChanged(null!, null!);
        }

        private void resourceDataBackgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            resourceProgressPanel.Visible = false;
            resourceListView_SelectedIndexChanged(null!, null!);
        }

        private void resourceListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            resourceDataTextBox.Visible = false;
            resourceDataPictureBox.Visible = false;

            if (resourceListView.SelectedItems.Count != 1)
                return;

            ListViewItem item = resourceListView.SelectedItems[0];
            int i = _resourceModels.FindIndex(r => r.ResourceKey.Equals(item.Text));
            ResourceModel resourceModel = _resourceModels[i];

            string type = resourceModel.Type;
            byte[] data = resourceModel.Data;

            if (data == null)
                return;


            if (Helper.ImageTypes.Contains(type))
            {
                resourceDataPictureBox.Visible = true;
                MemoryStream stream = new MemoryStream(resourceModel.Data);
                resourceDataPictureBox.Image = new Bitmap(stream);
                stream.Close();

            }
            else if (Helper.DDSTypes.Contains(type))
            {
                if (!_ddsBitmaps.TryGetValue(resourceModel.ResourceKey, out Bitmap? img))
                {
                    if (!resourceDataBackgroundWorker.IsBusy)
                    {
                        resourceDataTextBox.Visible = true;
                        resourceDataTextBox.Text = "The DDS image couldn't be loaded.";
                    }
                    return;
                }
                resourceDataPictureBox.Visible = true;
                resourceDataPictureBox.Image = img;
            }
            else
            {
                resourceDataTextBox.Visible = true;
                resourceDataTextBox.Text = resourceModel.ResourceEntry.Decode();
            }
        }

        #endregion


        #region ModContent

        private void PopulateModContent()
        {
            if (_modModel == null)
                return;

            resourceSplitContainer.Visible = false;
            packageSplitContainer.Visible = true;
            // The right-hand pane of the mod view has never had anything in it; let the package list use the width.
            packageSplitContainer.Panel2Collapsed = true;

            Dpi.RestoreColumnWidth("ModForm", "ModFormPackageFlagHeaderWidth", packageFlagHeader, this);


            List<PackageModel> packageModels = _modModel.PackageModels;
            packageModels.Sort((p1, p2) => StringLogicalComparer.Compare(p1.Name, p2.Name));

            foreach (PackageModel packageModel in packageModels)
            {
                ListViewItem item = new ListViewItem(packageModel.Name);
                item.SubItems.Add(string.Empty);
                packageListView.Items.Add(item);
            }
            Dpi.FitColumns(packageListView, packageHeader);
        }

        List<string> packageListView_conflictLoser = new List<string>();
        List<string> packageListView_conflictWinner = new List<string>();
        private void packageListView_SelectedIndexChanged(object sender, EventArgs e)
        {
            packageListView_conflictLoser.Clear();
            packageListView_conflictWinner.Clear();

            if (packageListView.SelectedItems.Count != 1)
                return;

            ListViewItem item = packageListView.SelectedItems[0];
            if (!_modModel.TryGetPackageModel(item.Text, out PackageModel packageModel))
                return;
            List<PackageModel> conflictingPackageModels = packageModel.ConflictingPackageModelsInModModel;

            if (conflictingPackageModels.Count == 0)
                return;

            foreach (PackageModel conflictingPackageModel in conflictingPackageModels)
            {
                int r = StringLogicalComparer.Compare(packageModel.Name, conflictingPackageModel.Name);

                if (r > 0)
                    packageListView_conflictLoser.Add(conflictingPackageModel.Name);
                else if (r < 0)
                    packageListView_conflictWinner.Add(conflictingPackageModel.Name);
            }
        }

        private void packageListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void packageListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (packageListView.Items.Count == 0)
                return;

            if (e.Item == null || e.SubItem == null)
                return;

            Color back = e.Item.Selected ? SystemColors.Highlight
                : packageListView_conflictLoser.Contains(e.Item.Text) ? Color.LightGreen
                : packageListView_conflictWinner.Contains(e.Item.Text) ? Color.LightCoral
                : (e.ItemIndex % 2 == 0) ? SystemColors.Window : SystemColors.Control;
            Dpi.FillCell(e.Graphics, e.Bounds, back);

            if (e.ColumnIndex == 0)
            {
                Rectangle text = Rectangle.FromLTRB(e.Bounds.Left + LogicalToDeviceUnits(5), e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, packageListView.Font, text, e.Item.Selected ? SystemColors.HighlightText : SystemColors.ControlText, Dpi.CellText | TextFormatFlags.Left);
            }
            else if (e.ColumnIndex == 1)
            {
                if (!_modModel.TryGetPackageModel(e.Item.Text, out PackageModel packageModel))
                    return;
                List<PackageModel> conflictingPackageModels = packageModel.ConflictingPackageModelsInModModel;

                if (conflictingPackageModels.Count == 0)
                    return;

                conflictingPackageModels.Sort((p1, p2) => StringLogicalComparer.Compare(p1.Name, p2.Name));

                int size = LogicalToDeviceUnits(16);
                Point at = new Point(e.Bounds.X + (e.Bounds.Width - size) / 2, e.Bounds.Y + (e.Bounds.Height - size) / 2);

                int i = StringLogicalComparer.Compare(packageModel.Name, conflictingPackageModels[0].Name);
                int j = StringLogicalComparer.Compare(packageModel.Name, conflictingPackageModels[conflictingPackageModels.Count - 1].Name);

                if (i < 0 && j < 0)
                    e.Graphics.DrawImage(Dpi.Icon(Icons.ErrorMinus, size), at);
                else if (i > 0 && j < 0)
                    e.Graphics.DrawImage(Dpi.Icon(Icons.ErrorPlusMinus, size), at);
                else if (i > 0 && j > 0)
                    e.Graphics.DrawImage(Dpi.Icon(Icons.ErrorPlus, size), at);
            }
        }

        private void packageListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo hit = packageListView.HitTest(e.X, e.Y);

            if (hit.Item != null)
            {
                if (!_modModel.TryGetPackageModel(hit.Item.Text, out PackageModel packageModel) || packageModel.LoadError != null)
                    return;

                ModForm form = new ModForm(packageModel);
                form.FormClosed += (s, e) => this.Enabled = true;
                this.Enabled = false;
                form.Show();
            }
        }

        #endregion
    }
}
