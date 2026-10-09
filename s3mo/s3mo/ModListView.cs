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
    // Drag and drop implementation - https://stackoverflow.com/questions/35198652/listview-dragdrop-re-order-for-listview-in-details-view-code-usage
    //
    // Check boxes: the list is owner-drawn, so the check box you see is painted by us. The native list view still has
    // its own (invisible) check box area whose position scales differently with DPI, which is why clicks used to miss
    // at high DPI. Now the painted box is also the click target, and the native control's own toggling is blocked.
    public class ModListView : ListView
    {
        private const int WM_KEYDOWN = 0x0100, WM_CHAR = 0x0102, WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;

        private int _nativeInputDepth;
        private int _allowCheckDepth;

        /// <summary>True while dragged items are being re-inserted (ItemChecked fires for them; it's not a user change).</summary>
        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public bool IsReordering { get; private set; }

        /// <summary>Changes an item's check state (the only way it changes; native toggling is blocked).</summary>
        public void SetChecked(ListViewItem item, bool value)
        {
            if (item.Checked == value)
                return;
            _allowCheckDepth++;
            try { item.Checked = value; }
            finally { _allowCheckDepth--; }
        }

        /// <summary>Where the check box is painted in a row (also its click area).</summary>
        public Rectangle GetCheckBoxBounds(Rectangle rowBounds)
        {
            int size = LogicalToDeviceUnits(13);
            return new Rectangle(rowBounds.X + LogicalToDeviceUnits(6), rowBounds.Y + (rowBounds.Height - size) / 2, size, size);
        }

        protected override void WndProc(ref Message m)
        {
            bool input = m.Msg == WM_LBUTTONDOWN || m.Msg == WM_LBUTTONUP || m.Msg == WM_LBUTTONDBLCLK || m.Msg == WM_KEYDOWN || m.Msg == WM_CHAR;
            if (input) _nativeInputDepth++;
            try { base.WndProc(ref m); }
            finally { if (input) _nativeInputDepth--; }
        }

        protected override void OnItemCheck(ItemCheckEventArgs ice)
        {
            // Clicks on the native check box area, double clicks and Space would otherwise toggle the item by themselves.
            if (_nativeInputDepth > 0 && _allowCheckDepth == 0)
                ice.NewValue = ice.CurrentValue;
            base.OnItemCheck(ice);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                ListViewItem? item = GetItemAt(e.X, e.Y);
                if (item != null)
                {
                    Rectangle box = GetCheckBoxBounds(item.GetBounds(ItemBoundsPortion.Entire));
                    box.Inflate(LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
                    if (box.Contains(e.Location))
                        SetChecked(item, !item.Checked); // a double click on the box counts as two clicks, like a normal check box
                }
            }
            base.OnMouseDown(e);
        }

        /// <summary>True if the point is on a row's check box (used to not open the mod window on a double click there).</summary>
        public bool IsOnCheckBox(Point p)
        {
            ListViewItem? item = GetItemAt(p.X, p.Y);
            if (item == null)
                return false;
            Rectangle box = GetCheckBoxBounds(item.GetBounds(ItemBoundsPortion.Entire));
            box.Inflate(LogicalToDeviceUnits(2), LogicalToDeviceUnits(2));
            return box.Contains(p);
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
            base.OnKeyDown(e);
        }

        protected override void OnItemDrag(ItemDragEventArgs e)
        {
            base.OnItemDrag(e);
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

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            //Return if the items are not selected in the ListView control.
            if (this.SelectedItems.Count == 0)
                return;

            //Returns the location of the mouse pointer in the ListView control.
            Point cp = this.PointToClient(new Point(drgevent.X, drgevent.Y));

            //Obtain the item that is located at the specified location of the mouse pointer.
            ListViewItem? dragToItem = this.GetItemAt(cp.X, cp.Y);
            if (dragToItem == null)
                return;

            //Obtain the index of the item at the mouse pointer.
            int dragIndex = dragToItem.Index;
            ListViewItem[] sel = new ListViewItem[this.SelectedItems.Count];

            for (int i = 0; i <= this.SelectedItems.Count - 1; i++)
                sel[i] = this.SelectedItems[i];

            IsReordering = true;
            _allowCheckDepth++; // re-inserted items keep their check state
            BeginUpdate();
            try
            {
                for (int i = 0; i < sel.GetLength(0); i++)
                {
                    //Obtain the ListViewItem to be dragged to the target location.
                    ListViewItem dragItem = sel[i];
                    int itemIndex = dragIndex;

                    if (itemIndex == dragItem.Index)
                        return;

                    if (dragItem.Index < itemIndex)
                        itemIndex++;
                    else
                        itemIndex = dragIndex + i;

                    //Insert the item at the mouse pointer.
                    ListViewItem insertItem = (ListViewItem)dragItem.Clone();

                    this.Items.Insert(itemIndex, insertItem);
                    insertItem.Selected = true;

                    //Removes the item from the initial location while
                    //the item is moved to the new location.
                    this.Items.Remove(dragItem);
                }
            }
            finally
            {
                EndUpdate();
                _allowCheckDepth--;
                IsReordering = false;
            }

            base.OnDragDrop(drgevent);
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
