using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms.VisualStyles;

namespace s3mo
{
    /// <summary>
    /// List icons, loaded once. (Every Properties.Resources access creates a new Bitmap, so reading them in a paint
    /// handler allocated a bitmap per row per repaint.)
    /// </summary>
    internal static class Icons
    {
        public static readonly Image ErrorMinus = Properties.Resources.error_minus;
        public static readonly Image ErrorPlus = Properties.Resources.error_plus;
        public static readonly Image ErrorPlusMinus = Properties.Resources.error_plusminus;
        public static readonly Image PackageMinus = Properties.Resources.package_minus;
        public static readonly Image Warning = Properties.Resources.icons8_general_warning_sign_32;

        /// <summary>A "?" in a circle, in the colour of the Info icon (drawn large and scaled down like the other toolbar icons).</summary>
        public static readonly Image Help = MakeHelpIcon(128);

        private static Image MakeHelpIcon(int size)
        {
            Color fill = Color.FromArgb(41, 150, 243);
            try
            {
                using Bitmap info = new Bitmap(Properties.Resources.icons8_info_32);
                Color c = info.GetPixel(info.Width / 8 + 1, info.Height / 2); // left part of its circle, away from the "i"
                if (c.A > 200)
                    fill = Color.FromArgb(255, c);
            }
            catch { /* keep the default blue */ }

            Bitmap bmp = new Bitmap(size, size);
            using Graphics g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float inset = size / 16f; // the icons8 circles leave a 2px margin at 32px
            using (SolidBrush b = new SolidBrush(fill))
                g.FillEllipse(b, inset, inset, size - 2 * inset, size - 2 * inset);

            using GraphicsPath path = new GraphicsPath();
            using (FontFamily family = new FontFamily(GenericFontFamilies.SansSerif))
            {
                FontFamily face = FontFamily.Families.FirstOrDefault(f => f.Name == "Segoe UI") ?? family;
                path.AddString("?", face, (int)FontStyle.Bold, size * 0.66f, PointF.Empty, StringFormat.GenericTypographic);
            }
            RectangleF r = path.GetBounds();
            using (Matrix m = new Matrix())
            {
                m.Translate(size / 2f - (r.X + r.Width / 2f), size / 2f - (r.Y + r.Height / 2f));
                path.Transform(m);
            }
            g.FillPath(Brushes.White, path);
            return bmp;
        }
    }

    /// <summary>
    /// Helpers for drawing and layout that follow the window's DPI.
    /// All hard-coded pixel values in the owner-drawn lists go through here, as "96-DPI pixels".
    /// </summary>
    internal static class Dpi
    {
        private static readonly Dictionary<(Image, int), Bitmap> _iconCache = new();

        /// <summary>The image resized to size×size device pixels (cached per image instance - pass long-lived images such as <see cref="Icons"/>; don't dispose the result).</summary>
        public static Image Icon(Image source, int size)
        {
            if (source.Width == size && source.Height == size)
                return source;

            lock (_iconCache)
            {
                if (_iconCache.TryGetValue((source, size), out Bitmap? cached))
                    return cached;

                // Don't copy the source's resolution: some of the icons claim 2-3 DPI in their file metadata.
                Bitmap bmp = new Bitmap(size, size);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = size % source.Width == 0 ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(source, new Rectangle(0, 0, size, size));
                }
                _iconCache[(source, size)] = bmp;
                return bmp;
            }
        }

        /// <summary>
        /// Draws an icon at exactly size×size device pixels. Always pass the size: Graphics.DrawImage(image, x, y)
        /// sizes the image by the DPI stored in the file, and the flag icons claim 2-3 DPI, so they came out
        /// hundreds of pixels wide and only a transparent corner showed in the cell.
        /// </summary>
        public static void DrawIcon(Graphics g, Image source, int x, int y, int size) =>
            g.DrawImage(Icon(source, size), new Rectangle(x, y, size, size));

        /// <summary>Width the columns of a list can use without a horizontal scroll bar (room is kept for the vertical one).</summary>
        public static int ColumnSpace(ListView lv)
        {
            int sb = SystemInformation.GetVerticalScrollBarWidthForDpi(lv.DeviceDpi);
            bool verticalShown = lv.Width - lv.ClientSize.Width >= sb;
            return Math.Max(0, lv.ClientSize.Width - (verticalShown ? 0 : sb));
        }

