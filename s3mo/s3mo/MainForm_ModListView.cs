using ns;
using s3molib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace s3mo
{
    partial class MainForm
    {
        #region modListView

        private void modListView_DragDrop(object sender, DragEventArgs e)
        {
            UpdateModModelPriority();
            RefreshSelectedModHighlight();
        }

        private void modListView_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            // Not a user change: the list is being filled, or dragged items are being re-inserted.
            if (_populatingModList || modListView.IsReordering)
                return;

            if (!_profileModel.TryGetModModel(e.Item.Text, out ModModel mod) || mod.Enabled == e.Item.Checked)
                return;
            mod.Update(mod.Name, e.Item.Checked, mod.Priority);
            UpdateModModelPriority();
            RefreshSelectedModHighlight();
            modListView.Invalidate();
        }

        private bool modListView_isEditingText = false; // Use to hide the drawn string on subitem.
        private string modListView_preLabelEditBuffer = string.Empty;
        private void modListView_BeforeLabelEdit(object sender, LabelEditEventArgs e)
        {
            modListView_isEditingText = true;
            modListView_preLabelEditBuffer = modListView.Items[e.Item].Text;
        }
        private void modListView_AfterLabelEdit(object sender, LabelEditEventArgs e)
        {
            string? newFolderName = e.Label;
            modListView_isEditingText = false;

            if (newFolderName == null) // edit cancelled
                return;

            if (!Helper.CheckValidFolderName(newFolderName) || _profileModel.TryGetModModel(newFolderName, out _))
            {
                e.CancelEdit = true;
                return;
            }

            Logger.LogDebug($"Renaming mod folder from '{modListView_preLabelEditBuffer}' to '{newFolderName}'");
            try
            {
                _profileModel.RenameMod(modListView_preLabelEditBuffer, newFolderName);
            }
            catch (Exception ex)
            {
                e.CancelEdit = true;
                MessageBox.Show(ex.Message, "Couldn't rename the mod", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            ValidateModBuild();
        }

        private List<string> modListView_losingConflictingModModels = new List<string>();
        private List<string> modListView_winningConflictingModModels = new List<string>();
        private void modListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            // Populate the packages panel on the right
            fileListView.BeginUpdate();
            try
            {
                fileListView.Items.Clear();

                if (modListView.SelectedItems.Count != 1)
                {
                    modListView_losingConflictingModModels.Clear();
                    modListView_winningConflictingModModels.Clear();
                    return;
                }

                if (!_profileModel.TryGetModModel(modListView.SelectedItems[0].Text, out ModModel modModel))
                    return;

                var items = new List<ListViewItem>();
                foreach (PackageModel packageModel in modModel.GetSortedPackages())
                {
                    ListViewItem listViewItem = new ListViewItem(packageModel.Name);
                    listViewItem.SubItems.Add(String.Empty);
                    items.Add(listViewItem);
                }

                foreach (string otherFile in modModel.GetOtherFiles())
                {
                    ListViewItem listViewItem = new ListViewItem(otherFile);
                    listViewItem.SubItems.Add(String.Empty);
                    items.Add(listViewItem);
                }
                fileListView.Items.AddRange(items.ToArray());
            }
            finally
            {
                fileListView.EndUpdate();
                Dpi.FitColumns(fileListView, fileHeader); // a scroll bar may have appeared
            }

            RefreshSelectedModHighlight();
        }

        /// <summary>Colors the mods that the selected mod wins/loses against.</summary>
        private void RefreshSelectedModHighlight()
        {
            // Responsible for conflict row color change
            modListView_losingConflictingModModels.Clear();
            modListView_winningConflictingModModels.Clear();

            if (modListView.SelectedItems.Count != 1 || !_profileModel.TryGetModModel(modListView.SelectedItems[0].Text, out ModModel modModel))
                return;

            if (modModel.Enabled)
            {
                foreach (ModModel conflict in modModel.ConflictingMods)
                {
                    if (!conflict.Enabled)
                        continue;
                    if (conflict.Priority < modModel.Priority)
                        modListView_losingConflictingModModels.Add(conflict.Name);
                    else if (modModel.Priority < conflict.Priority)
                        modListView_winningConflictingModModels.Add(conflict.Name);
                }
            }
            modListView.Invalidate();
        }

        private void modListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;
        private void modListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item == null || e.SubItem == null)
                return;

            // Draw background color
            Color back = e.Item.Selected ? SystemColors.Highlight
                : modListView_losingConflictingModModels.Contains(e.Item.Text) ? Color.LightGreen
                : modListView_winningConflictingModModels.Contains(e.Item.Text) ? Color.LightCoral
                : (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;
            Dpi.FillCell(e.Graphics, e.Bounds, back);

            Color textColor = e.Item.Selected ? SystemColors.HighlightText : SystemColors.ControlText;
            Font font = modListView.Font;

            // All positions are in 96-DPI pixels scaled to the list's DPI, and text is centered vertically in the row.
            if (e.ColumnIndex == 0) // Name
            {
                Rectangle box = modListView.GetCheckBoxBounds(e.Bounds);
                Dpi.DrawCheckBox(e.Graphics, box, e.Item.Checked);

                if (!(modListView_isEditingText && e.Item.Selected))
                {
                    Rectangle text = Rectangle.FromLTRB(box.Right + LogicalToDeviceUnits(5), e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, text, textColor, Dpi.CellText | TextFormatFlags.Left);
                }
            }
            else if (e.ColumnIndex == 2) // Priority
            {
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, e.Bounds, textColor, Dpi.CellText | TextFormatFlags.HorizontalCenter);
            }
            else if (e.ColumnIndex == 1) // Icons
            {
                if (!_profileModel.TryGetModModel(e.Item.Text, out ModModel modModel) || !modModel.Enabled)
                    return;

                int size = LogicalToDeviceUnits(16);
                int gap = LogicalToDeviceUnits(2);
                int x = e.Bounds.X + gap;
                int y = e.Bounds.Y + (e.Bounds.Height - size) / 2;

                // Packages inside this mod overwrite each other
                if (modModel.HasPackageConflictsInside)
                {
                    e.Graphics.DrawImage(Dpi.Icon(Icons.PackageMinus, size), x, y);
                    x += size + gap;
                }

                int priority = modModel.Priority;
                int lower = 0, higher = 0;
                foreach (ModModel m in modModel.ConflictingMods)
                {
                    if (!m.Enabled) continue;
                    if (m.Priority < priority) lower++;
                    else if (m.Priority > priority) higher++;
                }

                if (lower == 0 && higher == 0)
                    return;

                Image icon = lower > 0 && higher > 0 ? Icons.ErrorPlusMinus   // wins some, loses some
                           : lower > 0 ? Icons.ErrorPlus                      // overwrites lower mods
                           : Icons.ErrorMinus;                                // overwritten by higher mods
                e.Graphics.DrawImage(Dpi.Icon(icon, size), x, y);
            }
        }

        private void modListView_MouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                ListViewItem? hit = modListView.GetItemAt(e.X, e.Y);

                if (hit != null)
                    modListViewContextMenuStrip.Show(Cursor.Position);
                else
                    modListViewNonSelectedContextMenuStrip.Show(Cursor.Position);
            }
        }

        private async void modListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ListViewHitTestInfo info = modListView.HitTest(e.X, e.Y);
            ListViewItem listViewItem = info.Item;

            if (listViewItem == null || modListView.IsOnCheckBox(e.Location))
                return;

            await OpenModFormAsync(listViewItem.Text);
        }

        private async Task OpenModFormAsync(string modName)
        {
            this.Enabled = false;
            await WaitForAnalysisAsync();

            if (!_profileModel.TryGetModModel(modName, out ModModel mod))
            {
                this.Enabled = true;
                return;
            }

            ModForm modForm = new ModForm(mod);
            modForm.FormClosed += (sender, e) => this.Enabled = true;
            modForm.Show();
        }

        #endregion


        #region modListView ContextMenu

        private void modListViewToolStripMenuItemCreateEmptyMod_Click(object sender, EventArgs e)
        {
            string modName = _profileModel.CreateEmptyMod();
            _profileModel.Save(_currentProfile);

            ListViewItem listViewItem = new ListViewItem(modName);
            listViewItem.Checked = false;
            listViewItem.SubItems.Add("");
            listViewItem.SubItems.Add("");
            _populatingModList = true;
            try { modListView.Items.Add(listViewItem); }
            finally { _populatingModList = false; }

            UpdateModModelPriority();

            modListView.SelectedItems.Clear();
            listViewItem.BeginEdit();
        }


        private void modListViewToolStripMenuItemEnableSelected_Click(object sender, EventArgs e) => SetSelectedEnabled(true);

        private void modListViewToolStripMenuItemDisableSelected_Click(object sender, EventArgs e) => SetSelectedEnabled(false);

        private void SetSelectedEnabled(bool enabled)
        {
            if (modListView.SelectedItems.Count == 0)
                return;

            _populatingModList = true; // one save at the end instead of one per item
            try
            {
                foreach (ListViewItem item in modListView.SelectedItems)
                {
                    modListView.SetChecked(item, enabled);
                    if (_profileModel.TryGetModModel(item.Text, out ModModel modModel))
                        modModel.Update(modModel.Name, enabled, modModel.Priority);
                }
            }
            finally
            {
                _populatingModList = false;
            }
            UpdateModModelPriority();
            RefreshSelectedModHighlight();
        }

        private async void modListViewToolStripMenuItemInformation_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count != 1)
                return;

            await OpenModFormAsync(modListView.SelectedItems[0].Text);
        }

        private void modListViewToolStripMenuItemOpenInExplorer_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count == 0)
                return;

            string modName = modListView.SelectedItems[0].Text;
            if (_profileModel.TryGetModModel(modName, out ModModel mod))
                OpenInExplorer(mod.FolderPath);
        }

        private void modListViewToolStripMenuItemRenameMod_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count == 0)
                return;

            modListView.SelectedItems[0].BeginEdit();
        }

        private void modListViewToolStripMenuItemDeleteMod_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count == 0)
                return;

            string s = $"Delete the following mod(s) permanently?{Environment.NewLine}";
            foreach (ListViewItem item in modListView.SelectedItems)
                s += $"- {item.Text}{Environment.NewLine}";

            DialogResult r = MessageBox.Show(s, "Delete Mod(s)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (r == DialogResult.Yes)
            {
                foreach (ListViewItem item in modListView.SelectedItems.Cast<ListViewItem>().ToList())
                {
                    string name = item.Text;
                    try
                    {
                        _profileModel.DeleteMod(name);
                        modListView.Items.Remove(item);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, $"Couldn't delete '{name}'", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                this.UpdateModModelPriority();
                // Conflicts with the deleted mods disappear
                StartAnalysis();
            }
        }

        #endregion


    }
}
