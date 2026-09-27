using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace CLTaskbar
{
    internal partial class SettingsForm
    {
        // Colors that look good on a taskbar, in rows:
        // Windows' own accent colors (the palette in Settings > Personalization > Colors),
        // soft light shades (text on dark bars), deep tints (backgrounds on dark bars) and grays.
        static readonly (string title, string[] colors)[] PaletteRows =
        {
            ("Windows accent colors", new[] { "#FFB900", "#FF8C00", "#F7630C", "#CA5010", "#DA3B01", "#EF6950", "#D13438", "#FF4343", "#E74856", "#E81123", "#EA005E", "#C30052" }),
            (null, new[] { "#E3008C", "#BF0077", "#C239B3", "#9A0089", "#0078D7", "#0063B1", "#8E8CD8", "#6B69D6", "#8764B8", "#744DA9", "#B146C2", "#881798" }),
            (null, new[] { "#0099BC", "#2D7D9A", "#00B7C3", "#038387", "#00B294", "#018574", "#00CC6A", "#10893E", "#7A7574", "#5D5A58", "#68768A", "#515C6B" }),
            (null, new[] { "#567C73", "#486860", "#498205", "#107C10", "#767676", "#4C4A48", "#69797E", "#4A5459", "#647C64", "#525E54", "#847545", "#7E735F" }),
            ("Soft (good for text on a dark bar)", new[] { "#FFB3B3", "#FFCC99", "#FFE08A", "#FFF2B3", "#C8F0B0", "#A8EBD3", "#A8E0F0", "#A8C8FF", "#C4BDFF", "#DDB3FF", "#FFB3E0", "#EBD9CC" }),
            ("Deep (good for backgrounds on a dark bar)", new[] { "#4A1F1F", "#4A2E17", "#4A3F14", "#2E4417", "#174A2B", "#144543", "#17394A", "#1A2A52", "#2D1F52", "#431F4A", "#4A1A38", "#3D3630" }),
            ("Grays", new[] { "#000000", "#141414", "#1F1F1F", "#2B2B2B", "#383838", "#4A4A4A", "#5E5E5E", "#7A7A7A", "#9E9E9E", "#C8C8C8", "#E6E6E6", "#FFFFFF" }),
        };

        static readonly List<Color> recentColors = new List<Color>();   // your "More colors…" picks, offered again next time

        // A drop-down of good colors under the Pick… button, with a way into the full color picker
        void ShowColorPalette(Control anchor, Color? current, Action<string> choose)
        {
            var drop = new ToolStripDropDown { Padding = Padding.Empty, BackColor = cSurface, DropShadowEnabled = true };
            var panel = new PalettePanel(this, current, drop, choose);
            var host = new ToolStripControlHost(panel) { Padding = Padding.Empty, Margin = Padding.Empty, AutoSize = false, Size = panel.Size };
            drop.Items.Add(host);
            drop.Show(anchor, new Point(0, anchor.Height));
        }

        class PalettePanel : Control
        {
            readonly SettingsForm f;
            readonly Color? current;
            readonly ToolStripDropDown drop;
            readonly Action<string> choose;
            readonly List<(Rectangle r, Color c)> cells = new List<(Rectangle, Color)>();
            readonly List<(Rectangle r, string text)> titles = new List<(Rectangle, string)>();
            Rectangle moreBtn, defaultBtn;
            int hot = -1; string hotBtn;
            readonly ToolTip tip = new ToolTip { InitialDelay = 300 };
            string tipShown;

            public PalettePanel(SettingsForm owner, Color? cur, ToolStripDropDown dd, Action<string> pick)
            {
                f = owner; current = cur; drop = dd; choose = pick;
                DoubleBuffered = true;
                BackColor = f.cSurface;
                Font = f.fSmall;
                int sw = f.P(22), gap = f.P(4), pad = f.P(10), titleH = f.P(18);
                int y = pad;
                var rows = PaletteRows.ToList();
                if (recentColors.Count > 0) rows.Insert(0, ("Your colors", recentColors.Select(Theme.ToHex).ToArray()));
                foreach (var (title, colors) in rows)
                {
                    if (title != null)
                    {
                        if (y > pad) y += f.P(4);
                        titles.Add((new Rectangle(pad, y, 12 * (sw + gap), titleH), title));
                        y += titleH;
                    }
                    for (int i = 0; i < colors.Length; i++)
                    {
                        var c = Theme.Parse(colors[i]);
                        if (c.HasValue) cells.Add((new Rectangle(pad + i * (sw + gap), y, sw, sw), c.Value));
                    }
                    y += sw + gap;
                }
                y += f.P(6);
                int bw = (12 * (sw + gap) - gap - f.P(8)) / 2, bh = f.P(28);
                defaultBtn = new Rectangle(pad, y, bw, bh);
                moreBtn = new Rectangle(pad + bw + f.P(8), y, bw, bh);
                Size = new Size(pad * 2 + 12 * (sw + gap) - gap, y + bh + pad);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                int h = cells.FindIndex(x => x.r.Contains(e.Location));
                string hb = moreBtn.Contains(e.Location) ? "more" : defaultBtn.Contains(e.Location) ? "default" : null;
                if (h != hot || hb != hotBtn) { hot = h; hotBtn = hb; Invalidate(); }
                Cursor = h >= 0 || hb != null ? Cursors.Hand : Cursors.Default;
                string t = h >= 0 ? Theme.ToHex(cells[h].c) : null;
                if (t != tipShown) { tipShown = t; if (t != null) tip.Show(t, this, e.X + f.P(12), e.Y + f.P(16), 1500); else tip.Hide(this); }
            }

            protected override void OnMouseLeave(EventArgs e) { hot = -1; hotBtn = null; Invalidate(); }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                int h = cells.FindIndex(x => x.r.Contains(e.Location));
                if (h >= 0) { drop.Close(); choose(Theme.ToHex(cells[h].c)); return; }
                if (defaultBtn.Contains(e.Location)) { drop.Close(); choose(""); return; }
                if (moreBtn.Contains(e.Location))
                {
                    drop.Close();
                    // the full Windows color picker, with your recent picks and the soft/deep palette
                    // in its custom-color slots so they're easy to tweak from
                    var custom = recentColors.Concat(PaletteRows[4].colors.Concat(PaletteRows[5].colors).Select(x => Theme.Parse(x).Value))
                        .Take(16).Select(c => c.R | (c.G << 8) | (c.B << 16)).ToArray();
                    using (var dlg = new ColorDialog { FullOpen = true, AnyColor = true, Color = current ?? Color.FromArgb(0x60, 0x60, 0x60), CustomColors = custom })
                    {
                        if (dlg.ShowDialog(f) != DialogResult.OK) return;
                        var c = Color.FromArgb(255, dlg.Color);
                        recentColors.RemoveAll(x => x.ToArgb() == c.ToArgb());
                        recentColors.Insert(0, c);
                        if (recentColors.Count > 12) recentColors.RemoveAt(12);
                        choose(Theme.ToHex(c));
                    }
                }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(BackColor);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(f.cBorder)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
                foreach (var (r, text) in titles)
                    TextRenderer.DrawText(g, text, Font, r, f.cDim, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix);
                for (int i = 0; i < cells.Count; i++)
                {
                    var (r, c) = cells[i];
                    using (var b = new SolidBrush(c)) using (var p = BarForm.RoundRect(r, f.P(4))) g.FillPath(b, p);
                    using (var pen = new Pen(Color.FromArgb(60, f.cText))) using (var p = BarForm.RoundRect(r, f.P(4))) g.DrawPath(pen, p);
                    bool isCur = current.HasValue && (current.Value.ToArgb() & 0xFFFFFF) == (c.ToArgb() & 0xFFFFFF);
                    if (i == hot || isCur)
                    {
                        var ring = Rectangle.Inflate(r, f.P(2), f.P(2));
                        using (var pen = new Pen(isCur ? f.cAccent : f.cText, Math.Max(1.5f, f.P(2))))
                        using (var p = BarForm.RoundRect(ring, f.P(5))) g.DrawPath(pen, p);
                    }
                }
                DrawButton(g, defaultBtn, "Default", hotBtn == "default");
                DrawButton(g, moreBtn, "More colors…", hotBtn == "more");
            }

            void DrawButton(Graphics g, Rectangle r, string text, bool hover)
            {
                using (var b = new SolidBrush(hover ? f.cHover : f.cInput)) using (var p = BarForm.RoundRect(r, f.P(4))) g.FillPath(b, p);
                using (var pen = new Pen(f.cBorder)) using (var p = BarForm.RoundRect(r, f.P(4))) g.DrawPath(pen, p);
                TextRenderer.DrawText(g, text, f.fBody, r, f.cText, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
