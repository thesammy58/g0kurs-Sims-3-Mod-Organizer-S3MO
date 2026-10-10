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
    // The mod list on the left: mods in priority order, each expandable into the folders inside it.
    // Every row is a ModRow (in ListViewItem.Tag). A mod's check box turns the mod on or off; a folder's check box
    // turns that folder (and everything below it) on or off within its mod. Only whole mods are reordered.
    partial class MainForm
    {
        private readonly HashSet<string> _expandedRows = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (bool inside, int lower, int higher)> _folderFlagCache = new Dictionary<string, (bool, int, int)>(StringComparer.OrdinalIgnoreCase);

        private static ModRow? RowOf(ListViewItem? item) => ModListView.RowOf(item);

        private ModRow? SelectedRow => modListView.SelectedItems.Count == 1 ? RowOf(modListView.SelectedItems[0]) : null;

        #region rows

        private ListViewItem MakeRow(ModRow row)
        {
            ListViewItem item = new ListViewItem(row.IsMod ? row.Mod.Name : row.Folder.Name) { Tag = row };
            item.Checked = _profileModel.IsFolderEnabled(row.Mod, row.RelativeFolder);
            item.SubItems.Add(string.Empty); // flags (drawn)
            item.SubItems.Add(string.Empty); // priority (mods only)
            return item;
        }

        /// <summary>Rows for the folders below a row that are visible (their parents are expanded).</summary>
        private void AddVisibleChildren(ModRow row, List<ListViewItem> into)
        {
            if (!_expandedRows.Contains(row.Key))
                return;
            foreach (FolderNode child in row.Folder.Children)
            {
                ModRow childRow = new ModRow(row.Mod, child);
                into.Add(MakeRow(childRow));
                AddVisibleChildren(childRow, into);
            }
        }

        /// <summary>Rebuilds every row from the model, keeping the selection and scroll position.</summary>
        private void RebuildModRows(IEnumerable<string>? selectKeys = null)
        {
            HashSet<string> selected = new HashSet<string>(selectKeys ?? modListView.SelectedItems.Cast<ListViewItem>().Select(i => RowOf(i)?.Key ?? i.Text), StringComparer.OrdinalIgnoreCase);
            int top = modListView.TopItem?.Index ?? 0;

            List<ModModel> mods = _profileModel.ModModels;
            mods.Sort(new ModModelComparer(ModModelComparer.SortBy.Priority));

            List<ListViewItem> items = new List<ListViewItem>();
            int displayIndex = 0;
            foreach (ModModel m in mods)
            {
                ModRow row = new ModRow(m, m.RootFolder);
                ListViewItem item = MakeRow(row);
                item.SubItems[2].Text = m.Enabled ? (displayIndex++).ToString() : string.Empty;
                items.Add(item);
                AddVisibleChildren(row, items);
            }

            _populatingModList = true;
            modListView.BeginUpdate();
            try
            {
                modListView.Populate(() =>
                {
                    modListView.Items.Clear();
                    modListView.Items.AddRange(items.ToArray());
                });
                fileListView.Items.Clear();

                ListViewItem? first = null;
                foreach (ListViewItem item in modListView.Items)
                {
                    if (RowOf(item) is ModRow r && selected.Contains(r.Key))
                    {
                        item.Selected = true;
                        first ??= item;
                    }
                }
                if (modListView.Items.Count > 0)
                    modListView.TopItem = modListView.Items[Math.Min(top, modListView.Items.Count - 1)];
                if (first != null)
                {
                    first.Focused = true;
                    first.EnsureVisible();
                }
            }
            finally
            {
                modListView.EndUpdate();
                _populatingModList = false;
            }
            FitNameColumns();
            ShowFilesOfSelection();
            RefreshSelectedModHighlight();
        }

        /// <summary>Opens or closes a row in place (no full rebuild, so the list doesn't jump).</summary>
        private void SetExpanded(ListViewItem item, bool expand)
        {
            if (RowOf(item) is not ModRow row || !row.HasChildren || _expandedRows.Contains(row.Key) == expand)
                return;

            bool lostSelection = false;
            _populatingModList = true;
            modListView.BeginUpdate();
            try
            {
                int index = item.Index;
                if (expand)
                {
                    _expandedRows.Add(row.Key);
                    var children = new List<ListViewItem>();
                    AddVisibleChildren(row, children);
                    modListView.Populate(() =>
                    {
                        for (int i = 0; i < children.Count; i++)
                            modListView.Items.Insert(index + 1 + i, children[i]);
                    });
                }
                else
                {
                    _expandedRows.Remove(row.Key);
                    while (index + 1 < modListView.Items.Count && RowOf(modListView.Items[index + 1]) is ModRow next
                           && next.Mod == row.Mod && next.Depth > row.Depth)
                    {
                        lostSelection |= modListView.Items[index + 1].Selected;
                        modListView.Items.RemoveAt(index + 1);
                    }
                    // A row that was selected inside the closed folder hands the selection to the folder itself
                    if (lostSelection)
                    {
                        item.Selected = true;
                        item.Focused = true;
                    }
                }
            }
            finally
            {
                modListView.EndUpdate();
                _populatingModList = false;
            }
            FitNameColumns();
            if (lostSelection)
            {
                ShowFilesOfSelection();
                RefreshSelectedModHighlight();
            }
            modListView.Invalidate();
        }

        private void modListView_ExpandRequested(ListViewItem item, bool? expand)
        {
            if (RowOf(item) is not ModRow row)
                return;

            bool isExpanded = _expandedRows.Contains(row.Key);
            if (expand == false && (!row.HasChildren || !isExpanded) && !row.IsMod)
            {
                // Left on a closed folder moves to its parent row, like a tree view
                for (int i = item.Index - 1; i >= 0; i--)
                {
                    if (RowOf(modListView.Items[i]) is ModRow r && r.Mod == row.Mod && r.Depth < row.Depth)
                    {
                        modListView.SelectedItems.Clear();
                        modListView.Items[i].Selected = true;
                        modListView.Items[i].Focused = true;
                        modListView.Items[i].EnsureVisible();
                        break;
                    }
                }
                return;
            }
            SetExpanded(item, expand ?? !isExpanded);
        }

        private void LoadExpandedRows()
        {
            _expandedRows.Clear();
            if (Settings.TryGetSettings("ExpandedModFolders", out string s))
                foreach (string key in s.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    _expandedRows.Add(key);
        }

        private void SaveExpandedRows()
        {
            // Only rows that still exist, so the setting doesn't collect folders that were renamed or removed
            IEnumerable<string> existing = _expandedRows.Where(key =>
            {
                int cut = key.IndexOf(Path.DirectorySeparatorChar);
                string modName = cut < 0 ? key : key.Substring(0, cut);
                string relative = cut < 0 ? string.Empty : key.Substring(cut + 1);
                return _profileModel != null && _profileModel.TryGetModModel(modName, out ModModel mod) && mod.FindFolder(relative) != null;
            });
            Settings.SetSettings("ExpandedModFolders", string.Join("|", existing));
        }

        #endregion

        #region modListView

        private void modListView_DragDrop(object sender, DragEventArgs e)
        {
            // Move the dragged mods to the drop position: below the target when dragging down, above it when dragging up.
            Point p = modListView.PointToClient(new Point(e.X, e.Y));
            ModRow? target = RowOf(modListView.GetItemAt(p.X, p.Y));
            List<ModModel> dragged = modListView.SelectedItems.Cast<ListViewItem>().Select(RowOf).Where(r => r != null && r.IsMod).Select(r => r!.Mod).ToList();
            if (dragged.Count == 0)
                return;

            // Below the last row = to the end of the list
            bool toEnd = target == null && modListView.Items.Count > 0
                && p.Y > modListView.Items[modListView.Items.Count - 1].GetBounds(ItemBoundsPortion.Entire).Bottom;
            if ((target == null && !toEnd) || (target != null && dragged.Contains(target.Mod)))
                return;

            List<ModModel> order = _profileModel.ModModels;
            order.Sort(new ModModelComparer(ModModelComparer.SortBy.Priority));
            if (toEnd)
            {
                order.RemoveAll(dragged.Contains);
                order.AddRange(dragged);
            }
            else
            {
                bool down = order.IndexOf(dragged[0]) < order.IndexOf(target!.Mod);
                order.RemoveAll(dragged.Contains);
                order.InsertRange(order.IndexOf(target.Mod) + (down ? 1 : 0), dragged);
            }

            for (int i = 0; i < order.Count; i++)
                order[i].Update(order[i].Name, order[i].Enabled, i);
            _profileModel.Save(_currentProfile);

            _folderFlagCache.Clear();
            RebuildModRows(dragged.Select(m => m.Name));
            ValidateModBuild();
        }

        private void modListView_ItemChecked(object sender, ItemCheckedEventArgs e)
        {
            // Not a user change: the list is being filled.
            if (_populatingModList || RowOf(e.Item) is not ModRow row)
                return;

            if (row.IsMod)
            {
                if (row.Mod.Enabled == e.Item.Checked)
                    return;
                row.Mod.Update(row.Mod.Name, e.Item.Checked, row.Mod.Priority);
                UpdateModModelPriority();
            }
            else
            {
                if (_profileModel.IsFolderEnabled(row.Mod, row.RelativeFolder) == e.Item.Checked)
                    return;
                _profileModel.SetFolderEnabled(row.Mod, row.RelativeFolder, e.Item.Checked);
                _profileModel.Save(_currentProfile);
                // Packages in switched-off folders don't count for conflicts; the check is quick with the cache.
                StartAnalysis();
            }
            _folderFlagCache.Clear();
            ShowFilesOfSelection();
            RefreshSelectedModHighlight();
            modListView.Invalidate();
        }

        private bool modListView_isEditingText = false; // Use to hide the drawn string on subitem.
        private string modListView_preLabelEditBuffer = string.Empty;
        private void modListView_BeforeLabelEdit(object sender, LabelEditEventArgs e)
        {
            if (RowOf(modListView.Items[e.Item])?.IsMod != true)
            {
                e.CancelEdit = true; // folders are renamed in Explorer
                return;
            }
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

            string oldName = modListView_preLabelEditBuffer;
            Logger.LogDebug($"Renaming mod folder from '{oldName}' to '{newFolderName}'");
            try
            {
                _profileModel.RenameMod(oldName, newFolderName);
            }
            catch (Exception ex)
            {
                e.CancelEdit = true;
                MessageBox.Show(ex.Message, "Couldn't rename the mod", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Expanded rows of the mod keep their state under the new name
            foreach (string key in _expandedRows.ToList())
            {
                if (key.Equals(oldName, StringComparison.OrdinalIgnoreCase) || key.StartsWith(oldName + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    _expandedRows.Remove(key);
                    _expandedRows.Add(newFolderName + key.Substring(oldName.Length));
                }
            }
            _folderFlagCache.Clear();
            BeginInvoke(() => RebuildModRows(new[] { newFolderName }));
            ValidateModBuild();
        }

        // Rows (mods and folders, by ModRow.Key) holding packages that the selection overwrites (green) or that
        // overwrite the selection (red). A row in both is drawn in a third colour.
        private readonly HashSet<string> _rowsLosingToSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _rowsWinningOverSelection = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Color LosesColor = Color.LightGreen, WinsColor = Color.LightCoral, BothColor = Color.Khaki;
        private void modListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            if (_populatingModList)
                return;
            ShowFilesOfSelection();
            RefreshSelectedModHighlight();
        }

        /// <summary>The right-hand panel lists the files directly inside the selected mod or folder.</summary>
        private void ShowFilesOfSelection()
        {
            fileListView.BeginUpdate();
            try
            {
                fileListView.Items.Clear();

                if (SelectedRow is not ModRow row)
                    return;

                var items = new List<ListViewItem>();
                foreach (PackageModel packageModel in row.Mod.GetPackagesIn(row.RelativeFolder))
                {
                    ListViewItem listViewItem = new ListViewItem(Path.GetFileName(packageModel.Name)) { Tag = packageModel.Name };
                    listViewItem.SubItems.Add(String.Empty);
                    items.Add(listViewItem);
                }

                foreach (string otherFile in row.Mod.GetOtherFilesIn(row.RelativeFolder))
                {
                    ListViewItem listViewItem = new ListViewItem(Path.GetFileName(otherFile)) { Tag = otherFile };
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
        }

        /// <summary>
        /// Colours the rows that conflict with the selected mod or folder: green where the selection overwrites
        /// them, red where they overwrite the selection, including the folders the conflicting packages are in.
        /// </summary>
        private void RefreshSelectedModHighlight()
        {
            _rowsLosingToSelection.Clear();
            _rowsWinningOverSelection.Clear();

            if (SelectedRow is ModRow selection && selection.Mod.Enabled && _profileModel.IsFolderEffectivelyEnabled(selection.Mod, selection.RelativeFolder))
                ConflictHighlight.Compute(selection.Mod, selection.RelativeFolder, _rowsLosingToSelection, _rowsWinningOverSelection);
            modListView.Invalidate();
        }

        /// <summary>
        /// Flags of a folder row: packages inside overlap other packages of the mod, and how many times the
        /// folder's packages lose to / win against other mods. Cached until conflicts, priorities or switches change.
        /// </summary>
        private (bool inside, int lower, int higher) FolderFlags(ModRow row)
        {
            if (_folderFlagCache.TryGetValue(row.Key, out var cached))
                return cached;

            bool inside = false;
            int lower = 0, higher = 0;
            int priority = row.Mod.Priority;
            foreach (PackageModel p in row.Mod.GetPackagesUnder(row.RelativeFolder))
            {
                inside |= p.HasConflictsInModModel;
                foreach (PackageModel q in p.GetConflictingPackagesInOtherMods())
                {
                    if (!q.ModModel.Enabled) continue;
                    if (q.ModModel.Priority < priority) lower++;
                    else if (q.ModModel.Priority > priority) higher++;
                }
            }
            return _folderFlagCache[row.Key] = (inside, lower, higher);
        }

        private void modListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;
        private void modListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (e.Item == null || e.SubItem == null || RowOf(e.Item) is not ModRow row)
                return;

            // Draw background color
            Color back = e.Item.Selected ? SystemColors.Highlight
                : _rowsLosingToSelection.Contains(row.Key) && _rowsWinningOverSelection.Contains(row.Key) ? BothColor
                : _rowsLosingToSelection.Contains(row.Key) ? LosesColor
                : _rowsWinningOverSelection.Contains(row.Key) ? WinsColor
                : (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;
            Dpi.FillCell(e.Graphics, e.Bounds, back);

            // A folder whose mod or parent folder is off is shown greyed out.
            bool active = row.IsMod || _profileModel.IsFolderEffectivelyEnabled(row.Mod, row.Folder.Parent!.RelativePath);
            Color textColor = e.Item.Selected ? SystemColors.HighlightText : active ? SystemColors.ControlText : SystemColors.GrayText;
            Font font = modListView.Font;

            // All positions are in 96-DPI pixels scaled to the list's DPI, and text is centered vertically in the row.
            if (e.ColumnIndex == 0) // Name
            {
                if (row.HasChildren)
                    Dpi.DrawExpandGlyph(e.Graphics, modListView.GetGlyphBounds(e.Item, e.Bounds), _expandedRows.Contains(row.Key), textColor);

                CheckState state = !e.Item.Checked ? CheckState.Unchecked
                    : _profileModel.HasDisabledFoldersBelow(row.Mod, row.RelativeFolder) ? CheckState.Indeterminate
                    : CheckState.Checked;
                Dpi.DrawCheckBox(e.Graphics, modListView.GetCheckBoxBounds(e.Item, e.Bounds), state, active);

                if (!(modListView_isEditingText && e.Item.Selected))
                {
                    Rectangle text = Rectangle.FromLTRB(modListView.GetTextLeft(e.Item, e.Bounds), e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
                    TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, text, textColor, Dpi.CellText | TextFormatFlags.Left);
                }
            }
            else if (e.ColumnIndex == 2) // Priority
            {
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, font, e.Bounds, textColor, Dpi.CellText | TextFormatFlags.HorizontalCenter);
            }
            else if (e.ColumnIndex == 1) // Icons
            {
                ModModel modModel = row.Mod;
                if (!modModel.Enabled || !_profileModel.IsFolderEffectivelyEnabled(modModel, row.RelativeFolder))
                    return;

                bool inside;
                int lower = 0, higher = 0;
                if (row.IsMod)
                {
                    inside = modModel.HasPackageConflictsInside;
                    foreach (ModModel m in modModel.ConflictingMods)
                    {
                        if (!m.Enabled) continue;
                        if (m.Priority < modModel.Priority) lower++;
                        else if (m.Priority > modModel.Priority) higher++;
                    }
                }
                else
                {
                    (inside, lower, higher) = FolderFlags(row);
                }

                int size = LogicalToDeviceUnits(16);
                int gap = LogicalToDeviceUnits(2);
                int x = e.Bounds.X + gap;
                int y = e.Bounds.Y + (e.Bounds.Height - size) / 2;

                // Packages inside this mod/folder overwrite each other
                if (inside)
                {
                    Dpi.DrawIcon(e.Graphics, Icons.PackageMinus, x, y, size);
                    x += size + gap;
                }

                if (lower == 0 && higher == 0)
                    return;

                Image icon = lower > 0 && higher > 0 ? Icons.ErrorPlusMinus   // wins some, loses some
                           : lower > 0 ? Icons.ErrorPlus                      // overwrites lower mods
                           : Icons.ErrorMinus;                                // overwritten by higher mods
                Dpi.DrawIcon(e.Graphics, icon, x, y, size);
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

            if (listViewItem == null || modListView.IsOnCheckBox(e.Location) || modListView.IsOnGlyph(e.Location) || RowOf(listViewItem) is not ModRow row)
                return;

            if (!row.IsMod)
            {
                SetExpanded(listViewItem, !_expandedRows.Contains(row.Key)); // folders open and close
                return;
            }
            await OpenModFormAsync(row.Mod.Name);
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

        private void SetupModListContextMenu()
        {
            // Rename and Delete apply to whole mods only
            modListViewContextMenuStrip.Opening += (s, e) =>
            {
                var rows = modListView.SelectedItems.Cast<ListViewItem>().Select(RowOf).ToList();
                bool anyMod = rows.Any(r => r?.IsMod == true);
                modListViewToolStripMenuItemRename.Enabled = rows.Count == 1 && anyMod;
                modListViewToolStripMenuItemDelete.Enabled = anyMod;
            };
        }

        private void modListViewToolStripMenuItemCreateEmptyMod_Click(object sender, EventArgs e)
        {
            string modName = _profileModel.CreateEmptyMod();
            _profileModel.Save(_currentProfile);
            RebuildModRows(new[] { modName });
            UpdateModModelPriority();

            ListViewItem? item = modListView.Items.Cast<ListViewItem>().FirstOrDefault(i => RowOf(i)?.IsMod == true && RowOf(i)!.Mod.Name == modName);
            item?.BeginEdit();
        }


        private void modListViewToolStripMenuItemEnableSelected_Click(object sender, EventArgs e) => SetSelectedEnabled(true);

        private void modListViewToolStripMenuItemDisableSelected_Click(object sender, EventArgs e) => SetSelectedEnabled(false);

        private void SetSelectedEnabled(bool enabled)
        {
            if (modListView.SelectedItems.Count == 0)
                return;

            bool foldersChanged = false;
            _populatingModList = true; // one save at the end instead of one per item
            try
            {
                foreach (ListViewItem item in modListView.SelectedItems)
                {
                    if (RowOf(item) is not ModRow row)
                        continue;
                    modListView.SetChecked(item, enabled);
                    if (row.IsMod)
                        row.Mod.Update(row.Mod.Name, enabled, row.Mod.Priority);
                    else if (_profileModel.IsFolderEnabled(row.Mod, row.RelativeFolder) != enabled)
                    {
                        _profileModel.SetFolderEnabled(row.Mod, row.RelativeFolder, enabled);
                        foldersChanged = true;
                    }
                }
            }
            finally
            {
                _populatingModList = false;
            }
            _folderFlagCache.Clear();
            UpdateModModelPriority();
            if (foldersChanged)
                StartAnalysis();
            ShowFilesOfSelection();
            RefreshSelectedModHighlight();
        }

        private async void modListViewToolStripMenuItemInformation_Click(object sender, EventArgs e)
        {
            if (SelectedRow is ModRow row)
                await OpenModFormAsync(row.Mod.Name);
        }

        private void modListViewToolStripMenuItemOpenInExplorer_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count > 0 && RowOf(modListView.SelectedItems[0]) is ModRow row)
                OpenInExplorer(Path.Combine(row.Mod.FolderPath, row.RelativeFolder));
        }

        private void modListViewToolStripMenuItemRenameMod_Click(object sender, EventArgs e)
        {
            if (modListView.SelectedItems.Count > 0 && RowOf(modListView.SelectedItems[0])?.IsMod == true)
                modListView.SelectedItems[0].BeginEdit();
        }

        private void modListViewToolStripMenuItemDeleteMod_Click(object sender, EventArgs e)
        {
            List<ModModel> mods = modListView.SelectedItems.Cast<ListViewItem>().Select(RowOf).Where(r => r?.IsMod == true).Select(r => r!.Mod).ToList();
            if (mods.Count == 0)
                return;

            string s = $"Delete the following mod(s) permanently?{Environment.NewLine}";
            foreach (ModModel m in mods)
                s += $"- {m.Name}{Environment.NewLine}";

            DialogResult r = MessageBox.Show(s, "Delete Mod(s)", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

            if (r == DialogResult.Yes)
            {
                foreach (ModModel m in mods)
                {
                    try
                    {
                        _profileModel.DeleteMod(m.Name);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, $"Couldn't delete '{m.Name}'", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }

                RebuildModRows(Array.Empty<string>());
                this.UpdateModModelPriority();
                // Conflicts with the deleted mods disappear
                StartAnalysis();
            }
        }

        #endregion


    }
}