        /// <summary>Makes the given columns share whatever width the other columns leave.</summary>
        public static void FitColumns(ListView lv, params ColumnHeader[] fill)
        {
            if (fill.Length == 0 || !lv.IsHandleCreated)
                return;
            int others = 0;
            foreach (ColumnHeader c in lv.Columns)
                if (Array.IndexOf(fill, c) < 0)
                    others += c.Width;
            int each = Math.Max(lv.LogicalToDeviceUnits(60), (ColumnSpace(lv) - others) / fill.Length);
            foreach (ColumnHeader c in fill)
                if (c.Width != each)
                    c.Width = each;
        }

        /// <summary>Keeps the given columns filling the list whenever the list changes size.</summary>
        public static void FillWithColumns(ListView lv, params ColumnHeader[] fill)
        {
            lv.SizeChanged += (s, e) => FitColumns(lv, fill);
            lv.HandleCreated += (s, e) => FitColumns(lv, fill);
        }

        /// <summary>Paints a cell background (instead of setting SubItem.BackColor, which repaints the whole list every time it changes).</summary>
        public static void FillCell(Graphics g, Rectangle bounds, Color color)
        {
            using SolidBrush brush = new SolidBrush(color);
            g.FillRectangle(brush, bounds);
        }

        /// <summary>Draws a themed check box scaled to the given rectangle.</summary>
        public static void DrawCheckBox(Graphics g, Rectangle box, bool isChecked) => DrawCheckBox(g, box, isChecked ? CheckState.Checked : CheckState.Unchecked, true);

        /// <summary>Draws a themed check box (ticked, empty or partly ticked; greyed when not enabled) scaled to the rectangle.</summary>
        public static void DrawCheckBox(Graphics g, Rectangle box, CheckState state, bool enabled)
        {
            if (Application.RenderWithVisualStyles)
            {
                VisualStyleElement element = (state, enabled) switch
                {
                    (CheckState.Checked, true) => VisualStyleElement.Button.CheckBox.CheckedNormal,
                    (CheckState.Checked, false) => VisualStyleElement.Button.CheckBox.CheckedDisabled,
                    (CheckState.Indeterminate, true) => VisualStyleElement.Button.CheckBox.MixedNormal,
                    (CheckState.Indeterminate, false) => VisualStyleElement.Button.CheckBox.MixedDisabled,
                    (_, true) => VisualStyleElement.Button.CheckBox.UncheckedNormal,
                    _ => VisualStyleElement.Button.CheckBox.UncheckedDisabled,
                };
                if (VisualStyleRenderer.IsElementDefined(element))
                {
                    new VisualStyleRenderer(element).DrawBackground(g, box);
                    return;
                }
            }
            if (state == CheckState.Indeterminate)
                ControlPaint.DrawMixedCheckBox(g, box, ButtonState.Checked | ButtonState.Flat | (enabled ? 0 : ButtonState.Inactive));
            else
                ControlPaint.DrawCheckBox(g, box, (state == CheckState.Checked ? ButtonState.Checked : ButtonState.Normal) | ButtonState.Flat | (enabled ? 0 : ButtonState.Inactive));
        }

        /// <summary>
        /// Draws the expand/collapse arrow of a tree row: a chevron like Explorer's, pointing right when closed and
        /// down when open. It's drawn as lines sized to the rectangle (theme glyphs come in fixed pixel sizes that
        /// don't follow per-monitor scaling), in the row's text color so it shows on selected rows too.
        /// </summary>
        public static void DrawExpandGlyph(Graphics g, Rectangle r, bool expanded, Color color)
        {
            float half = r.Width * 0.18f;            // half the chevron's long side
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f;
            PointF[] pts = expanded
                ? new[] { new PointF(cx - half * 1.6f, cy - half * 0.8f), new PointF(cx, cy + half * 0.8f), new PointF(cx + half * 1.6f, cy - half * 0.8f) }
                : new[] { new PointF(cx - half * 0.8f, cy - half * 1.6f), new PointF(cx + half * 0.8f, cy), new PointF(cx - half * 0.8f, cy + half * 1.6f) };

            var oldMode = g.SmoothingMode;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using (Pen pen = new Pen(Color.FromArgb(200, color), Math.Max(1f, r.Width / 10f)))
            {
                pen.StartCap = pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                pen.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                g.DrawLines(pen, pts);
            }
            g.SmoothingMode = oldMode;
        }

