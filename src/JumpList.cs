using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    // Reads an app's "Recent" and "Frequent" jump-list items (the files and folders Windows remembers you
    // opened with that app) through Windows' documented, read-only IApplicationDocumentLists.
    // Only what Windows keeps for the app automatically is available; an app's own custom sections
    // (like Chrome's "Most visited" or its "New incognito window" task) are private to Explorer.
    internal static class JumpList
    {
        public class Entry { public string Name, Path; public bool IsFolder; }

        [ComImport, Guid("3c594f9f-9f30-47a1-979a-c9e83d3d0a06"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IApplicationDocumentLists
        {
            void SetAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
            [PreserveSig] int GetList(int listType, uint itemsDesired, [In] ref Guid riid, out IntPtr ppv);
        }
        [ComImport, Guid("86bec222-30f2-47e0-9f25-60d11cd75c28")] class ApplicationDocumentLists { }

        [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellLinkW
        {
            [PreserveSig] int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
            [PreserveSig] int GetIDList(out IntPtr pidl);
            [PreserveSig] int SetIDList(IntPtr pidl);
            [PreserveSig] int GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CountFn(IntPtr self, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetAtFn(IntPtr self, uint index, [In] ref Guid riid, out IntPtr obj);

        static T Slot<T>(IntPtr obj, int slot) where T : class =>
            (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));

        static readonly Guid IID_IObjectArray = new Guid("92CA9DCD-5622-4bba-A805-5E9F541BD8C9");
        static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");
        const int SIGDN_NORMALDISPLAY = 0, SIGDN_DESKTOPABSOLUTEPARSING = unchecked((int)0x80028000), SFGAO_FOLDER = 0x20000000;

        // Recent items (or, if an app keeps none, its frequent ones). Tries each possible app ID.
        public static List<Entry> For(IEnumerable<string> appIds, int max, out string title)
        {
            title = "Recent";
            foreach (var id in appIds)
            {
                if (string.IsNullOrWhiteSpace(id)) continue;
                var list = Get(id, 0, max);                        // ADLT_RECENT
                if (list.Count > 0) return list;
                list = Get(id, 1, max);                            // ADLT_FREQUENT
                if (list.Count > 0) { title = "Frequent"; return list; }
            }
            return new List<Entry>();
        }

        static List<Entry> Get(string appId, int type, int max)
        {
            var result = new List<Entry>();
            IApplicationDocumentLists adl = null;
            IntPtr arr = IntPtr.Zero;
            try
            {
                adl = (IApplicationDocumentLists)new ApplicationDocumentLists();
                adl.SetAppID(appId);
                var iid = IID_IObjectArray;
                if (adl.GetList(type, (uint)max, ref iid, out arr) != 0 || arr == IntPtr.Zero) return result;
                if (Slot<CountFn>(arr, 3)(arr, out uint count) != 0) return result;
                for (uint i = 0; i < count && result.Count < max; i++)
                {
                    var unk = IID_IUnknown;
                    if (Slot<GetAtFn>(arr, 4)(arr, i, ref unk, out IntPtr p) != 0 || p == IntPtr.Zero) continue;
                    try
                    {
                        var o = Marshal.GetObjectForIUnknown(p);
                        Entry e = null;
                        if (o is AppResolve.IShellItem si) e = FromItem(si);
                        else if (o is IShellLinkW sl) e = FromLink(sl);
                        if (e != null && !string.IsNullOrEmpty(e.Path)) result.Add(e);
                        Marshal.ReleaseComObject(o);
                    }
                    catch { }
                    finally { Marshal.Release(p); }
                }
            }
            catch { }
            finally
            {
                if (arr != IntPtr.Zero) Marshal.Release(arr);
                if (adl != null) Marshal.ReleaseComObject(adl);
            }
            return result;
        }

        static Entry FromItem(AppResolve.IShellItem si)
        {
            string Name(int sigdn)
            {
                if (si.GetDisplayName(sigdn, out IntPtr s) != 0 || s == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(s); } finally { Marshal.FreeCoTaskMem(s); }
            }
            si.GetAttributes(SFGAO_FOLDER, out uint attrs);
            return new Entry { Name = Name(SIGDN_NORMALDISPLAY), Path = Name(SIGDN_DESKTOPABSOLUTEPARSING), IsFolder = (attrs & SFGAO_FOLDER) != 0 };
        }

        static Entry FromLink(IShellLinkW sl)
        {
            var sb = new System.Text.StringBuilder(1024);
            if (sl.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0 || sb.Length == 0) return null;
            string path = sb.ToString();
            var d = new System.Text.StringBuilder(1024);
            string name = sl.GetDescription(d, d.Capacity) == 0 && d.Length > 0 ? d.ToString() : System.IO.Path.GetFileName(path);
            return new Entry { Name = name, Path = path, IsFolder = Directory.Exists(path) };
        }

        // ---- which ID Windows files an app's jump list under ----

        // Apps that name themselves (Store apps, Chrome, Explorer...) use that name. Other programs get one made
        // from their path, with the start of the path written as a Windows "known folder" ID.
        public static List<string> IdsFor(string aumid, string exe)
        {
            var ids = new List<string>();
            if (!string.IsNullOrWhiteSpace(aumid)) ids.Add(aumid);
            if (!string.IsNullOrWhiteSpace(exe))
            {
                if (Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase)) ids.Add("Microsoft.Windows.Explorer");
                var k = KnownFolderForm(exe);
                if (k != null) ids.Add(k);
                ids.Add(exe);
            }
            return ids;
        }

        static string KnownFolderForm(string exe)
        {
            (string dir, string guid)[] folders =
            {
                (Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"),
                (Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "{6D809377-6AF0-444B-8957-A3773F02200E}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.SystemX86), "{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.System), "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "{62AB5D82-FDC1-4DC3-A9DD-070D1D495D97}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.Windows), "{F38BF404-1D43-42F2-9305-67DE0B28FC23}"),
                (Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "{5E6C858F-0E22-4760-9AFE-EA3317B67173}"),
            };
            string best = null; int bestLen = 0;
            foreach (var (dir, guid) in folders)
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string d = dir.TrimEnd('\\') + "\\";
                if (exe.StartsWith(d, StringComparison.OrdinalIgnoreCase) && d.Length > bestLen) { best = guid + "\\" + exe.Substring(d.Length); bestLen = d.Length; }
            }
            return best;
        }
    }
}
