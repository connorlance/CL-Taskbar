using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    // The icon a browser uses for its private (incognito / InPrivate) windows.
    // - Firefox ships a separate private_browsing.exe with the private icon: use that.
    // - Chrome, Edge, Brave and friends keep their incognito icon inside their own .exe, next to the normal one.
    //   Its position there isn't fixed, so we look through the .exe's icons for the dark, colorless one.
    // - If neither works: the browser's icon with a small incognito badge drawn on it.
    internal static class PrivateIcons
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);

        static readonly Dictionary<string, int> found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);   // exe -> icon index, -1 = none

        public static Bitmap For(string exe, int px)
        {
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return null;
            try
            {
                // Firefox (and its relatives): a separate program just for private windows
                string pb = Path.Combine(Path.GetDirectoryName(exe), "private_browsing.exe");
                if (File.Exists(pb)) { var b = Icons.FromShell(pb, px); if (b != null) return b; }

                int idx = FindIncognito(exe);
                if (idx > 0) { var b = Extract(exe, idx, px); if (b != null) return b; }
            }
            catch { }
            var baseIcon = Icons.FromShell(exe, px);
            return baseIcon == null ? null : WithBadge(baseIcon, px);
        }

        static Bitmap Extract(string exe, int index, int px)
        {
            var h = new IntPtr[1]; var ids = new int[1];
            if (PrivateExtractIcons(exe, index, px, px, h, ids, 1, 0) == 0 || h[0] == IntPtr.Zero) return null;
            try { using (var ico = Icon.FromHandle(h[0])) return new Bitmap(ico.ToBitmap(), px, px); }
            finally { Native.DestroyIcon(h[0]); }
        }

        // The incognito icon: mostly dark and without color, unlike the browser's normal colorful logo
        static int FindIncognito(string exe)
        {
            lock (found) if (found.TryGetValue(exe, out int cached)) return cached;
            int best = -1;
            try
            {
                int count = (int)ExtractIconEx(exe, -1, null, null, 0);
                double bestSat = 0.16;   // must be at least this colorless
                for (int i = 1; i < Math.Min(count, 48); i++)
                {
                    using (var bmp = Extract(exe, i, 32))
                    {
                        if (bmp == null) continue;
                        Measure(bmp, out double sat, out double bright, out double cover);
                        if (cover < 0.35 || bright > 0.5) continue;   // mostly empty, or a light (document-style) icon
                        if (sat < bestSat) { bestSat = sat; best = i; }
                    }
                }
            }
            catch { best = -1; }
            lock (found) found[exe] = best;
            return best;
        }

        static void Measure(Bitmap b, out double sat, out double bright, out double cover)
        {
            double s = 0, v = 0; int n = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    var c = b.GetPixel(x, y);
                    if (c.A < 128) continue;
                    n++;
                    s += c.GetSaturation() * Math.Min(1.0, (Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0) * 2);   // dark pixels don't count as colorful
                    v += c.GetBrightness();
                }
            cover = n / (double)(b.Width * b.Height);
            sat = n > 0 ? s / n : 1;
            bright = n > 0 ? v / n : 1;
        }

        // The browser's icon with a small dark incognito badge (hat and glasses) in the corner
        public static Bitmap WithBadge(Bitmap icon, int px)
        {
            var b = new Bitmap(px, px);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, 0, 0, px, px);
                float d = px * 0.62f, x = px - d, y = px - d;
                using (var ring = new SolidBrush(Color.FromArgb(240, 255, 255, 255))) g.FillEllipse(ring, x - px * 0.04f, y - px * 0.04f, d + px * 0.08f, d + px * 0.08f);
                using (var bg = new SolidBrush(Color.FromArgb(255, 32, 33, 36))) g.FillEllipse(bg, x, y, d, d);
                using (var fg = new SolidBrush(Color.White))
                using (var pen = new Pen(Color.White, Math.Max(1f, d * 0.07f)))
                {
                    float cx = x + d / 2;
                    // hat: crown and brim
                    g.FillPolygon(fg, new[] { new PointF(cx - d * 0.18f, y + d * 0.20f), new PointF(cx + d * 0.18f, y + d * 0.20f), new PointF(cx + d * 0.24f, y + d * 0.42f), new PointF(cx - d * 0.24f, y + d * 0.42f) });
                    g.FillRectangle(fg, cx - d * 0.34f, y + d * 0.42f, d * 0.68f, d * 0.07f);
                    // glasses
                    float r = d * 0.12f, gy = y + d * 0.66f;
                    g.DrawEllipse(pen, cx - d * 0.30f, gy - r, r * 2, r * 2);
                    g.DrawEllipse(pen, cx + d * 0.06f, gy - r, r * 2, r * 2);
                    g.DrawLine(pen, cx - d * 0.06f, gy, cx + d * 0.06f, gy);
                }
            }
            return b;
        }
    }
}