        public const TextFormatFlags CellText = TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

        /// <summary>
        /// Keeps the given columns the same size relative to the text when the window moves to a monitor with a
        /// different scale (column widths are plain pixels and aren't always rescaled by WinForms).
        /// </summary>
        public static void KeepColumnsScaled(Form form, params ColumnHeader[] columns)
        {
            form.DpiChanged += (s, e) =>
            {
                double[] logical = columns.Select(c => c.Width * 96.0 / e.DeviceDpiOld).ToArray();
                form.BeginInvoke(() =>
                {
                    for (int i = 0; i < columns.Length; i++)
                        columns[i].Width = (int)Math.Round(logical[i] * e.DeviceDpiNew / 96.0);
                });
            };
        }

        // ------------------------------------------------------------ saved window layout

        /// <summary>
        /// Window sizes, splitter positions and column widths are saved in pixels together with the DPI they were
        /// measured at ("&lt;prefix&gt;Dpi"). Settings from older versions have no DPI value; they were measured at 96 DPI
        /// whenever the compatibility override was on, so 96 is assumed.
        /// </summary>
        public static void SaveLayoutDpi(string prefix, Control c) => Settings.SetSettings(prefix + "Dpi", c.DeviceDpi);

        public static bool TryGetScaled(string prefix, string key, Control c, out int value)
        {
            value = 0;
            if (!Settings.TryGetSettings(key, out string s) || !int.TryParse(s, out int raw))
                return false;
            int savedDpi = Settings.TryGetSettings(prefix + "Dpi", out string d) && int.TryParse(d, out int dpi) && dpi > 0 ? dpi : 96;
            value = (int)Math.Round(raw * (double)c.DeviceDpi / savedDpi);
            return true;
        }

        public static void RestoreColumnWidth(string prefix, string key, ColumnHeader column, Control owner)
        {
            if (TryGetScaled(prefix, key, owner, out int w) && w > 0)
                column.Width = w;
        }

        public static void RestoreSplitter(string prefix, string key, SplitContainer split)
        {
            if (!TryGetScaled(prefix, key, split, out int v))
                return;
            int extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
            int max = extent - split.Panel2MinSize - split.SplitterWidth;
            if (max <= split.Panel1MinSize)
                return;
            try { split.SplitterDistance = Math.Clamp(v, split.Panel1MinSize, max); }
            catch (Exception) { /* keep the designer default */ }
        }

        /// <summary>Restores a form's saved position and size, keeping it on a screen that exists now.</summary>
        public static void RestoreBounds(Form form, string prefix, string xKey, string yKey, string widthKey, string heightKey)
        {
            Rectangle bounds = form.Bounds;
            bool moved = false;

            if (Settings.TryGetSettings(xKey, out string sx) && int.TryParse(sx, out int x) &&
                Settings.TryGetSettings(yKey, out string sy) && int.TryParse(sy, out int y))
            {
                bounds.Location = new Point(x, y);
                moved = true;
            }
            if (TryGetScaled(prefix, widthKey, form, out int w) && TryGetScaled(prefix, heightKey, form, out int h) && w > 100 && h > 100)
                bounds.Size = new Size(w, h);

            // Keep it on a screen that exists now (monitors may have been unplugged or rearranged).
            Rectangle area = Screen.GetWorkingArea(bounds);
            bounds.Width = Math.Min(bounds.Width, area.Width);
            bounds.Height = Math.Min(bounds.Height, area.Height);
            if (!area.Contains(new Point(bounds.X + 40, bounds.Y + 10)))
                bounds.Location = new Point(area.X + (area.Width - bounds.Width) / 2, area.Y + (area.Height - bounds.Height) / 2);

            if (moved)
                form.StartPosition = FormStartPosition.Manual;
            form.Bounds = bounds;
        }
    }
}
