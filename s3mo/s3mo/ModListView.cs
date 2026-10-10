using s3molib;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Reflection;

namespace s3mo
{
    /// <summary>One row of the mod list: a mod, or a folder inside a mod (shown indented under it).</summary>
    public sealed class ModRow
    {
        public ModModel Mod { get; }
        public FolderNode Folder { get; }

        public ModRow(ModModel mod, FolderNode folder)
        {
            Mod = mod;
            Folder = folder;
        }

        public bool IsMod => Folder.Parent == null;
        public int Depth => Folder.Depth;
        public bool HasChildren => Folder.Children.Count > 0;
        public string RelativeFolder => Folder.RelativePath;

        /// <summary>Identifies the row across rebuilds (used for the expanded state and the selection).</summary>
        public string Key => ConflictHighlight.RowKey(Mod.Name, Folder.RelativePath);
    }

    // The mod list: owner-drawn rows that form a tree (mods, and the folders inside them).
    //
    // Check boxes: the check box you see is painted by us and is also the click target; the native list view's own
    // (invisible) check box area scales differently with DPI, so its toggling is blocked.
    // Drag and drop: only whole mods can be dragged; the form reorders the mods and rebuilds the rows.
    public class ModListView : OwnerDrawListView
    {
        private const int WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;

        private int _nativeInputDepth;
        private int _allowCheckDepth;

        /// <summary>Raised when a row's expand arrow is clicked (null = toggle) or Right/Left is pressed (true/false).</summary>
        public event Action<ListViewItem, bool?>? ExpandRequested;

        public static ModRow? RowOf(ListViewItem? item) => item?.Tag as ModRow;

        /// <summary>Changes an item's check state (the only way it changes; native toggling is blocked).</summary>
        public void SetChecked(ListViewItem item, bool value)
        {
            if (item.Checked == value)
                return;
            _allowCheckDepth++;
            try { item.Checked = value; }
            finally { _allowCheckDepth--; }
        }

        /// <summary>
        /// Lets rows be added with their check state while handling a click or key (expanding a row, dropping
        /// dragged mods); otherwise the check box guard would also block the state of the new rows.
        /// </summary>
        public void Populate(Action fill)
        {
            _allowCheckDepth++;
            try { fill(); }
            finally { _allowCheckDepth--; }
        }

        // ------------------------------------------------------------ row layout (96-DPI pixels, scaled)

        private int RowLeft(ListViewItem item, Rectangle rowBounds) =>
            rowBounds.X + LogicalToDeviceUnits(2) + (RowOf(item)?.Depth ?? 0) * LogicalToDeviceUnits(16);

        /// <summary>Where the expand arrow goes (space is kept for it on every row so names line up).</summary>
        public Rectangle GetGlyphBounds(ListViewItem item, Rectangle rowBounds)
        {
            int size = LogicalToDeviceUnits(16);
            return new Rectangle(RowLeft(item, rowBounds), rowBounds.Y + (rowBounds.Height - size) / 2, size, size);
        }

        /// <summary>Where the check box is painted in a row (also its click area).</summary>
        public Rectangle GetCheckBoxBounds(ListViewItem item, Rectangle rowBounds)
        {
            int size = LogicalToDeviceUnits(13);
            Rectangle glyph = GetGlyphBounds(item, rowBounds);
            return new Rectangle(glyph.Right + LogicalToDeviceUnits(2), rowBounds.Y + (rowBounds.Height - size) / 2, size, size);
        }

        public int GetTextLeft(ListViewItem item, Rectangle rowBounds) => GetCheckBoxBounds(item, rowBounds).Right + LogicalToDeviceUnits(5);

        private Rectangle ClickArea(Rectangle r)
        {
            r.Inflate(LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
            return r;
        }

        public bool IsOnCheckBox(Point p)
        {
            ListViewItem? item = GetItemAt(p.X, p.Y);
            return item != null && ClickArea(GetCheckBoxBounds(item, item.GetBounds(ItemBoundsPortion.Entire))).Contains(p);
        }

        public bool IsOnGlyph(Point p)
        {
            ListViewItem? item = GetItemAt(p.X, p.Y);
            return item != null && RowOf(item)?.HasChildren == true && ClickArea(GetGlyphBounds(item, item.GetBounds(ItemBoundsPortion.Entire))).Contains(p);
        }

        // ------------------------------------------------------------ input

        protected override void WndProc(ref Message m)
        {
            // Clicks on the drawn check box or expand arrow are handled here and not passed on: the list view would
            // otherwise also treat them as a click on the row's text (selecting it, or starting a rename when the
            // row was already selected) or start a drag.
            if ((m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONDBLCLK) && HandleControlClick(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16)))))
            {
                m.Result = IntPtr.Zero;
                return;
            }

