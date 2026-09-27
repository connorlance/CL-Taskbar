using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CLTaskbar
{
    // Finds the "real" app behind helper processes, and display names of shell items.
    internal static class AppResolve
    {
        static readonly Dictionary<string, string> mainExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        static bool LooksLikeHelper(string exe)
        {
            string n = Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            if (n.Contains("helper") || n.EndsWith("host") && n != "applicationframehost" || n.Contains("renderer")) return true;
            try
            {
                string d = FileVersionInfo.GetVersionInfo(exe).FileDescription ?? "";
                return d.IndexOf("helper", StringComparison.OrdinalIgnoreCase) >= 0;
            }
            catch { return false; }
        }

        // steamwebhelper.exe -> Steam\steam.exe (looks up to 4 folders up for the app's own exe)
        public static string MainExe(string exe)
        {
            if (string.IsNullOrEmpty(exe)) return exe;
            lock (mainExe)
            {
                if (mainExe.TryGetValue(exe, out var cached)) return cached;
                string result = exe;
                try
                {
                    if (LooksLikeHelper(exe))
                    {
                        string product = null;
                        try { product = FileVersionInfo.GetVersionInfo(exe).ProductName?.Trim(); } catch { }
                        var dir = new DirectoryInfo(Path.GetDirectoryName(exe));
                        for (int up = 0; up < 5 && dir != null && result == exe; up++, dir = dir.Parent)
                        {
                            // 1) an exe named after its folder (Steam\steam.exe)
                            string named = Path.Combine(dir.FullName, dir.Name + ".exe");
                            if (up > 0 && File.Exists(named) && !LooksLikeHelper(named)) { result = named; break; }
                            // 2) an exe named after the product
                            if (!string.IsNullOrEmpty(product))
                            {
                                string byProduct = Path.Combine(dir.FullName, product.Split(' ')[0] + ".exe");
                                if (File.Exists(byProduct) && !string.Equals(byProduct, exe, StringComparison.OrdinalIgnoreCase) && !LooksLikeHelper(byProduct)) { result = byProduct; break; }
                            }
                        }
                    }
                }
                catch { }
                mainExe[exe] = result;
                return result;
            }
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, [In] ref Guid bhid, [In] ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem parent);
            [PreserveSig] int GetDisplayName(int sigdn, out IntPtr name);
            [PreserveSig] int GetAttributes(uint mask, out uint attrs);
            [PreserveSig] int Compare(IShellItem other, uint hint, out int order);
        }

        public static readonly Guid IID_IShellItem = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");

        public static IShellItem Item(string path)
        {
            try
            {
                var iid = IID_IShellItem;
                return Native.SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out object o) == 0 ? o as IShellItem : null;
            }
            catch { return null; }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object store);

        static readonly Dictionary<string, string> packageExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Microsoft.WindowsTerminal_8wekyb3d8bbwe!App -> ...\WindowsTerminal.exe (only when the name clearly matches)
        public static string PackageExe(string aumid)
        {
            lock (packageExe)
            {
                if (packageExe.TryGetValue(aumid, out var c)) return c;
                string result = null;
                try
                {
                    string dir = null;
                    var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
                    if (SHGetPropertyStoreFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, 0, ref iid, out object o) == 0 && o is IPropertyStore store)
                    {
                        try
                        {
                            var key = new Native.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 15 };  // PackageInstallPath
                            if (store.GetValue(ref key, out var pv) == 0)
                                try { if (pv.vt == 31 && pv.p != IntPtr.Zero) dir = Marshal.PtrToStringUni(pv.p); } finally { Native.PropVariantClear(ref pv); }
                        }
                        finally { Marshal.ReleaseComObject(store); }
                    }
                    if (dir != null && Directory.Exists(dir))
                    {
                        string pkg = aumid.Split('_')[0];                       // Microsoft.WindowsTerminal
                        string last = pkg.Substring(pkg.LastIndexOf('.') + 1);  // WindowsTerminal
                        foreach (var f in Directory.GetFiles(dir, "*.exe"))
                        {
                            string n = Path.GetFileNameWithoutExtension(f);
                            if (n.Equals(last, StringComparison.OrdinalIgnoreCase)) { result = f; break; }
                        }
                    }
                }
                catch { }
                packageExe[aumid] = result;
                return result;
            }
        }

        public static string DisplayName(string path)
        {
            var it = Item(path);
            if (it == null) return null;
            try
            {
                if (it.GetDisplayName(0 /*SIGDN_NORMALDISPLAY*/, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
            catch { return null; }
            finally { Marshal.ReleaseComObject(it); }
        }
    }
}
