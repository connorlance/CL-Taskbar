using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    // Works out which app a pinned item really is (from a shortcut's app ID and target), so a running
    // window can be matched to its pin even when the names differ ("File Explorer" vs "Windows Explorer").
    internal static class AppIdentity
    {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr bc, int flags, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object store);

        struct Id { public string Aumid, Exe; }
        static readonly Dictionary<string, Id> cache = new Dictionary<string, Id>(StringComparer.OrdinalIgnoreCase);

        static Id Of(LaunchItem it)
        {
            string t = Launcher.Expand(it.Target);
            if (cache.TryGetValue(t, out var id)) return id;
            id = new Id();
            if (t.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)) id.Aumid = t.Substring(17);
            else if (t.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                id.Aumid = ReadString(t, new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);            // System.AppUserModel.ID
                id.Exe = ReadString(t, new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);             // System.Link.TargetParsingPath
                if (string.IsNullOrEmpty(id.Exe) || !id.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id.Exe = LinkTarget(t) ?? id.Exe;
                if ((id.Exe == null || !id.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) &&
                    Path.GetFileNameWithoutExtension(t).Equals("File Explorer", StringComparison.OrdinalIgnoreCase))
                    id.Exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                if (id.Exe != null && id.Exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id.Exe = AppResolve.MainExe(id.Exe);
            }
            else if (it.Type == "program")
            {
                var r = Launcher.Resolve(t);
                if (r.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id.Exe = r;
            }
            cache[t] = id;
            return id;
        }

        // The app a pinned item opens: its app ID and/or program path (for jump lists)
        public static (string aumid, string exe) IdOf(LaunchItem it)
        {
            if (it == null || it.Type != "program") return (null, null);
            var id = Of(it);
            return (id.Aumid, id.Exe);
        }

        public static bool Matches(LaunchItem it, AppWindow w)
        {
            if (it == null || w == null || it.Type != "program") return false;
            var id = Of(it);
            if (id.Aumid != null && w.Aumid != null && string.Equals(id.Aumid, w.Aumid, StringComparison.OrdinalIgnoreCase)) return true;
            if (id.Exe == null || w.ExePath == null || WindowTracker.IsHostExe(w.ExePath)) return false;
            if (!string.Equals(Path.GetFullPath(id.Exe), Path.GetFullPath(w.ExePath), StringComparison.OrdinalIgnoreCase)) return false;
            // same program: a match unless both have different app IDs (e.g. two different web apps in one browser)
            if (id.Aumid != null && w.Aumid != null)
                return w.Aumid.StartsWith(id.Aumid, StringComparison.OrdinalIgnoreCase) || id.Aumid.StartsWith(w.Aumid, StringComparison.OrdinalIgnoreCase)
                    || w.Aumid.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) < 0;
            return true;
        }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
        }
        [ComImport, Guid("00021401-0000-0000-C000-000000000046")] class ShellLink { }

        internal static string LinkTarget(string lnk)
        {
            try
            {
                var link = new ShellLink();
                try
                {
                    ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(lnk, 0);
                    var sb = new System.Text.StringBuilder(1024);
                    if (((IShellLinkW)link).GetPath(sb, sb.Capacity, IntPtr.Zero, 0) == 0 && sb.Length > 0) return Environment.ExpandEnvironmentVariables(sb.ToString());
                }
                finally { Marshal.ReleaseComObject(link); }
            }
            catch { }
            return null;
        }

        static string ReadString(string path, Guid fmt, int pid)
        {
            try
            {
                var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
                if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out object o) != 0 || o == null) return null;
                var store = (IPropertyStore)o;
                try
                {
                    var key = new Native.PROPERTYKEY { fmtid = fmt, pid = pid };
                    if (store.GetValue(ref key, out var pv) != 0) return null;
                    try { return pv.vt == 31 && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null; }
                    finally { Native.PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return null; }
        }
    }
}
