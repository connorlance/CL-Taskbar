using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CLTaskbar
{
    internal class AppWindow
    {
        public IntPtr Hwnd;
        public string Title;
        public string ExePath;
        public string Aumid;
        public string GroupKey;
        public Guid DesktopId;      // Guid.Empty = unknown / all desktops
        public bool Minimized;
        public uint Pid;
        public long FirstSeen;
    }

    internal class PinnedItem
    {
        public string Aumid;       // Store apps
        public string LnkPath;
        public string Name;
    }

    internal class WindowTracker
    {
        readonly Desktops desktops;
        readonly uint ownPid = (uint)Process.GetCurrentProcess().Id;
        readonly Dictionary<IntPtr, long> firstSeen = new Dictionary<IntPtr, long>();
        readonly Dictionary<IntPtr, (uint pid, string exe, string aumid)> infoCache = new Dictionary<IntPtr, (uint, string, string)>();
        long counter;
        static Native.EnumWindowsProc enumProc; // kept alive

        public WindowTracker(Desktops d) { desktops = d; }

        // CL-Taskbar's own settings window shows on the bar like any other app (the bar and its popups don't)
        public static volatile IntPtr OwnSettingsWindow;

        static readonly HashSet<string> SkipClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow",
            "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland", "ForegroundStaging",
            "MultitaskingViewFrame", "Windows.Internal.Shell.TabProxyWindow"
        };

        public List<AppWindow> Enumerate()
        {
            var handles = new List<IntPtr>();
            enumProc = (h, l) => { handles.Add(h); return true; };
            Native.EnumWindows(enumProc, IntPtr.Zero);

            var result = new List<AppWindow>();
            var alive = new HashSet<IntPtr>();
            foreach (var h in handles)
            {
                var w = TryMake(h);
                if (w == null) continue;
                alive.Add(h);
                result.Add(w);
            }
            // Forget windows that are gone
            foreach (var dead in firstSeen.Keys.Where(k => !alive.Contains(k)).ToList()) { firstSeen.Remove(dead); infoCache.Remove(dead); }
            result.Sort((a, b) => a.FirstSeen.CompareTo(b.FirstSeen));
            return result;
        }

        AppWindow TryMake(IntPtr h)
        {
            if (!Native.IsWindowVisible(h)) return null;
            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == ownPid && h != OwnSettingsWindow) return null;

            long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE).ToInt64();
            bool appWindow = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (!appWindow)
            {
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return null;
                if ((ex & Native.WS_EX_NOACTIVATE) != 0) return null;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return null;
            }
            string cls = Native.GetClass(h);
            if (SkipClasses.Contains(cls)) return null;
            string title = Native.GetText(h);

            Native.DwmGetWindowAttribute(h, Native.DWMWA_CLOAKED, out int cloaked, 4);
            Guid desk = desktops.GetWindowDesktop(h);
            if (cloaked != 0)
            {
                // Windows on OTHER virtual desktops are cloaked by the shell. Anything else
                // cloaked (suspended store apps, hidden system UI) is not a real taskbar window.
                if ((cloaked & Native.DWM_CLOAKED_SHELL) == 0) return null;
                if (desk == Guid.Empty) return null;
                var onCur = desktops.IsOnCurrent(h);
                if (onCur != false) return null;
                if (title.Length == 0) return null;
            }
            if (cls == "ApplicationFrameWindow" && title.Length == 0) return null;

            if (!firstSeen.TryGetValue(h, out long fs)) { fs = ++counter; firstSeen[h] = fs; }
            if (!infoCache.TryGetValue(h, out var info) || info.pid != pid)
            {
                uint realPid = pid;
                if (cls == "ApplicationFrameWindow") realPid = FindUwpChildPid(h, pid);
                string exe = Native.GetProcessPath(realPid);
                // Helper processes (like Steam's "Client WebHelper") stand in for their main app
                if (!IsHostExe(exe)) exe = AppResolve.MainExe(exe);
                info = (pid, exe, GetAumid(h));
                infoCache[h] = info;
            }

            string key = info.aumid != null ? "aumid:" + info.aumid.ToLowerInvariant()
                       : info.exe != null ? "exe:" + info.exe.ToLowerInvariant()
                       : "hwnd:" + h.ToInt64();
            // Helper programs (like rundll32 for the Sound panel) run many different things: tell them apart by title
            if (IsHostExe(info.exe) && info.aumid == null) key = "host:" + (info.exe ?? "").ToLowerInvariant() + "|" + title;

            return new AppWindow
            {
                Hwnd = h, Title = title, ExePath = info.exe, Aumid = info.aumid, GroupKey = key,
                DesktopId = desk, Minimized = Native.IsIconic(h), Pid = pid, FirstSeen = fs
            };
        }

        public static bool IsHostExe(string exe)
        {
            if (exe == null) return false;
            string n = Path.GetFileName(exe).ToLowerInvariant();
            return n == "applicationframehost.exe" || n == "javaw.exe" || n == "java.exe" || n == "rundll32.exe"
                || n == "dllhost.exe" || n == "mmc.exe" || n == "pythonw.exe" || n == "python.exe";
        }

        static uint FindUwpChildPid(IntPtr frame, uint framePid)
        {
            uint found = framePid;
            Native.EnumChildWindows(frame, (c, l) =>
            {
                Native.GetWindowThreadProcessId(c, out uint p);
                if (p != framePid) { found = p; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }

        static readonly Guid IID_IPropertyStore = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");

        static string GetAumid(IntPtr hwnd)
        {
            try
            {
                var iid = IID_IPropertyStore;
                if (Native.SHGetPropertyStoreForWindow(hwnd, ref iid, out object o) != 0 || o == null) return null;
                var store = (IPropertyStore)o;
                try
                {
                    var key = new Native.PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                    if (store.GetValue(ref key, out var pv) != 0) return null;
                    try
                    {
                        if (pv.vt == 31 && pv.p != IntPtr.Zero) // VT_LPWSTR
                        {
                            var s = Marshal.PtrToStringUni(pv.p);
                            return string.IsNullOrWhiteSpace(s) ? null : s;
                        }
                        return null;
                    }
                    finally { Native.PropVariantClear(ref pv); }
                }
                finally { Marshal.ReleaseComObject(store); }
            }
            catch { return null; }
        }

        // ---- Pinned taskbar shortcuts (read-only) ----
        public static List<PinnedItem> ReadPinned()
        {
            var items = new List<PinnedItem>();
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
                if (!Directory.Exists(dir)) return items;
                foreach (var f in Directory.GetFiles(dir, "*.lnk"))
                    items.Add(new PinnedItem { LnkPath = f, Name = Path.GetFileNameWithoutExtension(f) });

                // Try to match the real taskbar's order by locating each file name inside the
                // Taskband "Favorites" blob the shell keeps. Unknown ones go last, alphabetically.
                string even = "", odd = "";
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband"))
                    if (k?.GetValue("Favorites") is byte[] b && b.Length > 2)
                    {
                        even = Encoding.Unicode.GetString(b);
                        odd = Encoding.Unicode.GetString(b, 1, b.Length - 1);
                    }
                // Store apps (like Windows Terminal) are pinned without a shortcut file; their app IDs are in the same data
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var text in new[] { even, odd })
                    foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"[A-Za-z0-9][\w.\-]{2,}_[a-z0-9]{13}![\w.\-]+"))
                        if (seenIds.Add(m.Value))
                            items.Add(new PinnedItem { Aumid = m.Value, Name = AppResolve.DisplayName(@"shell:AppsFolder\" + m.Value) ?? m.Value.Split('!')[0].Split('_')[0] });

                int Pos(PinnedItem p)
                {
                    string n = p.Aumid ?? Path.GetFileName(p.LnkPath);
                    int i = even.IndexOf(n, StringComparison.OrdinalIgnoreCase);
                    if (i < 0) i = odd.IndexOf(n, StringComparison.OrdinalIgnoreCase);
                    return i < 0 ? int.MaxValue : i;
                }
                items = items.OrderBy(Pos).ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { }
            return items;
        }
    }
}
