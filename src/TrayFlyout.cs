using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CLTaskbar
{
    // The popup for a "popup grid" group: a grid of icons that opens above its button,
    // like the notification area's ^ button. Click to open, drag to reorder, right-click for options.
    internal class TrayFlyout : Form
    {
        readonly BarForm bar;
        readonly BarSection sec;
        readonly Theme theme;
        readonly float s;
        readonly Rectangle anchor;
        List<LaunchItem> items;
        int cols, rows, cell, icon, pad;
        int hot = -1, pressedIdx = -1, dropIdx = -1;
        Point downAt; bool dragging;
        readonly ToolTip tip = new ToolTip { InitialDelay = 400, ShowAlways = true };
        Color bg, hover;

        public TrayFlyout(BarForm owner, BarSection section, Theme t, float scale, Rectangle anchorRect)
        {
            bar = owner; sec = section; theme = t; s = scale; anchor = anchorRect;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            TopMost = true;
            KeyPreview = true;
            bg = Theme.Parse(sec.MenuBackColor) ?? theme.PopupBg;
            double lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
            hover = Theme.Parse(sec.MenuHoverColor) ?? (lum < 0.5 ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(28, 0, 0, 0));
            BackColor = bg;
            Relayout();
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST; return cp; }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!Theme.IsWin10) Native.RoundCorners(Handle);
        }

        int P(float v) => (int)Math.Round(v * s);

        void Relayout()
        {
            items = sec.Items.Where(i => i.Type == "program" || i.Type == "web").ToList();
            cols = sec.GridShape == "row" ? Math.Max(1, items.Count)
                 : sec.GridShape == "column" ? 1
                 : Math.Max(1, sec.GridColumns > 0 ? sec.GridColumns : 4);
            cols = Math.Min(cols, Math.Max(1, items.Count));
            icon = P(sec.GridIconSize > 0 ? sec.GridIconSize : 24);
            cell = icon + P(16);
            pad = P(6);
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = anchor.X + anchor.Width / 2, Y = anchor.Y }, 2), ref mi);
            var mon = mi.rcMonitor.ToRectangle();
            cols = Math.Max(1, Math.Min(cols, (mon.Width - P(16) - pad * 2) / cell));   // never wider than the screen
            rows = Math.Max(1, (items.Count + cols - 1) / cols);
            var size = new Size(cols * cell + pad * 2, rows * cell + pad * 2);
            int x = anchor.Left + anchor.Width / 2 - size.Width / 2;
            x = Math.Max(mon.Left + P(8), Math.Min(x, mon.Right - P(8) - size.Width));
            Bounds = new Rectangle(x, anchor.Top - size.Height - P(8), size.Width, size.Height);
            Invalidate();
        }

        Rectangle CellRect(int i) => new Rectangle(pad + (i % cols) * cell, pad + (i / cols) * cell, cell, cell);

        int IndexAt(Point p)
        {
            for (int i = 0; i < items.Count; i++) if (CellRect(i).Contains(p)) return i;
            return -1;
        }

        // insertion point while dragging: before the cell under the cursor (or after it on its right half)
        int DropIndexAt(Point p)
        {
            int c = Math.Max(0, Math.Min(cols - 1, (p.X - pad) / cell)), r = Math.Max(0, Math.Min(rows - 1, (p.Y - pad) / cell));
            int i = r * cols + c;
            if (i >= items.Count) return items.Count;
            var rc = CellRect(i);
            return p.X > rc.Left + rc.Width / 2 ? i + 1 : i;
        }

        DateTime shownAt = DateTime.Now;
        protected override void OnShown(EventArgs e) { base.OnShown(e); shownAt = DateTime.Now; }
        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (dragging) return;
            // something (like a menu that was closing) took the focus the instant it opened: take it back
            if ((DateTime.Now - shownAt).TotalMilliseconds < 350 && !IsDisposed)
            {
                BeginInvoke((Action)(() => { if (!IsDisposed) { Activate(); Native.SetForegroundWindow(Handle); } }));
                return;
            }
            Close();
        }
        protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Escape) Close(); base.OnKeyDown(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (pressedIdx >= 0 && e.Button == MouseButtons.Left)
            {
                if (!dragging && (Math.Abs(e.X - downAt.X) > P(5) || Math.Abs(e.Y - downAt.Y) > P(5))) { dragging = true; Cursor = Cursors.Hand; tip.Hide(this); }
                if (dragging) { dropIdx = DropIndexAt(e.Location); Invalidate(); return; }
            }
            int i = IndexAt(e.Location);
            if (i != hot)
            {
                hot = i;
                Invalidate();
                if (i >= 0) tip.Show(Title(items[i]), this, CellRect(i).Left, CellRect(i).Bottom + P(4), 3000);
                else tip.Hide(this);
            }
        }

        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hot = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressedIdx = IndexAt(e.Location);
            downAt = e.Location;
            dragging = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (dragging)
            {
                // move the item to where it was dropped
                var it = items[pressedIdx];
                int to = dropIdx;
                if (to > pressedIdx) to--;
                var others = items.Where(x => x != it).ToList();
                var before = to >= 0 && to < others.Count ? others[to] : null;
                sec.Items.Remove(it);
                int at = before != null ? sec.Items.IndexOf(before) : sec.Items.Count;
                sec.Items.Insert(at < 0 ? sec.Items.Count : at, it);
                dragging = false; pressedIdx = -1; dropIdx = -1; Cursor = Cursors.Default;
                bar.ApplySettings();
                Relayout();
                return;
            }
            int was = pressedIdx;
            pressedIdx = -1;
            if (i < 0 || i != was) return;
            if (e.Button == MouseButtons.Left) { var it = items[i]; Close(); bar.OpenFromGrid(it, sec); }
            else if (e.Button == MouseButtons.Right) ShowItemMenu(items[i], e.Location);
        }

        void ShowItemMenu(LaunchItem it, Point at)
        {
            var menu = Menus.Create(theme, s);
            Menus.Item(menu.Items, Title(it), null, () => { Close(); bar.OpenFromGrid(it, sec); }, true);
            Menus.Sep(menu.Items);
            Menus.Item(menu.Items, "Customize…", null, () => { Close(); bar.OpenSettings(it); });
            Menus.Item(menu.Items, "Unpin from " + (string.IsNullOrWhiteSpace(sec.Name) ? "this group" : sec.Name), null, () =>
            {
                sec.Items.Remove(it);
                bar.ApplySettings();
                if (sec.Items.Count == 0) Close(); else Relayout();
            });
            Menus.StyleTree(menu.Items, theme, s);
            menu.Show(this, at);
        }

        static string Title(LaunchItem it) =>
            !string.IsNullOrWhiteSpace(it.Name) ? it.Name : System.IO.Path.GetFileNameWithoutExtension(Launcher.Expand(it.Target));

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var pen = new Pen(Color.FromArgb(60, theme.PopupBorder))) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            for (int i = 0; i < items.Count; i++)
            {
                var rc = CellRect(i);
                var tint = Theme.Parse(items[i].BackColor);
                var inner = Rectangle.Inflate(rc, -P(2), -P(2));
                if (tint.HasValue) using (var b = new SolidBrush(tint.Value)) using (var p = BarForm.RoundRect(inner, Theme.IsWin10 ? 0 : P(4))) g.FillPath(b, p);
                if (i == hot && !dragging) using (var b = new SolidBrush(hover)) using (var p = BarForm.RoundRect(inner, Theme.IsWin10 ? 0 : P(4))) g.FillPath(b, p);
                var img = Launcher.IconFor(items[i], icon, sec.Browser);
                if (img != null) g.DrawImage(img, new Rectangle(rc.X + (rc.Width - icon) / 2, rc.Y + (rc.Height - icon) / 2, icon, icon));
            }
            if (dragging && dropIdx >= 0)
            {
                Rectangle rr = dropIdx < items.Count ? CellRect(dropIdx) : CellRect(items.Count - 1);
                int x = dropIdx < items.Count ? rr.Left : rr.Right;
                using (var b = new SolidBrush(theme.ActiveIndicator)) g.FillRectangle(b, x - P(1), rr.Top + P(3), Math.Max(2, P(2)), rr.Height - P(6));
            }
        }
    }
}
