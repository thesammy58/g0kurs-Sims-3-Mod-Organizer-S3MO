using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace s3mo
{
    /// <summary>
    /// A short guide to what the colours, icons and buttons mean. It stays open next to the main window (it doesn't
    /// block it), so the list can be compared with the legend.
    /// </summary>
    public sealed class HelpForm : Form
    {
        public HelpForm(IReadOnlyDictionary<string, Image> toolbarIcons, Image helpIcon)
        {
            SuspendLayout();
            Text = "Quick guide";
            AutoScaleDimensions = new SizeF(7F, 15F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(600, 680);
            MinimumSize = new Size(380, 300);
            StartPosition = FormStartPosition.Manual;
            MinimizeBox = false;
            ShowInTaskbar = false;
            KeyPreview = true;
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            var view = new HelpView(BuildContent(toolbarIcons, helpIcon)) { Dock = DockStyle.Fill };
            Controls.Add(view);
            Shown += (s, e) => view.Focus();
            ResumeLayout(false); // scales the sizes above to the current DPI, like the designer-made windows
        }

        // ------------------------------------------------------------------ content

        private static List<HelpView.Item> BuildContent(IReadOnlyDictionary<string, Image> tb, Image helpIcon)
        {
            var items = new List<HelpView.Item>();
            void H(string text) => items.Add(HelpView.Item.Heading(text));
            void P(string text) => items.Add(HelpView.Item.Paragraph(text));
            void E(HelpView.Glyph glyph, string title, string text) => items.Add(HelpView.Item.Entry(glyph, title, text));
            HelpView.Glyph Tool(string name) => tb.TryGetValue(name, out Image? img) ? HelpView.Glyph.Picture(img, 24) : HelpView.Glyph.None;

            H("Mod list: row colours");
            P("Select a mod or folder to see what it conflicts with. Two packages conflict when they contain the same resource; only one of them can be used in the game.");
            E(HelpView.Glyph.Swatch(Color.LightGreen), "Green", "The selected mod or folder overwrites this one, so this row's version isn't used.");
            E(HelpView.Glyph.Swatch(Color.LightCoral), "Red", "This row overwrites the selected mod or folder.");
            E(HelpView.Glyph.Swatch(Color.Khaki), "Yellow", "Some of both. This only happens between folders of the same mod.");
            P("Folders inside a coloured mod are coloured too, down to the folder that holds the conflicting package, so opening the mod shows where the conflict is.");

            H("Mod list: check boxes and folders");
            E(HelpView.Glyph.CheckBox(CheckState.Checked), "Ticked", "Included in the build.");
            E(HelpView.Glyph.CheckBox(CheckState.Unchecked), "Unticked", "Left out of the build and out of the conflict check.");
            E(HelpView.Glyph.CheckBox(CheckState.Indeterminate), "Partly ticked", "The mod or folder is on, but some folders inside it are off.");
            E(HelpView.Glyph.GreyText(), "Grey name", "Off because the mod or a folder above it is unticked. Its own tick is kept for when you switch the mod back on.");
            E(HelpView.Glyph.Chevron(), "Arrow", "Shows or hides the folders inside. You can also double-click a folder, or use the Left and Right arrow keys.");

            H("Flags");
            E(HelpView.Glyph.Picture(Icons.PackageMinus, 16), "Overlap inside", "Packages inside this mod or folder contain the same resources. Inside a mod, the package that comes later by name (including its folder) wins.");
            E(HelpView.Glyph.Picture(Icons.ErrorPlus, 16), "Overwrites", "Overwrites resources of other mods (mods with a lower priority). In the Files list: overwrites other packages of the same mod.");
            E(HelpView.Glyph.Picture(Icons.ErrorMinus, 16), "Overwritten", "Some of its resources are replaced by other mods (mods with a higher priority). In the Files list: by other packages of the same mod.");
            E(HelpView.Glyph.Picture(Icons.ErrorPlusMinus, 16), "Both", "Overwrites some resources and is overwritten for others.");
            E(HelpView.Glyph.Picture(Icons.Warning, 16), "Couldn't be read", "In the Files list: the package is damaged or isn't a Sims 3 package. It's left out of builds. Double-click it to see why.");

            H("Priority");
            P("When two mods contain the same resource, the one with the higher priority number (lower in the list) wins. Drag mods to change the order; folders move with their mod. Unticked mods have no number.");

            H("Files list");
            P("Shows the files directly inside the selected mod or folder. The flags there only compare packages of the same mod. Select a file to colour the other files: green ones are overwritten by it, red ones overwrite it.");
            P("Double-click a file to look inside it. Double-click a mod for its details; the Conflicts tab lists exactly which resources win and lose against other mods.");

            H("Toolbar");
            E(Tool("about"), "Info", "Version and credits.");
            E(HelpView.Glyph.Picture(helpIcon, 24), "Quick guide", "This window.");
            E(Tool("install"), "Install", "Adds .package, .sims3pack or .zip files as a new mod. Folders inside a zip are kept.");
            E(Tool("options"), "Settings", "Where the game and its Documents folder are, how big each merged file may get, the priority of the merged files in Resource.cfg, and which game caches the broom button clears.");
            E(Tool("profile"), "Profiles", "Separate setups with their own mod order and ticks. Every profile uses the same Mods folder.");
            E(Tool("openFolder"), "Folders and backups", "Opens the game folder, the Sims 3 Documents folder or the organizer's folder. Also makes a backup of the game's Mods folder, and opens your backups.");
            E(Tool("clearCache"), "Clear caches", "Deletes the game's cache files chosen in Settings. Worth doing after changing mods.");
            E(Tool("refresh"), "Refresh", "Reads the Mods folder again, for example after adding files in Explorer.");
            E(Tool("build"), "Build", "Merges the ticked mods into a few large files in Mods\\ModBuilds, in the game's Documents folder, which the game loads faster than many small files. Only changes are written. Right-click or Shift+click for a full rebuild. Before the first build, it offers to back up the game's Mods folder.");
            E(Tool("warning"), "Needs rebuild", "Lit when the merged files don't match the list anymore. Point at it to see what changed.");
            E(Tool("play"), "Play", "Starts the game.");

            H("Handy keys");
            P("Space ticks or unticks the selected rows. Right-click a row to switch several on or off at once, rename or delete a mod, or open it in Explorer. Press Esc to close this window.");
            return items;
        }

        // ------------------------------------------------------------------ view

        /// <summary>Lays out and paints the guide; everything is measured with the current font and DPI.</summary>
        private sealed class HelpView : ScrollableControl
        {
            public sealed class Glyph
            {
                public static readonly Glyph None = new Glyph((g, r, v) => { });
                public readonly Action<Graphics, Rectangle, HelpView> Draw;
                private Glyph(Action<Graphics, Rectangle, HelpView> draw) => Draw = draw;

                public static Glyph Swatch(Color c) => new Glyph((g, r, v) =>
                {
                    Rectangle box = Center(r, v.L(22), v.L(14));
                    using (var b = new SolidBrush(c)) g.FillRectangle(b, box);
                    using (var p = new Pen(SystemColors.ControlDark)) g.DrawRectangle(p, box);
                });

                public static Glyph CheckBox(CheckState state) => new Glyph((g, r, v) => Dpi.DrawCheckBox(g, Center(r, v.L(13), v.L(13)), state, true));

                public static Glyph Chevron() => new Glyph((g, r, v) => Dpi.DrawExpandGlyph(g, Center(r, v.L(16), v.L(16)), false, SystemColors.ControlText));

                public static Glyph GreyText() => new Glyph((g, r, v) =>
                    TextRenderer.DrawText(g, "Abc", v.Font, r, SystemColors.GrayText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding));

                public static Glyph Picture(Image image, int logicalSize) => new Glyph((g, r, v) =>
                {
                    int s = v.L(logicalSize);
                    Rectangle box = Center(r, s, s);
                    Dpi.DrawIcon(g, image, box.X, box.Y, s);
                });

                private static Rectangle Center(Rectangle r, int w, int h) => new Rectangle(r.X + (r.Width - w) / 2, r.Y + (r.Height - h) / 2, w, h);
            }

            public sealed class Item
            {
                public enum Kind { Heading, Paragraph, Entry }
                public Kind Type;
                public string Title = "", Text = "";
                public Glyph Icon = Glyph.None;
                internal Rectangle Bounds, TitleBounds, TextBounds, IconBounds; // laid out

                public static Item Heading(string text) => new Item { Type = Kind.Heading, Title = text };
                public static Item Paragraph(string text) => new Item { Type = Kind.Paragraph, Text = text };
                public static Item Entry(Glyph icon, string title, string text) => new Item { Type = Kind.Entry, Icon = icon, Title = title, Text = text };
            }

            private readonly List<Item> _items;
            private Font? _headingFont, _boldFont;
            private int _laidOutWidth = -1;

            private const TextFormatFlags Wrap = TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPadding;

            public HelpView(List<Item> items)
            {
                _items = items;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
                BackColor = SystemColors.Window;
                AutoScroll = true;
                TabStop = true;
            }

            public int L(int logical) => LogicalToDeviceUnits(logical);

            protected override void OnFontChanged(EventArgs e)
            {
                _headingFont?.Dispose(); _boldFont?.Dispose();
                _headingFont = _boldFont = null;
                _laidOutWidth = -1;
                base.OnFontChanged(e);
                Relayout();
            }

            protected override void OnSizeChanged(EventArgs e)
            {
                base.OnSizeChanged(e);
                Relayout();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                Focus(); // so the wheel and keys scroll
                base.OnMouseDown(e);
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                int line = L(40);
                int y = -AutoScrollPosition.Y;
                switch (e.KeyCode)
                {
                    case Keys.Down: y += line; break;
                    case Keys.Up: y -= line; break;
                    case Keys.PageDown: case Keys.Space: y += ClientSize.Height - line; break;
                    case Keys.PageUp: y -= ClientSize.Height - line; break;
                    case Keys.Home: y = 0; break;
                    case Keys.End: y = int.MaxValue / 2; break;
                    default: base.OnKeyDown(e); return;
                }
                AutoScrollPosition = new Point(0, Math.Max(0, y));
                e.Handled = true;
            }

            protected override bool IsInputKey(Keys keyData) =>
                keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End || base.IsInputKey(keyData);

            private Font HeadingFont => _headingFont ??= new Font(Font.FontFamily, Font.Size * 1.2f, FontStyle.Bold);
            private Font BoldFont => _boldFont ??= new Font(Font, FontStyle.Bold);

            private void Relayout()
            {
                if (!IsHandleCreated && Parent == null)
                    return;
                // The text width ignores the scroll bar so the layout doesn't flip back and forth when it appears.
                int width = Width - SystemInformation.GetVerticalScrollBarWidthForDpi(this.DeviceDpi);
                if (width == _laidOutWidth || width <= 0)
                    return;
                _laidOutWidth = width;

                int margin = L(16), iconColumn = L(40), gap = L(8);
                int textWidth = Math.Max(L(80), width - 2 * margin);
                int y = L(12);
                bool first = true;
                foreach (Item it in _items)
                {
                    switch (it.Type)
                    {
                        case Item.Kind.Heading:
                            if (!first) y += L(14);
                            Size hs = TextRenderer.MeasureText(it.Title, HeadingFont, new Size(textWidth, int.MaxValue), Wrap);
                            it.TitleBounds = new Rectangle(margin, y, textWidth, hs.Height);
                            it.Bounds = it.TitleBounds;
                            y += hs.Height + L(6);
                            break;

                        case Item.Kind.Paragraph:
                            Size ps = TextRenderer.MeasureText(it.Text, Font, new Size(textWidth, int.MaxValue), Wrap);
                            it.TextBounds = new Rectangle(margin, y, textWidth, ps.Height);
                            it.Bounds = it.TextBounds;
                            y += ps.Height + L(8);
                            break;

                        case Item.Kind.Entry:
                            int x = margin + iconColumn + gap;
                            int w = Math.Max(L(60), width - margin - x);
                            Size ts = TextRenderer.MeasureText(it.Title, BoldFont, new Size(w, int.MaxValue), Wrap);
                            Size ds = TextRenderer.MeasureText(it.Text, Font, new Size(w, int.MaxValue), Wrap);
                            it.TitleBounds = new Rectangle(x, y, w, ts.Height);
                            it.TextBounds = new Rectangle(x, y + ts.Height, w, ds.Height);
                            int h = Math.Max(ts.Height + ds.Height, L(28));
                            it.IconBounds = new Rectangle(margin, y, iconColumn, Math.Min(h, Math.Max(L(28), ts.Height + L(8))));
                            it.Bounds = new Rectangle(margin, y, width - 2 * margin, h);
                            y += h + L(8);
                            break;
                    }
                    first = false;
                }
                AutoScrollMinSize = new Size(0, y + L(8));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                if (_laidOutWidth < 0)
                    Relayout();
                Graphics g = e.Graphics;
                g.Clear(BackColor);
                // Everything is drawn at its laid-out position moved by the scroll offset (no transform: TextRenderer
                // and GDI+ don't treat transforms the same way).
                Rectangle visible = new Rectangle(0, -AutoScrollPosition.Y, Width, ClientSize.Height);
                Color text = SystemColors.WindowText;

                foreach (Item it in _items)
                {
                    if (!it.Bounds.IntersectsWith(visible))
                        continue;
                    switch (it.Type)
                    {
                        case Item.Kind.Heading:
                            TextRenderer.DrawText(g, it.Title, HeadingFont, Shift(it.TitleBounds), SystemColors.HotTrack, Wrap);
                            break;
                        case Item.Kind.Paragraph:
                            TextRenderer.DrawText(g, it.Text, Font, Shift(it.TextBounds), text, Wrap);
                            break;
                        case Item.Kind.Entry:
                            GraphicsState state = g.Save();
                            g.SmoothingMode = SmoothingMode.None;
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            it.Icon.Draw(g, Shift(it.IconBounds), this);
                            g.Restore(state);
                            TextRenderer.DrawText(g, it.Title, BoldFont, Shift(it.TitleBounds), text, Wrap);
                            TextRenderer.DrawText(g, it.Text, Font, Shift(it.TextBounds), text, Wrap);
                            break;
                    }
                }
            }

            private Rectangle Shift(Rectangle r) => new Rectangle(r.X, r.Y + AutoScrollPosition.Y, r.Width, r.Height);

            protected override void Dispose(bool disposing)
            {
                if (disposing) { _headingFont?.Dispose(); _boldFont?.Dispose(); }
                base.Dispose(disposing);
            }
        }
    }
}