            bool input = m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONUP || m.Msg == WM_LBUTTONDBLCLK || m.Msg == WM_KEYDOWN || m.Msg == WM_CHAR;
            if (input) _nativeInputDepth++;
            try { base.WndProc(ref m); }
            finally { if (input) _nativeInputDepth--; }
        }

        /// <summary>A left click on a row's expand arrow or check box; each click (also each half of a double click) counts.</summary>
        private bool HandleControlClick(Point p)
        {
            ListViewItem? item = GetItemAt(p.X, p.Y);
            if (item == null)
                return false;

            Rectangle row = item.GetBounds(ItemBoundsPortion.Entire);
            if (RowOf(item)?.HasChildren == true && ClickArea(GetGlyphBounds(item, row)).Contains(p))
            {
                Focus();
                ExpandRequested?.Invoke(item, null);
                return true;
            }
            if (ClickArea(GetCheckBoxBounds(item, row)).Contains(p))
            {
                Focus();
                SetChecked(item, !item.Checked);
                return true;
            }
            return false;
        }

        protected override void OnItemCheck(ItemCheckEventArgs ice)
        {
            // Clicks on the native check box area, double clicks and Space would otherwise toggle the item by themselves.
            if (_nativeInputDepth > 0 && _allowCheckDepth == 0)
                ice.NewValue = ice.CurrentValue;
            base.OnItemCheck(ice);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space && !e.Control && !e.Alt && SelectedItems.Count > 0)
            {
                bool allChecked = SelectedItems.Cast<ListViewItem>().All(i => i.Checked);
                foreach (ListViewItem item in SelectedItems.Cast<ListViewItem>().ToList())
                    SetChecked(item, !allChecked);
                e.Handled = true;
                e.SuppressKeyPress = true;
                return;
            }
            if ((e.KeyCode == Keys.Right || e.KeyCode == Keys.Left) && !e.Control && !e.Alt && SelectedItems.Count == 1)
            {
                ExpandRequested?.Invoke(SelectedItems[0], e.KeyCode == Keys.Right);
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        // ------------------------------------------------------------ drag and drop (whole mods only)

        protected override void OnItemDrag(ItemDragEventArgs e)
        {
            base.OnItemDrag(e);
            if (SelectedItems.Count == 0 || SelectedItems.Cast<ListViewItem>().Any(i => RowOf(i)?.IsMod != true))
                return;
            this.DoDragDrop(this.SelectedItems, DragDropEffects.Move);
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);

            if (drgevent.Data!.GetDataPresent("System.Windows.Forms.ListView+SelectedListViewItemCollection"))
                drgevent.Effect = DragDropEffects.Move;
            else
                drgevent.Effect = DragDropEffects.None;
        }

        protected override void OnDragOver(DragEventArgs drgevent)
        {
            base.OnDragOver(drgevent);

            if (this.SelectedItems.Count == 0 || this.Items.Count == 0)
                return;

            Point localPoint = this.PointToClient(new Point(drgevent.X, drgevent.Y));
            ListViewItem? hoverItem = this.GetItemAt(localPoint.X, localPoint.Y);
            ListViewItem? topItem = this.TopItem;

            if (hoverItem == null || topItem == null)
                return;

            // Auto-scroll when dragging near the top or bottom edge (edge size scales with DPI).
            int i = this.Items.IndexOf(topItem);
            int rowHeight = topItem.Bounds.Height;
            int header = topItem.Bounds.Top;

            if (localPoint.Y < header + rowHeight / 2 && i > 0)
                this.TopItem = this.Items[i - 1];
            else if (localPoint.Y > this.ClientSize.Height - LogicalToDeviceUnits(10) && i + 1 < this.Items.Count)
                this.TopItem = this.Items[i + 1];
        }
    }

    public static class ListViewExtensions
    {
        /// <summary>
        /// Sets the double buffered property of a list view to the specified value
        /// </summary>
        /// <param name="listView">The List view</param>
        /// <param name="doubleBuffered">Double Buffered or not</param>
        public static void SetDoubleBuffered(this ListView listView, bool doubleBuffered = true)
        {
            listView.GetType().GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(listView, doubleBuffered, null);
        }
    }
}
