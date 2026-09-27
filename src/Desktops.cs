using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Win32;

namespace CLTaskbar
{
    internal class DesktopInfo
    {
        public Guid Id;
        public int Index;       // 0-based order as shown in Task View
        public string Name;     // custom name, or "Desktop N"
        public bool HasCustomName;
    }

    // Reads the virtual desktop list, order and names from the registry (read-only),
    // and uses the documented IVirtualDesktopManager for per-window questions.
    internal class Desktops
    {
        const string Root = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

        public readonly IVirtualDesktopManager Vdm;
        public List<DesktopInfo> List = new List<DesktopInfo>();
        public Guid CurrentId;

        public Desktops()
        {
            try { Vdm = (IVirtualDesktopManager)new VirtualDesktopManagerClass(); } catch { Vdm = null; }
        }

        public void Refresh()
        {
            var list = new List<DesktopInfo>();
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Root))
                {
                    if (k?.GetValue("VirtualDesktopIDs") is byte[] ids)
                    {
                        for (int i = 0; i + 16 <= ids.Length; i += 16)
                        {
                            var b = new byte[16];
                            Array.Copy(ids, i, b, 0, 16);
                            var g = new Guid(b);
                            list.Add(new DesktopInfo { Id = g, Index = list.Count });
                        }
                    }
                }
                foreach (var d in list)
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(Root + @"\Desktops\" + d.Id.ToString("B").ToUpperInvariant()))
                    {
                        var n = k?.GetValue("Name") as string;
                        if (!string.IsNullOrWhiteSpace(n)) { d.Name = n; d.HasCustomName = true; }
                    }
                    if (d.Name == null) d.Name = "Desktop " + (d.Index + 1);
                }
            }
            catch { }
            List = list;
            CurrentId = ReadCurrentId();
        }

        public static Guid ReadCurrentId()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Root))
                    if (k?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length >= 16) return new Guid(Slice(b));
                int sid = Process.GetCurrentProcess().SessionId;
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\" + sid + @"\VirtualDesktops"))
                    if (k?.GetValue("CurrentVirtualDesktop") is byte[] b && b.Length >= 16) return new Guid(Slice(b));
            }
            catch { }
            return Guid.Empty;
        }

        // Fallback when the registry doesn't say: ask which desktop the foreground window is on.
        public Guid DeriveCurrent(uint ownPid)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return Guid.Empty;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == ownPid) return Guid.Empty;
            var id = GetWindowDesktop(fg);
            return id != Guid.Empty && IsOnCurrent(fg) == true && Find(id) != null ? id : Guid.Empty;
        }

        static byte[] Slice(byte[] b) { var r = new byte[16]; Array.Copy(b, r, 16); return r; }

        public DesktopInfo Find(Guid id)
        {
            foreach (var d in List) if (d.Id == id) return d;
            return null;
        }

        public Guid GetWindowDesktop(IntPtr hwnd)
        {
            if (Vdm == null) return Guid.Empty;
            try { return Vdm.GetWindowDesktopId(hwnd, out Guid g) == 0 ? g : Guid.Empty; } catch { return Guid.Empty; }
        }

        public bool? IsOnCurrent(IntPtr hwnd)
        {
            if (Vdm == null) return null;
            try { return Vdm.IsWindowOnCurrentVirtualDesktop(hwnd, out int on) == 0 ? on != 0 : (bool?)null; } catch { return null; }
        }

        public bool MoveOwnWindow(IntPtr hwnd, Guid desktop)
        {
            if (Vdm == null || desktop == Guid.Empty) return false;
            try { return Vdm.MoveWindowToDesktop(hwnd, ref desktop) == 0; } catch { return false; }
        }
    }
}
