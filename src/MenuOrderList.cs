using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CLTaskbar
{
    // One right-click menu's options: tick to show, drag to reorder (including the divider lines).
    internal class MenuOrderList : Control
    {
        readonly BarForm bar;
        readonly string menu;
        readonly Action changed;
        readonly Color text, dim, accent, border, hoverBg;
        readonly float s;
        List<string> ids;        // the rows shown (all options, or only the ones turned on)
        bool enabledOnly;
        readonly int rowH;
        int hot = -1, dragFrom = -1, dropAt = -1;
        Point downAt; bool dragging;

        public MenuOrderList(BarForm owner, string menuName, float scale, Color textColor, Color dimColor, Color accentColor, Color borderColor, Color hover, Color back, Action onChanged)
        {
            bar = owner; menu = menuName; s = scale; changed = onChanged;
            text = textColor; dim = dimColor; accent = accentColor; border = borderColor; hoverBg = hover;
            BackColor = back;
            DoubleBuffered = true;
            Font = new Font("Segoe UI", 9f);
            rowH = (int)(26 * s);
            ids = bar.OrderFor(menu);
            Size = new Size((int)(430 * s), ids.Count * rowH);
        }

        // Show every option, or just the ones that are on. Turning something off while filtered keeps
        // it in view (dimmed) until the filter is applied again, so a mis-click is easy to undo.
        public void SetEnabledOnly(bool on)
        {
            enabledOnly = on;
            var all = bar.OrderFor(menu);
            ids = on ? all.Where(bar.On).ToList() : all;
            Height = Math.Max(1, ids.Count) * rowH;
            hot = -1;
            Invalidate();
        }

        string Label(string id) => BarForm.MenuOptions.First(o => o[1] == id)[2];
        bool IsDivider(string id) => id.EndsWith("|");
        bool Locked(string id) => id == "b.settings";

        int RowAt(int y) => y < 0 || ids.Count == 0 ? -1 : Math.Min(ids.Count - 1, y / rowH);

        Rectangle BoxRect(int i) => new Rectangle((int)(30 * s), i * rowH + (rowH - (int)(16 * s)) / 2, (int)(16 * s), (int)(16 * s));

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragFrom >= 0 && e.Button == MouseButtons.Left && ids.Count > 1)
            {
                if (!dragging && Math.Abs(e.Y - downAt.Y) > (int)(4 * s)) dragging = true;
                if (dragging)
                {
                    Cursor = Cursors.SizeNS;
                    dropAt = Math.Max(0, Math.Min(ids.Count, (e.Y + rowH / 2) / rowH));
                    Invalidate();
                    return;
                }
            }
            int r = e.X < (int)(26 * s) ? -2 : RowAt(e.Y);
            Cursor = e.X < (int)(26 * s) ? Cursors.SizeNS : Cursors.Hand;
            int h = RowAt(e.Y);
            if (h != hot) { hot = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { if (!dragging) { hot = -1; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            dragFrom = RowAt(e.Y);
            downAt = e.Location;
            dragging = false;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int from = dragFrom;
            dragFrom = -1;
            if (dragging)
            {
                dragging = false;
                Cursor = Cursors.Hand;
                if (from >= 0 && dropAt >= 0)
                {
                    var id = ids[from];
                    int to = dropAt > from ? dropAt - 1 : dropAt;
                    ids.RemoveAt(from);
                    to = Math.Max(0, Math.Min(ids.Count, to));
                    ids.Insert(to, id);
                    // Place it in the full order too (hidden options keep their places):
                    // just before the next shown row, or just after the previous one if it's now last
                    var full = bar.OrderFor(menu);
                    full.Remove(id);
                    int at = to + 1 < ids.Count ? full.IndexOf(ids[to + 1])
                           : to > 0 ? full.IndexOf(ids[to - 1]) + 1 : 0;
                    full.Insert(Math.Max(0, Math.Min(full.Count, at)), id);
                    var cfg = bar.Settings;
                    var mine = new HashSet<string>(full);
                    cfg.MenuOrder = cfg.MenuOrder.Where(x => !mine.Contains(x)).Concat(full).ToList();
                    changed();
                }
                dropAt = -1;
                Invalidate();
                return;
            }
            // a plain click toggles the option
            int i = RowAt(e.Y);
            if (i < 0 || i != from || Locked(ids[i])) return;
            var cf = bar.Settings;
            cf.MenuChoices[ids[i]] = !bar.On(ids[i]);
            changed();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (ids.Count == 0)
                TextRenderer.DrawText(g, "Nothing turned on in this menu", Font, new Rectangle((int)(30 * s), 0, Width, rowH), dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            for (int i = 0; i < ids.Count; i++)
            {
                var id = ids[i];
                var row = new Rectangle(0, i * rowH, Width, rowH);
                if (i == hot && !dragging) using (var b = new SolidBrush(hoverBg)) using (var p = BarForm.RoundRect(row, (int)(4 * s))) g.FillPath(b, p);
                if (dragging && i == dragFrom) using (var b = new SolidBrush(Color.FromArgb(40, accent))) g.FillRectangle(b, row);

                // grip
                using (var b = new SolidBrush(dim))
                    for (int gx = 0; gx < 2; gx++) for (int gy = 0; gy < 3; gy++)
                        g.FillEllipse(b, (int)(9 * s) + gx * (int)(5 * s), row.Y + rowH / 2 - (int)(6 * s) + gy * (int)(5 * s), (int)(2.5f * s), (int)(2.5f * s));

                bool on = bar.On(id);
                var box = BoxRect(i);
                if (on)
                {
                    using (var b = new SolidBrush(Locked(id) ? dim : accent)) using (var p = BarForm.RoundRect(box, (int)(3 * s))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.White, Math.Max(1.5f, 1.6f * s)))
                        g.DrawLines(pen, new[] { new PointF(box.Left + box.Width * .22f, box.Top + box.Height * .52f), new PointF(box.Left + box.Width * .42f, box.Top + box.Height * .72f), new PointF(box.Left + box.Width * .78f, box.Top + box.Height * .30f) });
                }
                else using (var pen = new Pen(dim, Math.Max(1f, s))) using (var p = BarForm.RoundRect(box, (int)(3 * s))) g.DrawPath(pen, p);

                var tr = new Rectangle(box.Right + (int)(10 * s), row.Y, Width - box.Right - (int)(14 * s), rowH);
                if (IsDivider(id))
                {
                    int y = row.Y + rowH / 2;
                    using (var pen = new Pen(on ? border : Color.FromArgb(80, border), Math.Max(1f, s)) { DashStyle = on ? DashStyle.Solid : DashStyle.Dash })
                        g.DrawLine(pen, tr.Left, y, tr.Right - (int)(60 * s), y);
                    TextRenderer.DrawText(g, "divider", Font, new Rectangle(tr.Right - (int)(56 * s), row.Y, (int)(56 * s), rowH), dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Right);
                }
                else
                    TextRenderer.DrawText(g, Label(id), Font, tr, on ? text : dim, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            }
            if (dragging && dropAt >= 0)
            {
                int y = dropAt * rowH;
                using (var b = new SolidBrush(accent)) g.FillRectangle(b, 0, Math.Max(0, y - 1), Width, Math.Max(2, (int)(2 * s)));
            }
        }
    }
}
