namespace s3mo
{
    /// <summary>
    /// A list view for owner-drawn rows that also paints the parts of the list no row covers.
    ///
    /// When rows are drawn by the program, the strip to the right of the last column and the space below the last
    /// row are left unpainted, so whatever was in the drawing buffer shows through (the odd yellow bar at the bottom
    /// of the mod list). After the list paints itself, those areas are filled with the background colour.
    /// </summary>
    public class OwnerDrawListView : ListView
    {
        private const int WM_PAINT = 0x000F;

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == WM_PAINT && OwnerDraw && View == View.Details && IsHandleCreated)
                PaintEmptyAreas();
        }

        private void PaintEmptyAreas()
        {
            Rectangle client = ClientRectangle;
            ListViewItem? top = TopItem;
            if (client.Width <= 0 || client.Height <= 0 || top == null || Items.Count == 0)
                return;

            // Rows start right below the header; item bounds are in client coordinates (already scrolled).
            Rectangle first = top.GetBounds(ItemBoundsPortion.Entire);
            Rectangle last = Items[Items.Count - 1].GetBounds(ItemBoundsPortion.Entire);
            int rowsTop = Math.Max(client.Top, first.Top);

            using Graphics g = CreateGraphics();
            using SolidBrush brush = new SolidBrush(BackColor);

            if (first.Right < client.Right)
                g.FillRectangle(brush, first.Right, rowsTop, client.Right - first.Right, client.Bottom - rowsTop);

            int below = Math.Max(rowsTop, last.Bottom);
            if (below < client.Bottom)
                g.FillRectangle(brush, client.Left, below, client.Width, client.Bottom - below);
        }
    }
}
