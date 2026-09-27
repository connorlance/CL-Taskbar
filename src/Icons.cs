using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    internal static class Icons
    {
        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        static readonly Guid IID_ImageFactory = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        public static Func<AppWindow, int, Bitmap> Override;
        public static Func<AppWindow, string> NameOverride;

        public static Bitmap ForWindow(AppWindow w, int px)
        {
            var o = Override?.Invoke(w, px);
            if (o != null) return o;
            string key = w.GroupKey + "|" + px;
            if (cache.TryGetValue(key, out var bmp)) return bmp;

            bool host = WindowTracker.IsHostExe(w.ExePath);
            if (w.Aumid != null) bmp = FromShell(@"shell:AppsFolder\" + w.Aumid, px);
            if (bmp == null && w.ExePath != null && !host) bmp = FromShell(w.ExePath, px);
            if (bmp == null) bmp = FromWindowIcon(w.Hwnd);
            if (bmp == null && w.ExePath != null) bmp = FromShell(w.ExePath, px);
            if (bmp == null) bmp = SystemIcons.Application.ToBitmap();
            cache[key] = bmp;
            return bmp;
        }

        public static Bitmap ForFile(string path, int px)
        {
            string key = "file:" + path.ToLowerInvariant() + "|" + px;
            if (cache.TryGetValue(key, out var bmp)) return bmp;
            bmp = FromShell(path, px) ?? SystemIcons.Application.ToBitmap();
            cache[key] = bmp;
            return bmp;
        }

        public static void Clear()
        {
            foreach (var b in cache.Values) b.Dispose();
            cache.Clear();
        }

        internal static Bitmap FromShell(string path, int px)
        {
            try
            {
                var iid = IID_ImageFactory;
                if (Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o) != 0 || o == null) return null;
                var f = (IShellItemImageFactory)o;
                try
                {
                    // SIIGBF_ICONONLY = 0x4
                    if (f.GetImage(new Native.SIZE(px, px), 0x4, out IntPtr hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                    try { return FromHBitmap(hbmp); } finally { Native.DeleteObject(hbmp); }
                }
                finally { Marshal.ReleaseComObject(f); }
            }
            catch { return null; }
        }

        static Bitmap FromHBitmap(IntPtr hbmp)
        {
            if (Native.GetObject(hbmp, Marshal.SizeOf(typeof(Native.BITMAP)), out var bm) == 0) return null;
            int w = bm.bmWidth, h = Math.Abs(bm.bmHeight);
            if (w <= 0 || h <= 0) return null;
            var bih = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf(typeof(Native.BITMAPINFOHEADER)), biWidth = w, biHeight = -h,
                biPlanes = 1, biBitCount = 32, biCompression = 0
            };
            var buf = new byte[w * h * 4];
            IntPtr dc = Native.GetDC(IntPtr.Zero);
            try { if (Native.GetDIBits(dc, hbmp, 0, (uint)h, buf, ref bih, 0) == 0) return null; }
            finally { Native.ReleaseDC(IntPtr.Zero, dc); }

            bool anyAlpha = false, straight = false;
            for (int i = 0; i < buf.Length; i += 4)
            {
                byte a = buf[i + 3];
                if (a != 0) anyAlpha = true;
                // In premultiplied data a color channel can never exceed alpha
                if (buf[i] > a || buf[i + 1] > a || buf[i + 2] > a) straight = true;
            }
            if (!anyAlpha) { for (int i = 3; i < buf.Length; i += 4) buf[i] = 255; straight = true; }
            var fmt = straight ? PixelFormat.Format32bppArgb : PixelFormat.Format32bppPArgb;

            var bmp = new Bitmap(w, h, fmt);
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, fmt);
            for (int y = 0; y < h; y++) Marshal.Copy(buf, y * w * 4, data.Scan0 + y * data.Stride, w * 4);
            bmp.UnlockBits(data);
            return bmp;
        }

        static Bitmap FromWindowIcon(IntPtr hwnd)
        {
            try
            {
                IntPtr hIcon = IntPtr.Zero;
                foreach (int type in new[] { 1, 2, 0 }) // ICON_BIG, ICON_SMALL2, ICON_SMALL
                {
                    Native.SendMessageTimeout(hwnd, Native.WM_GETICON, (IntPtr)type, IntPtr.Zero, 0x2 /*ABORTIFHUNG*/, 100, out hIcon);
                    if (hIcon != IntPtr.Zero) break;
                }
                if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, -14); // GCLP_HICON
                if (hIcon == IntPtr.Zero) hIcon = Native.GetClassLongPtr(hwnd, -34); // GCLP_HICONSM
                if (hIcon == IntPtr.Zero) return null;
                using (var ico = Icon.FromHandle(hIcon)) return ico.ToBitmap();
            }
            catch { return null; }
        }

        public static string FriendlyName(AppWindow w)
        {
            var custom = NameOverride?.Invoke(w);
            if (custom != null) return custom;
            return RealName(w);
        }

        public static string RealName(AppWindow w)
        {
            try
            {
                if (w.ExePath != null && !WindowTracker.IsHostExe(w.ExePath))
                {
                    var vi = FileVersionInfo.GetVersionInfo(w.ExePath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription)) return vi.FileDescription.Trim();
                    return Path.GetFileNameWithoutExtension(w.ExePath);
                }
            }
            catch { }
            return string.IsNullOrEmpty(w.Title) ? "Window" : w.Title;
        }
    }
}
