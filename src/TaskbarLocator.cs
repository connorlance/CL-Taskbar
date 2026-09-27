using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;

namespace CLTaskbar
{
    // Works out where the real taskbar's app-icon area is (between Task View and the tray).
    // Uses UI Automation, which only reads positions of the taskbar's buttons.
    internal class TaskbarLocator
    {
        public struct Layout
        {
            public bool Valid;
            public IntPtr Tray;
            public Native.RECT Taskbar;   // full taskbar rect (physical px)
            public int IconAreaLeft;      // screen x where our bar may start
            public int IconAreaRight;     // screen x where our bar must end
            public float Scale;
            public bool FromAutomation, FromTaskWindow, Vertical;
            public int RawAutoLeft, RawAutoRight;
        }

        public volatile string AutomationDump = "(not scanned yet)";
        volatile object uiaResult; // Tuple<int left, int right, Native.RECT taskbar>
        readonly Thread thread;
        volatile bool running = true;
        readonly AutoResetEvent wake = new AutoResetEvent(false);

        public TaskbarLocator()
        {
            thread = new Thread(UiaLoop) { IsBackground = true, Name = "TaskbarLocator" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        public void Stop() { running = false; wake.Set(); }
        public void Poke() => wake.Set();

        public Layout Get()
        {
            var L = new Layout();
            IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !Native.GetWindowRect(tray, out var tb) || tb.Width <= 0) return L;
            L.Tray = tray;
            L.Taskbar = tb;
            uint dpi = 96;
            try { dpi = Native.GetDpiForWindow(tray); } catch { }
            if (dpi == 0) dpi = 96;
            L.Scale = dpi / 96f;

            if (tb.Height > tb.Width) { L.Vertical = true; return L; } // side taskbars aren't supported

            // Windows 10: the app-button area is a real window we can simply measure
            if (Theme.IsWin10)
            {
                IntPtr rebar = Native.FindWindowEx(tray, IntPtr.Zero, "ReBarWindow32", null);
                IntPtr tasks = rebar != IntPtr.Zero ? Native.FindWindowEx(rebar, IntPtr.Zero, "MSTaskSwWClass", null) : IntPtr.Zero;
                if (tasks != IntPtr.Zero && Native.GetWindowRect(tasks, out var tr) && tr.Width > 60)
                {
                    L.IconAreaLeft = tr.Left;
                    L.IconAreaRight = tr.Right;
                    L.FromAutomation = false;
                    L.FromTaskWindow = true;
                    L.Valid = true;
                    return L;
                }
            }

            // Classic estimate (always available)
            int cRight;
            IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
            if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Width > 0 && nr.Left > tb.Left + tb.Width / 2)
                cRight = nr.Left;
            else cRight = tb.Right - (int)(260 * L.Scale);
            bool centered = ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1) != 0;
            int cLeft = centered ? tb.Left + tb.Width / 2 - (int)(150 * L.Scale) : tb.Left + (int)(150 * L.Scale);

            int left = -1, right = -1;
            if (uiaResult is Tuple<int, int, Native.RECT> r && r.Item3.Left == tb.Left && r.Item3.Right == tb.Right && r.Item3.Top == tb.Top)
            {
                left = r.Item1; right = r.Item2;
            }
            L.RawAutoLeft = left; L.RawAutoRight = right;
            // Only trust automation values that make sense; otherwise use the classic estimate
            if (left <= tb.Left || left >= tb.Right - 60) left = -1;
            if (right <= tb.Left + 60 || right > tb.Right) right = -1;
            if (left > 0 && right > 0 && right - left < 80) { left = -1; right = -1; }
            L.FromAutomation = left > 0 || right > 0;
            if (right <= 0) right = cRight;
            if (left <= 0) left = cLeft;
            if (right - left < 80) { left = cLeft; right = cRight; }
            L.IconAreaLeft = left;
            L.IconAreaRight = right;
            L.Valid = right - left > 40;
            return L;
        }

