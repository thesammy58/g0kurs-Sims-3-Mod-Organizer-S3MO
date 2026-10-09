using System.Drawing.Drawing2D;
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

                Bitmap bmp = new Bitmap(size, size);
                bmp.SetResolution(source.HorizontalResolution, source.VerticalResolution);
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

        /// <summary>Width of a vertical scroll bar plus the list's border, at the control's DPI.</summary>
        public static int ScrollBarAllowance(Control c) =>
            SystemInformation.GetVerticalScrollBarWidthForDpi(c.DeviceDpi) + c.LogicalToDeviceUnits(4);

        /// <summary>Draws a themed check box scaled to the given rectangle.</summary>
        public static void DrawCheckBox(Graphics g, Rectangle box, bool isChecked)
        {
            if (Application.RenderWithVisualStyles)
            {
                VisualStyleElement element = isChecked ? VisualStyleElement.Button.CheckBox.CheckedNormal : VisualStyleElement.Button.CheckBox.UncheckedNormal;
                if (VisualStyleRenderer.IsElementDefined(element))
                {
                    new VisualStyleRenderer(element).DrawBackground(g, box);
                    return;
                }
            }
            ControlPaint.DrawCheckBox(g, box, (isChecked ? ButtonState.Checked : ButtonState.Normal) | ButtonState.Flat);
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
