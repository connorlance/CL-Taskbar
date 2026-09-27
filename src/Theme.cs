using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using Microsoft.Win32;

namespace CLTaskbar
{
    // Colors that match the Windows 11 taskbar, read from your personalization settings (read-only).
    internal class Theme
    {
        public Color Background;
        public Color Hover, Pressed, ActiveBg;
        public Color Indicator, ActiveIndicator, Flash;
        public Color Text, TextDim, Divider, GroupHighlight;
        public Color PopupBg, PopupHover, PopupBorder;
        public bool Dark;
        public Color Accent;
        public bool PopupIsDark() => (0.2126 * PopupBg.R + 0.7152 * PopupBg.G + 0.0722 * PopupBg.B) / 255.0 < 0.5;

        public static readonly bool IsWin10 = DetectWin10();

        static bool DetectWin10()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                    if (int.TryParse(k?.GetValue("CurrentBuild") as string, out int b)) return b < 22000;
            }
            catch { }
            return Environment.OSVersion.Version.Build < 22000;
        }

        public static Theme Build(Config cfg, Color? sampled)
        {
            var t = new Theme();
            bool lightTaskbar = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) != 0;
            bool accentOnTaskbar = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "ColorPrevalence", 0) != 0;
            var palette = ReadAccentPalette();
            t.Accent = palette != null ? palette[3] : Color.FromArgb(0, 120, 212);

            Color bg;
            if (cfg.Color.StartsWith("#") && TryHex(cfg.Color, out var hex)) bg = hex;
            else if (cfg.Color.Equals("sample", StringComparison.OrdinalIgnoreCase) && sampled.HasValue) bg = sampled.Value;
            else if (accentOnTaskbar && palette != null) bg = palette[5];
            else bg = lightTaskbar ? Color.FromArgb(238, 238, 238) : Color.FromArgb(28, 28, 28);
            t.Background = Color.FromArgb(255, bg);

            double lum = (0.2126 * bg.R + 0.7152 * bg.G + 0.0722 * bg.B) / 255.0;
            t.Dark = lum < 0.5;

            if (t.Dark)
            {
                t.Hover = Color.FromArgb(22, 255, 255, 255);
                t.Pressed = Color.FromArgb(14, 255, 255, 255);
                t.ActiveBg = Color.FromArgb(16, 255, 255, 255);
                t.Indicator = Color.FromArgb(160, 255, 255, 255);
                t.ActiveIndicator = palette != null ? palette[1] : Color.FromArgb(96, 205, 255);
                t.Text = Color.FromArgb(235, 255, 255, 255);
                t.TextDim = Color.FromArgb(130, 255, 255, 255);
                t.Divider = Color.FromArgb(45, 255, 255, 255);
                t.GroupHighlight = Color.FromArgb(30, 255, 255, 255);
                t.PopupBg = Color.FromArgb(44, 44, 44);
                t.PopupHover = Color.FromArgb(58, 58, 58);
                t.PopupBorder = Color.FromArgb(70, 70, 70);
            }
            else
            {
                t.Hover = Color.FromArgb(150, 255, 255, 255);
                t.Pressed = Color.FromArgb(90, 255, 255, 255);
                t.ActiveBg = Color.FromArgb(120, 255, 255, 255);
                t.Indicator = Color.FromArgb(140, 0, 0, 0);
                t.ActiveIndicator = palette != null ? palette[4] : Color.FromArgb(0, 95, 184);
                t.Text = Color.FromArgb(230, 0, 0, 0);
                t.TextDim = Color.FromArgb(130, 0, 0, 0);
                t.Divider = Color.FromArgb(40, 0, 0, 0);
                t.GroupHighlight = Color.FromArgb(24, 0, 0, 0);
                t.PopupBg = Color.FromArgb(249, 249, 249);
                t.PopupHover = Color.FromArgb(234, 234, 234);
                t.PopupBorder = Color.FromArgb(215, 215, 215);
            }
            t.Flash = Color.FromArgb(235, 130, 20);
            if (IsWin10)
            {
                // Windows 10: square buttons, stronger fills, accent underline for every running app
                if (t.Dark)
                {
                    t.Hover = Color.FromArgb(30, 255, 255, 255);
                    t.ActiveBg = Color.FromArgb(40, 255, 255, 255);
                    t.Pressed = Color.FromArgb(22, 255, 255, 255);
                    t.GroupHighlight = Color.FromArgb(34, 255, 255, 255);
                }
                else
                {
                    t.Hover = Color.FromArgb(22, 0, 0, 0);
                    t.ActiveBg = Color.FromArgb(32, 0, 0, 0);
                    t.Pressed = Color.FromArgb(16, 0, 0, 0);
                    t.GroupHighlight = Color.FromArgb(26, 0, 0, 0);
                }
                t.Indicator = t.ActiveIndicator;
            }
            // your own colors for the lines under open apps
            var ind = Parse(cfg.IndicatorColor);
            var act = Parse(cfg.ActiveIndicatorColor);
            if (act.HasValue) t.ActiveIndicator = act.Value;
            if (ind.HasValue) t.Indicator = ind.Value;
            else if (act.HasValue && IsWin10) t.Indicator = act.Value;   // Windows 10 uses one line color for both
            return t;
        }

        static int ReadDword(string path, string name, int def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(path)) return k?.GetValue(name) is int v ? v : def; }
            catch { return def; }
        }

        static Color[] ReadAccentPalette()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    if (k?.GetValue("AccentPalette") is byte[] b && b.Length >= 32)
                    {
                        var c = new Color[8];
                        for (int i = 0; i < 8; i++) c[i] = Color.FromArgb(b[i * 4], b[i * 4 + 1], b[i * 4 + 2]);
                        return c;
                    }
                }
            }
            catch { }
            return null;
        }

        // "#RRGGBB" or "#AARRGGBB"; empty = not set
        public static Color? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string h = s.Trim().TrimStart('#');
            if (!uint.TryParse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;
            if (h.Length == 6) return Color.FromArgb(255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
            if (h.Length == 8) return Color.FromArgb((int)(v >> 24) & 255, (int)(v >> 16) & 255, (int)(v >> 8) & 255, (int)v & 255);
            return null;
        }

        public static string ToHex(Color c) => c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

        // A copy with different popup-menu colors (for styled menus)
        public Theme WithMenuColors(Color? back, Color? text, Color? hover)
        {
            var t = (Theme)MemberwiseClone();
            if (back.HasValue)
            {
                t.PopupBg = back.Value;
                double lum = (0.2126 * back.Value.R + 0.7152 * back.Value.G + 0.0722 * back.Value.B) / 255.0;
                t.PopupHover = hover ?? (lum < 0.5 ? ControlPaintLight(back.Value, 0.12) : ControlPaintLight(back.Value, -0.08));
                t.PopupBorder = lum < 0.5 ? ControlPaintLight(back.Value, 0.2) : ControlPaintLight(back.Value, -0.15);
                if (!text.HasValue) { t.Text = lum < 0.5 ? Color.White : Color.FromArgb(27, 27, 27); t.TextDim = Color.FromArgb(150, t.Text); }
            }
            if (text.HasValue) { t.Text = text.Value; t.TextDim = Color.FromArgb(160, text.Value); }
            if (hover.HasValue) t.PopupHover = hover.Value;
            return t;
        }

        static Color ControlPaintLight(Color c, double amt)
        {
            int f(int x) => Math.Max(0, Math.Min(255, (int)(amt >= 0 ? x + (255 - x) * amt : x * (1 + amt))));
            return Color.FromArgb(c.A, f(c.R), f(c.G), f(c.B));
        }

        static bool TryHex(string s, out Color c)
        {
            c = Color.Black;
            s = s.TrimStart('#');
            if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v)) return false;
            c = Color.FromArgb((v >> 16) & 255, (v >> 8) & 255, v & 255);
            return true;
        }

        // Reads a few pixels of the REAL taskbar next to our bar (outside it) and returns the median color.
        public static Color? Sample(Native.RECT taskbar, int barLeft, int barRight, float scale)
        {
            try
            {
                var xs = new List<int>();
                int gap = barRight + (int)(2 * scale);
                xs.Add(taskbar.Right - (int)(3 * scale));  // the "show desktop" sliver is plain taskbar
                xs.Add(taskbar.Left + 1);
                if (gap < taskbar.Right - 4) xs.Add(gap);
                int top = taskbar.Top + (int)(6 * scale), bottom = taskbar.Bottom - (int)(4 * scale);
                if (bottom <= top) return null;
                var samples = new List<Color>();
                foreach (int x in xs)
                {
                    using (var bmp = new Bitmap(1, bottom - top))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(x, top, 0, 0, bmp.Size);
                        for (int y = 0; y < bmp.Height; y += 2) samples.Add(bmp.GetPixel(0, y));
                    }
                }
                if (samples.Count == 0) return null;
                samples.Sort((a, b) => (a.R + a.G + a.B).CompareTo(b.R + b.G + b.B));
                var m = samples[samples.Count / 2];
                return Color.FromArgb(m.R, m.G, m.B);
            }
            catch { return null; }
        }
    }
}
