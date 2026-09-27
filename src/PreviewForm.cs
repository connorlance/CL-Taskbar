using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace CLTaskbar
{
    // Hover popup: live window thumbnails (via the DWM thumbnail API, same one the real taskbar uses),
    // or a small text tooltip for pinned items and desktop labels.
    internal class PreviewForm : Form
    {
        class Tile
        {
            public AppWindow Win;
            public Rectangle Rect, ThumbRect, CloseRect, TitleRect, IconRect;
            public IntPtr Thumb;
            public bool HasThumb;
        }

        readonly List<Tile> tiles = new List<Tile>();
        readonly Timer poll = new Timer { Interval = 80 };
        Theme theme;
        float scale = 1;
        string text;
        Rectangle anchor;          // screen rect of the bar button we belong to
        DateTime outsideSince = DateTime.MaxValue;
        Tile hoverTile; bool hoverClose;
        Font titleFont;

        public object Owner_Key;   // which bar button this popup is showing
        public Action<AppWindow> OnActivate;
        public Action<AppWindow> OnCloseWindow;

        public PreviewForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            TopMost = true;
            poll.Tick += (s, e) => CheckMouse();
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Native.RoundCorners(Handle);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            base.WndProc(ref m);
        }

        public bool IsShowingFor(object key) => Visible && Equals(Owner_Key, key);

        public void ShowText(object key, string msg, Rectangle anchorRect, Theme t, float s)
        {
            ClearThumbs();
            Owner_Key = key; text = msg; theme = t; scale = s; anchor = anchorRect;
            EnsureFont();
            Size sz;
            using (var g = CreateGraphics())
            {
                var m = TextRenderer.MeasureText(g, msg, titleFont);
                sz = new Size(m.Width + (int)(20 * s), m.Height + (int)(14 * s));
            }
            PlaceAndShow(sz);
        }

        public void ShowWindows(object key, List<AppWindow> wins, Rectangle anchorRect, Theme t, float s)
        {
            ClearThumbs();
            Owner_Key = key; text = null; theme = t; scale = s; anchor = anchorRect;
            EnsureFont();
            if (!IsHandleCreated) CreateHandle();

            var mon = MonitorOf(anchorRect);
            int pad = (int)(8 * s), titleH = (int)(30 * s), gap = (int)(4 * s);
            int maxW = (int)(220 * s), maxH = (int)(124 * s);
            int n = Math.Max(1, wins.Count);
            int avail = mon.Width - (int)(40 * s);
            int tileW = maxW + pad * 2;
            if (n * (tileW + gap) + gap > avail)
            {
                tileW = Math.Max((int)(110 * s), (avail - gap) / n - gap);
                maxW = tileW - pad * 2;
                maxH = maxW * 124 / 220;
            }
            int tileH = pad + titleH + maxH + pad;

            int x = gap;
            foreach (var w in wins)
            {
                var tile = new Tile { Win = w };
                tile.Rect = new Rectangle(x, gap, tileW, tileH);
                int icon = (int)(16 * s);
                tile.IconRect = new Rectangle(x + pad, gap + pad + (titleH - icon) / 2 - (int)(2 * s), icon, icon);
                int closeSz = (int)(28 * s);
                tile.CloseRect = new Rectangle(tile.Rect.Right - pad - closeSz + (int)(4 * s), gap + pad - (int)(4 * s), closeSz, closeSz);
                tile.TitleRect = Rectangle.FromLTRB(tile.IconRect.Right + (int)(8 * s), gap + pad - (int)(2 * s), tile.CloseRect.Left - (int)(2 * s), gap + pad + titleH - (int)(6 * s));

                // Register a live thumbnail and fit it to the source aspect ratio
                Size src = Size.Empty;
                if (Native.DwmRegisterThumbnail(Handle, w.Hwnd, out IntPtr th) == 0)
                {
                    tile.Thumb = th;
                    if (Native.DwmQueryThumbnailSourceSize(th, out var ss) == 0 && ss.cx > 1 && ss.cy > 1)
                        src = new Size(ss.cx, ss.cy);
                }
                int areaTop = gap + pad + titleH;
                if (src.Width > 0)
                {
                    double k = Math.Min((double)maxW / src.Width, (double)maxH / src.Height);
                    int tw = Math.Max(1, (int)(src.Width * k)), thh = Math.Max(1, (int)(src.Height * k));
                    tile.ThumbRect = new Rectangle(x + pad + (maxW - tw) / 2, areaTop + (maxH - thh) / 2, tw, thh);
                    tile.HasThumb = true;
                }
                else tile.ThumbRect = new Rectangle(x + pad, areaTop, maxW, maxH);
                tiles.Add(tile);
                x += tileW + gap;
            }
            PlaceAndShow(new Size(x, tileH + gap * 2));
            foreach (var tile in tiles) UpdateThumb(tile);
        }

        void UpdateThumb(Tile t)
        {
            if (t.Thumb == IntPtr.Zero) return;
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
                rcDestination = new Native.RECT { Left = t.ThumbRect.Left, Top = t.ThumbRect.Top, Right = t.ThumbRect.Right, Bottom = t.ThumbRect.Bottom },
                opacity = 255, fVisible = t.HasThumb, fSourceClientAreaOnly = false
            };
            Native.DwmUpdateThumbnailProperties(t.Thumb, ref p);
        }

        void PlaceAndShow(Size sz)
        {
            var mon = MonitorOf(anchor);
            int x = anchor.Left + anchor.Width / 2 - sz.Width / 2;
            x = Math.Max(mon.Left + (int)(8 * scale), Math.Min(x, mon.Right - (int)(8 * scale) - sz.Width));
            int y = anchor.Top - sz.Height - (int)(10 * scale);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, sz.Width, sz.Height, Native.SWP_NOACTIVATE);
            if (!Visible) Show();
            outsideSince = DateTime.MaxValue;
            hoverTile = null;
            poll.Start();
            Invalidate();
        }

        static Rectangle MonitorOf(Rectangle r)
        {
            var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            var mon = Native.MonitorFromPoint(new Native.POINT { X = r.Left + r.Width / 2, Y = r.Top + r.Height / 2 }, 2);
            Native.GetMonitorInfo(mon, ref mi);
            return mi.rcMonitor.ToRectangle();
        }

        void EnsureFont()
        {
            float px = 12f * scale;
            if (titleFont == null || Math.Abs(titleFont.Size - px) > 0.1)
            {
                titleFont?.Dispose();
                titleFont = new Font("Segoe UI", px, GraphicsUnit.Pixel);
            }
        }

        public void HidePopup()
        {
            poll.Stop();
            ClearThumbs();
            Owner_Key = null;
            if (Visible) Hide();
        }

        void ClearThumbs()
        {
            foreach (var t in tiles) if (t.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(t.Thumb);
            tiles.Clear();
        }

        void CheckMouse()
        {
            Native.GetCursorPos(out var p);
            var pt = new Point(p.X, p.Y);
            var mine = Bounds;
            mine.Inflate((int)(4 * scale), (int)(12 * scale));
            var anc = anchor; anc.Inflate(0, (int)(4 * scale));
            if (mine.Contains(pt) || anc.Contains(pt)) { outsideSince = DateTime.MaxValue; return; }
            if (outsideSince == DateTime.MaxValue) outsideSince = DateTime.Now;
            else if ((DateTime.Now - outsideSince).TotalMilliseconds > 220) HidePopup();
        }

        // Called by the bar when windows close etc.
        public bool HasDeadWindows()
        {
            foreach (var t in tiles) if (!Native.IsWindow(t.Win.Hwnd)) return true;
            return false;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (theme == null) return;
            var g = e.Graphics;
            g.Clear(theme.PopupBg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            using (var pen = new Pen(theme.PopupBorder)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);

            if (text != null)
            {
                TextRenderer.DrawText(g, text, titleFont, ClientRectangle, theme.Text, theme.PopupBg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                return;
            }

            foreach (var t in tiles)
            {
                bool hov = t == hoverTile;
                if (hov) using (var b = new SolidBrush(theme.PopupHover)) FillRound(g, b, t.Rect, (int)(6 * scale));
                var icon = Icons.ForWindow(t.Win, (int)(16 * scale));
                g.DrawImage(icon, t.IconRect);
                TextRenderer.DrawText(g, string.IsNullOrEmpty(t.Win.Title) ? "(untitled)" : t.Win.Title, titleFont, t.TitleRect, theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                if (hov)
                {
                    if (hoverClose) using (var b = new SolidBrush(Color.FromArgb(196, 43, 28))) FillRound(g, b, t.CloseRect, (int)(4 * scale));
                    var c = t.CloseRect; int k = (int)(5 * scale);
                    int cx = c.Left + c.Width / 2, cy = c.Top + c.Height / 2;
                    using (var pen = new Pen(hoverClose ? Color.White : theme.Text, Math.Max(1f, 1.1f * scale)))
                    {
                        g.DrawLine(pen, cx - k, cy - k, cx + k, cy + k);
                        g.DrawLine(pen, cx - k, cy + k, cx + k, cy - k);
                    }
                }
                if (!t.HasThumb)
                {
                    int big = (int)(48 * scale);
                    var ib = Icons.ForWindow(t.Win, big);
                    g.DrawImage(ib, new Rectangle(t.ThumbRect.Left + (t.ThumbRect.Width - big) / 2, t.ThumbRect.Top + (t.ThumbRect.Height - big) / 2, big, big));
                }
            }
        }

        static void FillRound(Graphics g, Brush b, Rectangle r, int rad)
        {
            using (var p = BarForm.RoundRect(r, rad)) g.FillPath(b, p);
        }

        Tile TileAt(Point p)
        {
            foreach (var t in tiles) if (t.Rect.Contains(p)) return t;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var t = TileAt(e.Location);
            bool c = t != null && t.CloseRect.Contains(e.Location);
            if (t != hoverTile || c != hoverClose) { hoverTile = t; hoverClose = c; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hoverTile = null; hoverClose = false; Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            var t = TileAt(e.Location);
            if (t == null) return;
            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && t.CloseRect.Contains(e.Location)))
            {
                OnCloseWindow?.Invoke(t.Win);
                return;
            }
            if (e.Button == MouseButtons.Left)
            {
                HidePopup();
                OnActivate?.Invoke(t.Win);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ClearThumbs(); poll.Dispose(); titleFont?.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
