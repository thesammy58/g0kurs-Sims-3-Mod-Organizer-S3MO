using ns;
using s3molib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace s3mo
{
    partial class MainForm
    {
        List<string> fileListView_conflictLosingPackageModel = new List<string>();
        List<string> fileListView_conflictWinningPackageModel = new List<string>();

        private ModModel? SelectedMod => SelectedRow?.Mod;

        /// <summary>A file row shows the file name; its path inside the mod (e.g. "Hair\bob.package") is in Tag.</summary>
        private static string FileKey(ListViewItem item) => item.Tag as string ?? item.Text;

        private void fileListView_ItemSelectionChanged(object sender, ListViewItemSelectionChangedEventArgs e)
        {
            fileListView_conflictWinningPackageModel.Clear();
            fileListView_conflictLosingPackageModel.Clear();

            ModModel? modModel = SelectedMod;
            if (modModel == null || fileListView.SelectedItems.Count == 0)
            {
                fileListView.Invalidate();
                return;
            }

            if (!modModel.TryGetPackageModel(FileKey(fileListView.SelectedItems[0]), out PackageModel packageModel))
            {
                fileListView.Invalidate();
                return;
            }

            foreach (PackageModel p in packageModel.ConflictingPackageModelsInModModel)
            {
                int comp = StringLogicalComparer.Compare(p.Name, packageModel.Name);

                if (comp < 0)
                    fileListView_conflictLosingPackageModel.Add(p.Name);
                else if (comp > 0)
                    fileListView_conflictWinningPackageModel.Add(p.Name);
            }
            fileListView.Invalidate();
        }

        private void fileListView_DrawColumnHeader(object sender, DrawListViewColumnHeaderEventArgs e) => e.DrawDefault = true;

        private void fileListView_DrawSubItem(object sender, DrawListViewSubItemEventArgs e)
        {
            if (fileListView.Items.Count == 0 || e.Item == null || e.SubItem == null)
                return;

            Color back = e.Item.Selected ? SystemColors.Highlight
                : fileListView_conflictLosingPackageModel.Contains(FileKey(e.Item)) ? Color.LightGreen
                : fileListView_conflictWinningPackageModel.Contains(FileKey(e.Item)) ? Color.LightCoral
                : (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;
            Dpi.FillCell(e.Graphics, e.Bounds, back);

            if (e.ColumnIndex == 0)
            {
                Rectangle text = Rectangle.FromLTRB(e.Bounds.Left + LogicalToDeviceUnits(5), e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
                // Files in a switched-off folder (or a switched-off mod) are greyed out: they won't be built.
                bool active = SelectedRow is ModRow row && row.Mod.Enabled && _profileModel.IsFolderEffectivelyEnabled(row.Mod, row.RelativeFolder);
                Color color = e.Item.Selected ? SystemColors.HighlightText : active ? SystemColors.ControlText : SystemColors.GrayText;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, fileListView.Font, text, color, Dpi.CellText | TextFormatFlags.Left);
            }
            else if (e.ColumnIndex == 1)
            {
                ModModel? modModel = SelectedMod;
                if (modModel == null || !modModel.TryGetPackageModel(FileKey(e.Item), out PackageModel packageModel))
                    return;

                int size = LogicalToDeviceUnits(16);
                int x = e.Bounds.X + (e.Bounds.Width - size) / 2;
                int y = e.Bounds.Y + (e.Bounds.Height - size) / 2;

                if (packageModel.LoadError != null)
                {
                    Dpi.DrawIcon(e.Graphics, Icons.Warning, x, y, size);
                    return;
                }

                List<PackageModel> conflictingPackageModels = packageModel.ConflictingPackageModelsInModModel;

                if (conflictingPackageModels.Count == 0)
                    return;

                // Later packages (by name) overwrite earlier ones inside a mod.
                bool beforeSome = conflictingPackageModels.Any(p => StringLogicalComparer.Compare(packageModel.Name, p.Name) < 0);
                bool afterSome = conflictingPackageModels.Any(p => StringLogicalComparer.Compare(packageModel.Name, p.Name) > 0);

                Image icon = beforeSome && afterSome ? Icons.ErrorPlusMinus
                           : afterSome ? Icons.ErrorPlus
                           : Icons.ErrorMinus;
                Dpi.DrawIcon(e.Graphics, icon, x, y, size);
            }
        }

        private async void fileListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ModModel? modModel = SelectedMod;
            if (modModel == null)
                return;

            ListViewHitTestInfo info = fileListView.HitTest(e.X, e.Y);
            ListViewItem listViewItem = info.Item;

            if (listViewItem == null || !FileKey(listViewItem).EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                return;

            if (!modModel.TryGetPackageModel(FileKey(listViewItem), out PackageModel packageModel))
                return;

            this.Enabled = false;
            await WaitForAnalysisAsync();

            if (packageModel.LoadError != null)
            {
                this.Enabled = true;
                MessageBox.Show(packageModel.LoadError, $"Couldn't read {packageModel.Name}", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            ModForm modForm;
            try { modForm = new ModForm(packageModel); }
            catch (Exception ex)
            {
                this.Enabled = true;
                MessageBox.Show(ex.Message, $"Couldn't open {packageModel.Name}", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            modForm.FormClosed += (sender, e) => this.Enabled = true;
            modForm.Show();
        }

    }
}
