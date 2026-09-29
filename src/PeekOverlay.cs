using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CLTaskbar
{
    // "Peek": pointing at a window's preview shows only that window, over your desktop, like the real taskbar.
    // Windows' own peek fades slowly and its speed can't be changed, so this draws the same picture ourselves:
    // on each screen (never over the taskbars) a live copy of the desktop (wallpaper and icons), and on top of it
    // a live copy of the window, at its exact size and place. Minimized windows show where they'd come back to.
    // It only draws: no window is moved, shown or changed. Appears and switches instantly.
    internal class PeekOverlay
    {
        readonly List<Host> hosts = new List<Host>();   // one per screen
        public IntPtr Current { get; private set; }

        // A click-through, never-activating, always-on-top window that live copies are drawn into
        class Host : Form
        {
            public readonly List<IntPtr> Desktop = new List<IntPtr>();   // live copies of the desktop
            public IntPtr Window;                                         // live copy of the peeked window
            public bool InUse;

            public Host()
            {
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                AutoScaleMode = AutoScaleMode.None;
                BackColor = SystemColors.Desktop;   // (only seen if the desktop can't be copied)
                TopMost = true;
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | 0x20 /*WS_EX_TRANSPARENT*/;
                    return cp;
                }
            }
            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Native.NoOpenAnimation(Handle);
            }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
                if (m.Msg == 0x84 /*WM_NCHITTEST*/) { m.Result = (IntPtr)(-1) /*HTTRANSPARENT*/; return; }
                base.WndProc(ref m);
            }

            public void Unregister(bool desktopToo)
            {
                if (Window != IntPtr.Zero) { Native.DwmUnregisterThumbnail(Window); Window = IntPtr.Zero; }
                if (desktopToo) { foreach (var t in Desktop) Native.DwmUnregisterThumbnail(t); Desktop.Clear(); }
            }
        }

        // Show this window (or switch to it). keepOnTop: our preview popup, which stays above it all.
        public void Show(IntPtr target, IntPtr keepOnTop)
        {
            if (target == Current) return;
            if (!Native.IsWindow(target)) { Hide(); return; }
            bool starting = Current == IntPtr.Zero;

            if (starting)
            {
                // cover each screen's working area (so the taskbars stay as they are) with a live copy of the desktop
                var screens = Screen.AllScreens;
                var desk = DesktopWindows();
                while (hosts.Count < screens.Length) hosts.Add(new Host());
                for (int i = 0; i < hosts.Count; i++)
                {
                    var h = hosts[i];
                    h.Unregister(true);
                    h.InUse = i < screens.Length;
                    if (!h.InUse) { if (h.Visible) h.Hide(); continue; }
                    h.Bounds = screens[i].WorkingArea;
                    foreach (var (hwnd, rect) in desk)   // bottom first, so the icons end up above the wallpaper
                        if (Register(h, hwnd, rect, null, out var th)) h.Desktop.Add(th);
                }
            }

            // the window itself, over the desktop copy, on every screen it's on
            foreach (var h in hosts)
            {
                if (!h.InUse) continue;
                if (h.Window != IntPtr.Zero) { Native.DwmUnregisterThumbnail(h.Window); h.Window = IntPtr.Zero; }
                if (Place(target, h, out var th)) h.Window = th;
            }

            if (starting)
                foreach (var h in hosts)
                    if (h.InUse)
                    {
                        if (!h.Visible) h.Show();
                        Native.SetWindowPos(h.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                    }
            // our preview popup stays on top of it all
            if (keepOnTop != IntPtr.Zero)
                Native.SetWindowPos(keepOnTop, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            Current = target;
        }

        public void Hide()
        {
            foreach (var h in hosts) { h.Unregister(true); if (h.Visible) h.Hide(); }
            Current = IntPtr.Zero;
        }

        public void Dispose()
        {
            Hide();
            foreach (var h in hosts) h.Dispose();
            hosts.Clear();
        }

        // Draws a live copy of `source` into the host: its `screen` rectangle (in screen pixels), optionally only part of it
        static bool Register(Host h, IntPtr source, Rectangle screen, Rectangle? part, out IntPtr thumb)
        {
            thumb = IntPtr.Zero;
            var hr = h.Bounds;
            if (!screen.IntersectsWith(hr)) return false;
            if (Native.DwmRegisterThumbnail(h.Handle, source, out thumb) != 0) { thumb = IntPtr.Zero; return false; }
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = Native.DWM_TNP_RECTDESTINATION | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY
                          | (part.HasValue ? Native.DWM_TNP_RECTSOURCE : 0),
                rcDestination = new Native.RECT { Left = screen.Left - hr.Left, Top = screen.Top - hr.Top, Right = screen.Right - hr.Left, Bottom = screen.Bottom - hr.Top },
                opacity = 255, fVisible = true, fSourceClientAreaOnly = false
            };
            if (part.HasValue) p.rcSource = new Native.RECT { Left = part.Value.Left, Top = part.Value.Top, Right = part.Value.Right, Bottom = part.Value.Bottom };
            Native.DwmUpdateThumbnailProperties(thumb, ref p);
            return true;
        }

        // Draws the peeked window into the host where it is on the screen (or, minimized, where it would come back to)
        static bool Place(IntPtr target, Host h, out IntPtr thumb)
        {
            thumb = IntPtr.Zero;
            if (!Native.IsIconic(target))
            {
                // exactly where it is, without its invisible resize borders
                if (!Native.GetVisibleFrame(target, out var fr) || !Native.GetWindowRect(target, out var wr)) return false;
                var frame = fr.ToRectangle();
                if (frame.Width < 2 || frame.Height < 2) return false;
                return Register(h, target, frame, new Rectangle(frame.Left - wr.Left, frame.Top - wr.Top, frame.Width, frame.Height), out thumb);
            }

            // minimized: where it would be restored to (Windows keeps its last picture)
            var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)) };
            if (!GetWindowPlacement(target, ref wp)) return false;
            var n = wp.normal.ToRectangle();
            // those numbers are relative to the working area of the window's screen, not the whole screen
            var mi = new Native.MONITORINFO { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFO)) };
            Native.GetMonitorInfo(Native.MonitorFromPoint(new Native.POINT { X = n.Left + n.Width / 2, Y = n.Top + n.Height / 2 }, 2), ref mi);
            var work = mi.rcWork.ToRectangle(); var mon = mi.rcMonitor.ToRectangle();
            n.Offset(work.Left - mon.Left, work.Top - mon.Top);

            if (Native.DwmRegisterThumbnail(h.Handle, target, out var probe) != 0) return false;
            Native.DwmQueryThumbnailSourceSize(probe, out var ss);
            Native.DwmUnregisterThumbnail(probe);

            if ((wp.flags & 2 /*WPF_RESTORETOMAXIMIZED*/) != 0 && ss.cx > 0 && ss.cy > 0)
            {
                // it comes back maximized: fill the working area (trimming the picture's edges that hang off it)
                int w = Math.Min(ss.cx, work.Width), hh = Math.Min(ss.cy, work.Height);
                return Register(h, target, new Rectangle(work.Left, work.Top, w, hh), new Rectangle((ss.cx - w) / 2, (ss.cy - hh) / 2, w, hh), out thumb);
            }
            if (ss.cx > 0 && ss.cy > 0) n.Size = new Size(ss.cx, ss.cy);   // the picture's own size (invisible borders show the desktop)
            return n.Width > 1 && n.Height > 1 && Register(h, target, n, null, out thumb);
        }

        // The windows that make up the desktop (wallpaper and icons), bottom first
        static List<(IntPtr hwnd, Rectangle rect)> DesktopWindows()
        {
            var list = new List<(IntPtr, Rectangle)>();
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) return list;
            Native.GetWindowThreadProcessId(shell, out uint shellPid);
            Native.EnumWindowsProc cb = (hwnd, l) =>
            {
                Native.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid != shellPid || !Native.IsWindowVisible(hwnd)) return true;
                string cls = Native.GetClass(hwnd);
                if (cls != "Progman" && cls != "WorkerW") return true;
                if (!Native.GetWindowRect(hwnd, out var r)) return true;
                var rect = r.ToRectangle();
                if (rect.Width < 100 || rect.Height < 100) return true;
                list.Add((hwnd, rect));
                return true;
            };
            Native.EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            list.Reverse();   // EnumWindows goes top to bottom
            return list;
        }

        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public Native.POINT min, max;
            public Native.RECT normal;
        }
    }
}
