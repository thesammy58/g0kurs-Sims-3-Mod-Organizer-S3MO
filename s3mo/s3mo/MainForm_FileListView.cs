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

        private ModModel? SelectedMod =>
            modListView.SelectedItems.Count == 1 && _profileModel.TryGetModModel(modListView.SelectedItems[0].Text, out ModModel m) ? m : null;

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

            if (!modModel.TryGetPackageModel(fileListView.SelectedItems[0].Text, out PackageModel packageModel))
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

            if (e.Item.Selected)
                e.SubItem.BackColor = SystemColors.Highlight;
            else if (fileListView_conflictLosingPackageModel.Contains(e.Item.Text))
                e.SubItem.BackColor = Color.LightGreen;
            else if (fileListView_conflictWinningPackageModel.Contains(e.Item.Text))
                e.SubItem.BackColor = Color.LightCoral;
            else
                e.SubItem.BackColor = (e.ItemIndex % 2 != 0) ? SystemColors.Control : SystemColors.Window;

            e.DrawBackground();

            if (e.ColumnIndex == 0)
            {
                Rectangle text = Rectangle.FromLTRB(e.Bounds.Left + LogicalToDeviceUnits(5), e.Bounds.Top, e.Bounds.Right, e.Bounds.Bottom);
                Color color = e.Item.Selected ? SystemColors.HighlightText : SystemColors.ControlText;
                TextRenderer.DrawText(e.Graphics, e.SubItem.Text, fileListView.Font, text, color, Dpi.CellText | TextFormatFlags.Left);
            }
            else if (e.ColumnIndex == 1)
            {
                ModModel? modModel = SelectedMod;
                if (modModel == null || !modModel.TryGetPackageModel(e.Item.Text, out PackageModel packageModel))
                    return;

                int size = LogicalToDeviceUnits(16);
                int x = e.Bounds.X + (e.Bounds.Width - size) / 2;
                int y = e.Bounds.Y + (e.Bounds.Height - size) / 2;

                if (packageModel.LoadError != null)
                {
                    e.Graphics.DrawImage(Dpi.Icon(Properties.Resources.icons8_general_warning_sign_32, size), x, y);
                    return;
                }

                List<PackageModel> conflictingPackageModels = packageModel.ConflictingPackageModelsInModModel;

                if (conflictingPackageModels.Count == 0)
                    return;

                // Later packages (by name) overwrite earlier ones inside a mod.
                bool beforeSome = conflictingPackageModels.Any(p => StringLogicalComparer.Compare(packageModel.Name, p.Name) < 0);
                bool afterSome = conflictingPackageModels.Any(p => StringLogicalComparer.Compare(packageModel.Name, p.Name) > 0);

                Image icon = beforeSome && afterSome ? Properties.Resources.error_plusminus
                           : afterSome ? Properties.Resources.error_plus
                           : Properties.Resources.error_minus;
                e.Graphics.DrawImage(Dpi.Icon(icon, size), x, y);
            }
        }

        private async void fileListView_MouseDoubleClick(object sender, MouseEventArgs e)
        {
            ModModel? modModel = SelectedMod;
            if (modModel == null)
                return;

            ListViewHitTestInfo info = fileListView.HitTest(e.X, e.Y);
            ListViewItem listViewItem = info.Item;

            if (listViewItem == null || !listViewItem.Text.EndsWith(".package", StringComparison.OrdinalIgnoreCase))
                return;

            if (!modModel.TryGetPackageModel(listViewItem.Text, out PackageModel packageModel))
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
