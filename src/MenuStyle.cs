using System;
using System.Collections.Generic;
using System.Linq;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace CLTaskbar
{
    // Builds menus that look like the Windows 10 / 11 shell menus (jump-list style), in light or dark.
    // Per-item look for styled menus
    internal class ItemLook
    {
        public Color? Back, Fore;
        public Bitmap Picture;       // drawn centered (for picture-only items)
        public int Height;
    }

    internal static class Menus
    {
        public static ContextMenuStrip Create(Theme t, float scale)
        {
            var m = new ContextMenuStrip();
            Style(m, t, scale);
            m.HandleCreated += (s, e) => { Native.NoOpenAnimation(m.Handle); if (!Theme.IsWin10) Native.RoundCorners(m.Handle); };
            m.Closed += (s, e) => m.BeginInvoke((Action)(() => m.Dispose()));
            return m;
        }

        public static void Style(ToolStripDropDownMenu m, Theme t, float scale, Font font = null)
        {
            m.Renderer = new ShellMenuRenderer(t, scale);
            m.Font = font ?? new Font("Segoe UI", 9f);
            m.ShowItemToolTips = true;
            m.BackColor = t.PopupBg;
            m.ForeColor = t.Text;
            m.ShowImageMargin = true;
            m.ShowCheckMargin = false;
            int icon = Math.Max(16, (int)Math.Round(16 * scale));
            m.ImageScalingSize = new Size(icon, icon);
            m.Padding = new Padding(0, (int)(4 * scale), 0, (int)(4 * scale));
            m.DropShadowEnabled = true;
        }

        // Applies the style to every sub-menu too (call after the menu is filled)
        public static void StyleTree(ToolStripItemCollection items, Theme t, float scale, Font font = null)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripMenuItem mi && mi.HasDropDownItems)
                {
                    Style((ToolStripDropDownMenu)mi.DropDown, t, scale, font);
                    // Sub-menus open straight to the side of their item (flipping left only when there's no room),
                    // not "above" like the taskbar menu they came from, which left a gap and pushed them upward
                    mi.DropDownDirection = ToolStripDropDownDirection.Right;
                    var dd = mi.DropDown;
                    dd.HandleCreated += (s, e) => { Native.NoOpenAnimation(dd.Handle); if (!Theme.IsWin10) Native.RoundCorners(dd.Handle); };
                    // Open the sub-menu as soon as you point at it (Windows otherwise waits about half a second)
                    var sub = mi;
                    sub.MouseEnter += (s, e) =>
                    {
                        if (!sub.Enabled || sub.DropDown.Visible || sub.Owner == null) return;
                        foreach (ToolStripItem o in sub.Owner.Items)
                            if (o != sub && o is ToolStripMenuItem om && om.HasDropDownItems && om.DropDown.Visible) om.HideDropDown();
                        sub.ShowDropDown();
                    };
                    StyleTree(mi.DropDownItems, t, scale, font);
                }
                Pad(it, scale);
            }
        }

        static void Pad(ToolStripItem it, float scale)
        {
            if (it is ToolStripSeparator) return;
            it.Padding = new Padding(0, (int)(3 * scale), (int)(12 * scale), (int)(3 * scale));
        }

        public static ToolStripMenuItem Item(ToolStripItemCollection items, string text, Image img, Action onClick, bool bold = false)
        {
            // Menus get their own copy of the icon, so later icon refreshes can't pull it out from under them
            var it = new ToolStripMenuItem(text, img != null ? new Bitmap(img) : null);
            if (onClick != null)
            {
                // Only a left click (or Enter) runs it; a right click on a menu item does nothing
                var last = MouseButtons.Left;
                it.MouseDown += (s, e) => last = e.Button;
                it.Click += (s, e) => { var b = last; last = MouseButtons.Left; if (b == MouseButtons.Right) return; onClick(); };
            }
            if (bold) it.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
            items.Add(it);
            return it;
        }

        // Small grey section title, like "Pinned" / "Tasks" in a jump list
        public static void Header(ToolStripItemCollection items, string text)
        {
            var it = new ToolStripMenuItem(text) { Enabled = false, Tag = "header" };
            it.Font = new Font("Segoe UI", 8.25f, FontStyle.Regular);
            items.Add(it);
        }

        // A long list drawn in several columns inside a menu (instead of a list that scrolls off the screen)
        // Long lists always go into columns (never a list with scroll arrows)
        public static bool TooTall(int count, float scale) => count > 18;

        public static ToolStripControlHost ColumnList(List<(string text, Image icon, Action act)> entries, Theme t, float scale)
        {
            var list = new ColumnListControl(entries, t, scale);
            return new ToolStripControlHost(list) { AutoSize = false, Size = list.Size, Margin = Padding.Empty, Padding = Padding.Empty };
        }

        class ColumnListControl : Control
        {
            readonly List<(string text, Image icon, Action act)> entries;
            readonly Theme t; readonly float s;
            readonly int itemH, colW, rows;
            int hot = -1;

            public ColumnListControl(List<(string text, Image icon, Action act)> e, Theme theme, float scale)
            {
                entries = e; t = theme; s = scale;
                DoubleBuffered = true;
                Font = new Font("Segoe UI", 9f);
                BackColor = t.PopupBg;
                itemH = (int)(26 * s);
                int avail = Screen.PrimaryScreen.WorkingArea.Height - (int)(60 * s);
                rows = Math.Max(1, Math.Min(18, avail / itemH));
                int cols = (e.Count + rows - 1) / rows;
                rows = (e.Count + cols - 1) / cols;   // balance the columns
                using (var g = CreateGraphics())
                    colW = e.Max(x => TextRenderer.MeasureText(g, x.text, Font).Width) + (int)(56 * s);
                Size = new Size(cols * colW, rows * itemH);
            }

            int IndexAt(Point p)
            {
                int c = p.X / colW, r = p.Y / itemH, i = c * rows + r;
                return p.X >= 0 && p.Y >= 0 && r < rows && i < entries.Count ? i : -1;
            }

            protected override void OnMouseMove(MouseEventArgs e) { int i = IndexAt(e.Location); if (i != hot) { hot = i; Invalidate(); } }
            protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                int i = IndexAt(e.Location);
                if (i < 0) return;
                // close the whole menu, then run the item
                var host = Parent as ToolStrip;
                ToolStrip ts = host;
                while (ts is ToolStripDropDown dd) { var owner = dd.OwnerItem; dd.Close(ToolStripDropDownCloseReason.ItemClicked); ts = owner?.Owner; }
                entries[i].act?.Invoke();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(t.PopupBg);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                int icon = Math.Max(16, (int)(16 * s));
                for (int i = 0; i < entries.Count; i++)
                {
                    int c = i / rows, r = i % rows;
                    var rc = new Rectangle(c * colW, r * itemH, colW, itemH);
                    if (i == hot)
                    {
                        var hr = Theme.IsWin10 ? rc : Rectangle.Inflate(rc, -(int)(4 * s), -(int)(1 * s));
                        using (var b = new SolidBrush(t.PopupHover)) g.FillRectangle(b, hr);
                    }
                    if (entries[i].icon != null) g.DrawImage(entries[i].icon, new Rectangle(rc.X + (int)(10 * s), rc.Y + (itemH - icon) / 2, icon, icon));
                    TextRenderer.DrawText(g, entries[i].text, Font, new Rectangle(rc.X + (int)(36 * s), rc.Y, colW - (int)(40 * s), itemH), t.Text,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }

        public static void Sep(ToolStripItemCollection items)
        {
            if (items.Count > 0 && !(items[items.Count - 1] is ToolStripSeparator)) items.Add(new ToolStripSeparator());
        }
    }

    internal class ShellMenuRenderer : ToolStripProfessionalRenderer
    {
        readonly Theme t;
        readonly float s;

        public ShellMenuRenderer(Theme theme, float scale) : base(new Colors(theme)) { t = theme; s = scale; RoundedEdges = false; }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using (var b = new SolidBrush(t.PopupBg)) e.Graphics.FillRectangle(b, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            using (var p = new Pen(t.PopupBorder)) e.Graphics.DrawRectangle(p, r);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e) { }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var look = e.Item.Tag as ItemLook;
            var full = new Rectangle(Point.Empty, e.Item.Size);
            var r = full;
            if (!Theme.IsWin10) r.Inflate(-(int)(4 * s), -(int)(1 * s));
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (look?.Back != null) FillR(g, look.Back.Value, r);
            if (e.Item.Selected && e.Item.Enabled)
                FillR(g, look?.Back != null ? Color.FromArgb(70, t.PopupIsDark() ? Color.White : Color.Black) : t.PopupHover, r);
            if (look?.Picture != null)
            {
                var p = look.Picture;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(p, new Rectangle((full.Width - p.Width) / 2, (full.Height - p.Height) / 2, p.Width, p.Height));
            }
        }

        void FillR(Graphics g, Color c, Rectangle r)
        {
            using (var b = new SolidBrush(c))
            {
                if (Theme.IsWin10) g.FillRectangle(b, r);
                else using (var p = BarForm.RoundRect(r, (int)(4 * s))) g.FillPath(b, p);
            }
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            bool header = "header".Equals(e.Item.Tag);
            e.TextColor = (e.Item.Tag as ItemLook)?.Fore ?? (header ? t.TextDim : e.Item.Enabled ? t.Text : t.TextDim);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
        {
            if (e.Image == null) return;
            var g = e.Graphics;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(e.Image, e.ImageRectangle);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int y = e.Item.Height / 2;
            using (var p = new Pen(t.PopupBorder))
                e.Graphics.DrawLine(p, (int)(10 * s), y, e.Item.Width - (int)(10 * s), y);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = t.Text;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var r = e.ImageRectangle;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(t.Text, Math.Max(1.2f, 1.4f * s)))
                g.DrawLines(p, new[] { new PointF(r.Left + r.Width * .2f, r.Top + r.Height * .55f), new PointF(r.Left + r.Width * .42f, r.Top + r.Height * .75f), new PointF(r.Left + r.Width * .8f, r.Top + r.Height * .28f) });
        }

        class Colors : ProfessionalColorTable
        {
            readonly Theme t;
            public Colors(Theme theme) { t = theme; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground => t.PopupBg;
            public override Color ImageMarginGradientBegin => t.PopupBg;
            public override Color ImageMarginGradientMiddle => t.PopupBg;
            public override Color ImageMarginGradientEnd => t.PopupBg;
            public override Color MenuBorder => t.PopupBorder;
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => t.PopupHover;
            public override Color MenuItemSelectedGradientBegin => t.PopupHover;
            public override Color MenuItemSelectedGradientEnd => t.PopupHover;
            public override Color MenuItemPressedGradientBegin => t.PopupHover;
            public override Color MenuItemPressedGradientEnd => t.PopupHover;
            public override Color SeparatorDark => t.PopupBorder;
            public override Color SeparatorLight => t.PopupBg;
        }
    }
}