        static int ReadDword(string path, string name, int def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(path)) return k?.GetValue(name) is int v ? v : def; }
            catch { return def; }
        }

        void UiaLoop()
        {
            IUIAutomation uia = null;
            IUIAutomationCondition trueCond = null;
            try
            {
                uia = (IUIAutomation)new CUIAutomation();
                uia.CreateTrueCondition(out trueCond);
            }
            catch { return; }

            while (running)
            {
                try
                {
                    IntPtr tray = Native.FindWindow("Shell_TrayWnd", null);
                    if (tray != IntPtr.Zero && Native.GetWindowRect(tray, out var tb))
                    {
                        var res = Scan(uia, trueCond, tray, tb, out string dump);
                        AutomationDump = dump;
                        if (res != null) uiaResult = res;
                    }
                }
                catch { }
                wake.WaitOne(2000);
            }
        }

        static Tuple<int, int, Native.RECT> Scan(IUIAutomation uia, IUIAutomationCondition cond, IntPtr tray, Native.RECT tb, out string dump)
        {
            var sbDump = new System.Text.StringBuilder();
            dump = "(automation failed)";
            if (uia.ElementFromHandle(tray, out var root) != 0 || root == null) return null;
            IUIAutomationElementArray arr = null;
            try
            {
                if (root.FindAll(4 /*Descendants*/, cond, out arr) != 0 || arr == null) return null;
                arr.get_Length(out int n);

                int startRight = -1, leftButtonsRight = -1, startLeft = int.MaxValue;
                var rightCandidates = new List<Native.RECT>();
                var trayish = new List<int>();
                for (int i = 0; i < n; i++)
                {
                    if (arr.GetElement(i, out var e) != 0 || e == null) continue;
                    try
                    {
                        e.get_CurrentAutomationId(out string id);
                        e.get_CurrentClassName(out string cls);
                        e.get_CurrentBoundingRectangle(out var r);
                        id = id ?? ""; cls = cls ?? "";
                        if (sbDump.Length < 6000) sbDump.AppendLine($"  id='{id}' class='{cls}' L={r.Left} R={r.Right} T={r.Top} B={r.Bottom}");
                        if (r.Width <= 0 || r.Height <= 0) continue;
                        if (id == "StartButton") { startRight = r.Right; startLeft = r.Left; }
                        else if (id == "TaskViewButton" || id.StartsWith("Search", StringComparison.OrdinalIgnoreCase))
                            leftButtonsRight = Math.Max(leftButtonsRight, r.Right);
                        else if (id == "WidgetsButton" || id.IndexOf("Copilot", StringComparison.OrdinalIgnoreCase) >= 0)
                            rightCandidates.Add(r);
                        else if (id == "SystemTrayIcon" || id == "NotifyItemIcon" || cls.IndexOf("SystemTray", StringComparison.OrdinalIgnoreCase) >= 0
                                 || cls == "TrayNotifyWnd" || cls == "TrayButton")
                            trayish.Add(r.Left);
                    }
                    finally { Marshal.ReleaseComObject(e); }
                }

                int left = Math.Max(startRight, leftButtonsRight);
                if (left > 0) left += 2;
                int right = -1;
                foreach (var x in trayish) if (x > left && (right < 0 || x < right)) right = x;
                IntPtr notify = Native.FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
                if (notify != IntPtr.Zero && Native.GetWindowRect(notify, out var nr) && nr.Width > 0 && nr.Left > left)
                    right = right < 0 ? nr.Left : Math.Min(right, nr.Left);
                // Widgets / Copilot buttons that sit on the right side limit us too
                foreach (var rc in rightCandidates)
                    if (rc.Left > left && startLeft != int.MaxValue && rc.Left > startLeft && (right < 0 || rc.Left < right)) right = rc.Left;
                if (right > 0) right -= 2;
                dump = $"{n} elements, startRight={startRight} leftButtonsRight={leftButtonsRight} -> left={left} right={right}\r\n" + sbDump;
                return Tuple.Create(left, right, tb);
            }
            finally
            {
                if (arr != null) Marshal.ReleaseComObject(arr);
                Marshal.ReleaseComObject(root);
            }
        }
    }
}
