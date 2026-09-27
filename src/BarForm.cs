using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace CLTaskbar
{
    internal enum GroupKind { Pins, AllDesktops, Desktop, FlatWindows, Menu, Gap }

    internal class GroupModel
    {
        public GroupKind Kind;
        public BarSection Section;
        public DesktopInfo Desktop;
        public string Label;
        public string Tooltip;
        public bool IsCurrent;
        public int GapBefore;          // logical px of space before this group
        public Color? BackColor, LabelColor, LabelBack;
        public string LabelImage;      // picture shown in the desktop label
        public bool DividerBefore;
        public List<BarButton> Buttons = new List<BarButton>();
        public Rectangle Bounds, LabelRect;
    }

    internal class BarButton
    {
        public string Key;
        public GroupModel Group;
        public List<AppWindow> Windows = new List<AppWindow>();
        public LaunchItem Pin;         // a pinned item
        public bool DeskPin;           // pinned to a specific virtual desktop (Group.Desktop)
        public int GridIcon;           // >0: a small icon in a grid-style group (icon size in px)
        public BarSection Menu;        // a menu button
        public BarSection Tray;        // a popup-grid group's button
        public Rectangle Rect;
    }

    // The overlay bar. It sits on top of the real taskbar's icon area and never changes the real taskbar;
    // close this app and the normal taskbar is right there underneath.
    internal partial class BarForm : Form, Launcher.IWindowHost
    {
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        Config cfg;
        readonly Desktops desktops = new Desktops();
        readonly WindowTracker tracker;
        readonly TaskbarLocator locator = new TaskbarLocator();
        Theme theme;
        Color? sampled;
        List<GroupModel> groups = new List<GroupModel>();
        List<AppWindow> lastWindows = new List<AppWindow>();
        readonly Dictionary<IntPtr, long> lastActivated = new Dictionary<IntPtr, long>();
        readonly HashSet<IntPtr> flashing = new HashSet<IntPtr>();
        IntPtr lastForeign;       // last foreground window that isn't ours
        IntPtr prevApp;           // last real app window you used (not the taskbar or Start)
        object hover, pressed;
        int scrollX, contentWidth;
        float scale = 1;
        string signature = "";
        int settingsVersion;
        bool dirty = true, shouldShow;
        int tickCount;
        Guid lastDesktop, scrolledFor, lastReg;
        bool barCloaked;
        static readonly uint OwnPid = (uint)Process.GetCurrentProcess().Id;
        IntPtr lastTray;
        Rectangle lastBounds;
        Font labelFont, textFont;
        TaskbarLocator.Layout lastLayout;
        string lastHideReason;
        int hideReasonCount, badTicks;

        readonly Timer tick = new Timer { Interval = 200 };
        readonly Timer colorTimer = new Timer { Interval = 3000 };
        readonly Timer previewTimer = new Timer();
        readonly Timer saveTimer = new Timer { Interval = 400 };
        readonly Timer burstTimer = new Timer { Interval = 30 };
        DateTime burstUntil;
        readonly PreviewForm preview = new PreviewForm();
        NotifyIcon trayIcon;
        int shellMsg;
        IntPtr winEventHook;
        Native.WinEventDelegate winEventProc;

        public Config Settings => cfg;

        // A custom icon you gave a pinned app also shows on its open windows
        readonly Dictionary<string, string> pinIconCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string PinIconFor(AppWindow w)
        {
            if (pinIconCache.TryGetValue(w.GroupKey, out var c)) return c;
            string found = null;
            var all = cfg.Layout.Where(x => x.Type == "pins" || x.Type == "item").SelectMany(x => x.Items).Concat(cfg.Desktops.SelectMany(d => d.Pins));
            foreach (var it in all)
                if (!string.IsNullOrWhiteSpace(it.IconPath) && it.Type == "program" && AppIdentity.Matches(it, w)) { found = it.IconPath; break; }
            pinIconCache[w.GroupKey] = found;
            return found;
        }

        // A hover/press shade that stays visible on top of whatever background color is under it
        Color Shade(Color? under, Color normal, int onDark, int onLight)
        {
            if (!under.HasValue || under.Value.A < 40) return normal;
            var c = under.Value;
            double lum = (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;
            return lum < 0.55 ? Color.FromArgb(onDark, 255, 255, 255) : Color.FromArgb(onLight, 0, 0, 0);
        }
        public int WindowsIconSize => SmallButtons ? 16 : 24;
        public int WindowsButtonWidth => Theme.IsWin10 ? (SmallButtons ? 40 : 48) : 44;
        public List<AppWindow> SeenApps() => lastWindows.ToList();
        // The buttons currently shown for one desktop, in taskbar order (for the settings window)
        public List<BarButton> ButtonsForDesktop(Guid id) =>
            groups.Where(g => g.Kind == GroupKind.Desktop && g.Desktop.Id == id).SelectMany(g => g.Buttons).ToList();
        public List<BarButton> FlatButtons() =>
            groups.Where(g => g.Kind == GroupKind.FlatWindows || g.Kind == GroupKind.AllDesktops).SelectMany(g => g.Buttons).ToList();
        public List<DesktopInfo> DesktopList() { desktops.Refresh(); return desktops.List.ToList(); }

        public BarForm()
        {
            Launcher.Init();
            cfg = Config.Load();
            tracker = new WindowTracker(desktops);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            TopMost = true;
            Text = "CL-Taskbar";
            Bounds = new Rectangle(-32000, -32000, 10, 10);
            theme = Theme.Build(cfg, null);

            tick.Tick += (s, e) => OnTick();
            colorTimer.Tick += (s, e) => SampleColor();
            previewTimer.Tick += (s, e) => { previewTimer.Stop(); ShowPreviewFor(hover); };
            saveTimer.Tick += (s, e) => { saveTimer.Stop(); cfg.Save(); };
            burstTimer.Tick += (s, e) => { if (DateTime.Now > burstUntil) burstTimer.Stop(); ReassertTopmost(); };
            preview.OnActivate = w => Activate(w);
            preview.OnCloseWindow = w => { CloseWindow(w.Hwnd); dirty = true; };
            Launcher.IconsChanged += () => { Invalidate(); };
            SetupTrayIcon();
            // Your custom icons and names for apps apply everywhere (bar, previews, menus)
            Icons.Override = (w, px) =>
            {
                var st = cfg.FindApp(w.GroupKey);
                string path = st != null && !string.IsNullOrWhiteSpace(st.IconPath) ? st.IconPath : PinIconFor(w);
                return path != null ? Launcher.FromIconFileCached(Launcher.Expand(path), px) : null;
            };
            Icons.NameOverride = w =>
            {
                var st = cfg.FindApp(w.GroupKey);
                return st != null && !string.IsNullOrWhiteSpace(st.Name) ? st.Name : null;
            };
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            shellMsg = Native.RegisterWindowMessage("SHELLHOOK");
            Native.RegisterShellHookWindow(Handle);
            winEventProc = OnWinEvent;
            winEventHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, winEventProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
            tick.Start();
            colorTimer.Start();
            StartBadges();
            DesktopWatcher.Start(() => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)DesktopMaybeChanged); } catch { } });
            BeginInvoke((Action)(() => { OnTick(); SampleColor(); }));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = (IntPtr)Native.MA_NOACTIVATE; return; }
            if (ShellMenu.HandleMessage(ref m)) return;
            if (shellMsg != 0 && m.Msg == shellMsg)
            {
                int code = m.WParam.ToInt32();
                if (code == 0x8006) flashing.Add(m.LParam);                     // HSHELL_FLASH
                else if (code == 4 || code == 0x8004) flashing.Remove(m.LParam); // activated
                dirty = true;
            }
            base.WndProc(ref m);
        }

        void OnWinEvent(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (hwnd == IntPtr.Zero) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == OwnPid) return;
            IntPtr root = GetAncestor(hwnd, 3);
            if (root == IntPtr.Zero) root = hwnd;
            lastForeign = root;
            string cls = Native.GetClass(root);
            if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
            {
                // After Start, Windows often leaves keyboard focus on its own taskbar, which keeps it in front of
                // everything (that's why clicking anywhere fixed it). Give focus back to the app you were using.
                var t = new Timer { Interval = 350 };
                t.Tick += (s, e) =>
                {
                    t.Dispose();
                    IntPtr fg = GetAncestor(Native.GetForegroundWindow(), 3);
                    string fc = Native.GetClass(fg);
                    if ((fc == "Shell_TrayWnd" || fc == "Shell_SecondaryTrayWnd") && prevApp != IntPtr.Zero && Native.IsWindow(prevApp) && Native.IsWindowVisible(prevApp))
                    {
                        if (Native.IsIconic(prevApp) || !Native.SetForegroundWindow(prevApp)) Native.SwitchToThisWindow(prevApp, true);
                    }
                    ReassertTopmost();
                };
                t.Start();
            }
            else if (cls != "Windows.UI.Core.CoreWindow" && cls != "XamlExplorerHostIslandWindow" && cls != "Progman" && cls != "WorkerW")
                prevApp = root;
            lastActivated[root] = Stopwatch.GetTimestamp();
            flashing.Remove(root);
            if (cfg.CountNotificationBadges) { var aw = lastWindows?.FirstOrDefault(x => x.Hwnd == root); if (aw != null) Badges.MarkSeen(aw.Aumid); }
            dirty = true;
            DesktopMaybeChanged();
            ReassertTopmost();
            // When Start or search opens, Windows lifts its own taskbar; push ours back up repeatedly for a moment
            burstUntil = DateTime.Now.AddMilliseconds(1500);
            if (!burstTimer.Enabled) burstTimer.Start();
        }

        // Called by the settings window whenever something changes
        public void ApplySettings(bool saveNow = false)
        {
            settingsVersion++;
            Launcher.ClearIcons();
            Icons.Clear();
            pinIconCache.Clear();
            labelFont?.Dispose(); labelFont = null;
            textFont?.Dispose(); textFont = null;
            theme = Theme.Build(cfg, sampled);
            if (!cfg.Color.Equals("sample", StringComparison.OrdinalIgnoreCase)) sampled = null;
            SampleColor();
            if (trayIcon != null) trayIcon.Visible = cfg.ShowTrayIcon;
            StartBadges();
            signature = "";
            lastBounds = Rectangle.Empty;
            dirty = true;
            preview.HidePopup();
            RefreshModel();
            Invalidate();
            if (saveNow) { saveTimer.Stop(); cfg.Save(); }
            else { saveTimer.Stop(); saveTimer.Start(); }
        }

        // ================= Launcher.IWindowHost =================
        public List<AppWindow> AllWindows() => lastWindows;
        public Guid CurrentDesktop => lastDesktop;
        public long LastActivated(IntPtr h) => lastActivated.TryGetValue(h, out long t) ? t : 0;
        public void ActivateWindow(AppWindow w) => Activate(w);

        // ================= periodic work =================

        void OnTick()
        {
            tickCount++;
            UpdatePosition();

            Guid der = CheckDesktop();
            // Keep the bar on whichever desktop you're looking at (only moves OUR window, and only
            // when Windows has actually hidden it because it's on another desktop).
            if (Visible && IsHandleCreated)
            {
                Native.DwmGetWindowAttribute(Handle, Native.DWMWA_CLOAKED, out int cl, 4);
                barCloaked = cl != 0;
                if (barCloaked)
                {
                    var target = der != Guid.Empty ? der : lastDesktop;
                    if (target != Guid.Empty) desktops.MoveOwnWindow(Handle, target);
                }
            }

            if (dirty || tickCount % 3 == 0) { dirty = false; RefreshModel(); }
            if (preview.Visible && preview.HasDeadWindows()) preview.HidePopup();
        }

        // Which desktop is on screen: trust the foreground window first (always accurate),
        // the registry value only when it has just changed (it can be stale on some builds).
        // Runs on the timer, and right away when Windows says the desktop or the active window changed.
        // Returns the desktop worked out from the foreground window (or empty).
        Guid CheckDesktop()
        {
            if (desktops.List.Count == 0) desktops.Refresh();
            Guid reg = Desktops.ReadCurrentId();
            Guid der = desktops.DeriveCurrent(OwnPid);
            Guid cur = der != Guid.Empty ? der : (reg != lastReg && reg != Guid.Empty) ? reg : lastDesktop;
            if (cur == Guid.Empty) cur = reg;
            lastReg = reg;
            if (cur != lastDesktop)
            {
                // the settings window comes with you to the desktop you switch to
                if (settingsForm != null && !settingsForm.IsDisposed && settingsForm.Visible && settingsForm.WindowState != FormWindowState.Minimized && cur != Guid.Empty
                    && desktops.IsOnCurrent(settingsForm.Handle) == false)
                    desktops.MoveOwnWindow(settingsForm.Handle, cur);
                lastDesktop = cur;
                preview.HidePopup();
                dirty = true;
                var t = new Timer { Interval = 450 };
                t.Tick += (s, e) => { t.Dispose(); SampleColor(); };
                t.Start();
            }
            return der;
        }

        // A desktop switch seen by an event (not the timer): redraw the highlight now
        void DesktopMaybeChanged()
        {
            if (IsDisposed || !IsHandleCreated) return;
            var before = lastDesktop;
            CheckDesktop();
            if (lastDesktop != before) { dirty = false; RefreshModel(); Update(); }
        }

        bool SmallButtons => Theme.IsWin10 && lastLayout.Taskbar.Height > 0 && lastLayout.Taskbar.Height / Math.Max(0.5f, scale) < 36;

        void UpdatePosition()
        {
            var L = locator.Get();
            if (L.Tray != lastTray && L.Tray != IntPtr.Zero)
            {
                // Explorer restarted: re-register for shell notifications
                if (lastTray != IntPtr.Zero && IsHandleCreated) { Native.DeregisterShellHookWindow(Handle); Native.RegisterShellHookWindow(Handle); }
                lastTray = L.Tray;
                locator.Poke();
            }
            bool wasSmall = SmallButtons;
            lastLayout = L;
            if (wasSmall != SmallButtons) { dirty = true; signature = ""; }
            string reason = null;
            if (L.Tray == IntPtr.Zero) reason = "taskbar window not found";
            else if (L.Vertical) reason = "taskbar is on the side of the screen (not supported yet)";
            else if (!L.Valid) reason = "icon area too small";
            else
            {
                var mon = Native.GetMonitorRect(L.Tray, false);
                int visibleH = Math.Min(L.Taskbar.Bottom, mon.Bottom) - Math.Max(L.Taskbar.Top, mon.Top);
                if (visibleH < L.Taskbar.Height / 2) reason = "taskbar is auto-hidden";
                else if (cfg.HideWhenFullscreen && IsFullscreenApp(mon, out string fgInfo)) reason = "fullscreen app in front: " + fgInfo;
            }
            if (reason != null) { lastHideReason = reason; hideReasonCount++; badTicks++; } else badTicks = 0;

            // Only hide when the reason persists for ~0.6s, so brief glitches don't make the bar vanish
            if (reason != null && (badTicks >= 3 || L.Tray == IntPtr.Zero))
            {
                if (Visible) { Hide(); preview.HidePopup(); }
                shouldShow = false;
                return;
            }
            if (reason != null && !Visible) return;
            shouldShow = true;
            if (Math.Abs(L.Scale - scale) > 0.01f) { scale = L.Scale; labelFont?.Dispose(); labelFont = null; textFont?.Dispose(); textFont = null; dirty = true; }

            int x0 = L.IconAreaLeft + (int)(cfg.LeftOffset * scale);
            int x1 = L.IconAreaRight - (int)(cfg.RightOffset * scale);
            // Windows 11 has a 1px top border we leave visible; Windows 10 doesn't
            int y0 = Theme.IsWin10 ? L.Taskbar.Top : L.Taskbar.Top + Math.Max(1, (int)Math.Round(scale));
            var b = Rectangle.FromLTRB(x0, y0, Math.Max(x0 + 10, x1), L.Taskbar.Bottom);
            if (b != lastBounds || !Visible)
            {
                lastBounds = b;
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
                if (!Visible) Show();
                LayoutButtons();
                Invalidate();
            }
            else ReassertTopmost();
        }

        // If the real taskbar ended up in front of the bar (it happens after using Start), put the bar back on top
        int coveredCount;
        void KeepAboveTaskbar()
        {
            if (!Visible || !shouldShow || lastLayout.Tray == IntPtr.Zero) return;
            bool covered = false;
            IntPtr h = Handle; int steps = 0;
            while ((h = Native.GetWindow(h, 3 /*GW_HWNDPREV*/)) != IntPtr.Zero && steps++ < 4000)
                if (h == lastLayout.Tray) { covered = true; break; }
            if (!covered) { coveredCount = 0; return; }
            coveredCount++;
            // step 1: re-insert at the top of the "always on top" windows
            Native.SetWindowPos(Handle, (IntPtr)(-2) /*HWND_NOTOPMOST*/, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
            // step 2: if that didn't work, hide and show it again (which always puts it on top)
            if (coveredCount >= 3)
            {
                Native.ShowWindow(Handle, 0 /*SW_HIDE*/);
                Native.ShowWindow(Handle, 4 /*SW_SHOWNOACTIVATE*/);
                coveredCount = 0;
            }
        }

        void ReassertTopmost()
        {
            if (Visible && shouldShow)
                Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
        }

        bool IsFullscreenApp(Native.RECT mon, out string info)
        {
            info = null;
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == Handle || fg == preview.Handle) return false;
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (pid == OwnPid) return false;
            string cls = Native.GetClass(fg);
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd" || cls == "XamlExplorerHostIslandWindow"
                || cls == "Windows.UI.Core.CoreWindow" || cls == "ForegroundStaging" || cls == "MultitaskingViewFrame"
                || cls == "TopLevelWindowForOverflowXamlIsland" || cls.StartsWith("Windows.UI.Input", StringComparison.Ordinal)) return false;
            if (!Native.IsWindowVisible(fg)) return false;
            Native.DwmGetWindowAttribute(fg, Native.DWMWA_CLOAKED, out int cloaked, 4);
            if (cloaked != 0) return false;
            string exe = Native.GetProcessPath(pid) ?? "?";
            if (exe.EndsWith("\\explorer.exe", StringComparison.OrdinalIgnoreCase)) return false;
            if (!Native.GetWindowRect(fg, out var r)) return false;
            bool full = r.Left <= mon.Left && r.Top <= mon.Top && r.Right >= mon.Right && r.Bottom >= mon.Bottom;
            if (full) info = cls + " (" + Path.GetFileName(exe) + ")";
            return full;
        }

        void SampleColor()
        {
            if (!cfg.Color.Equals("sample", StringComparison.OrdinalIgnoreCase)) { theme = Theme.Build(cfg, null); Invalidate(); return; }
            var L = locator.Get();
            if (!L.Valid || !Visible) return;
            var c = Theme.Sample(L.Taskbar, lastBounds.Left, lastBounds.Right, L.Scale);
            if (c.HasValue && (!sampled.HasValue || ColorDist(c.Value, sampled.Value) > 3))
            {
                sampled = c;
                theme = Theme.Build(cfg, sampled);
                Invalidate();
            }
            else if (!sampled.HasValue) { theme = Theme.Build(cfg, null); Invalidate(); }
        }

        static int ColorDist(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);

        // ================= model =================

        void RefreshModel()
        {
            desktops.Refresh();
            if (lastDesktop != Guid.Empty) desktops.CurrentId = lastDesktop;
            var wins = tracker.Enumerate();
            lastWindows = wins;

            var deskList = desktops.List;
            if (deskList.Count == 0)
            {
                // Only one desktop has ever existed: make a single group
                var id = desktops.CurrentId;
                if (id == Guid.Empty) foreach (var w in wins) if (w.DesktopId != Guid.Empty) { id = w.DesktopId; break; }
                deskList = new List<DesktopInfo> { new DesktopInfo { Id = id, Index = 0, Name = "Desktop 1" } };
                if (desktops.CurrentId == Guid.Empty) desktops.CurrentId = id;
            }

            var newGroups = new List<GroupModel>();
            bool afterGap = true;  // no automatic space at the very start
            foreach (var sec in cfg.Layout)
            {
                int before = newGroups.Count;
                switch (sec.Type)
                {
                    case "gap":
                        newGroups.Add(new GroupModel { Kind = GroupKind.Gap, Section = sec });
                        afterGap = true;
                        continue;
                    case "pins" when sec.Style == "grid":
                    {
                        // a popup grid: one button that opens the grid above the bar
                        if (!sec.Items.Any(i => i.Type == "program" || i.Type == "web")) break;
                        var g = new GroupModel { Kind = GroupKind.Menu, Section = sec, Tooltip = sec.Name, BackColor = Theme.Parse(sec.BackColor) };
                        g.Buttons.Add(new BarButton { Key = "tray:" + sec.Id, Group = g, Tray = sec });
                        newGroups.Add(g);
                        break;
                    }
                    case "pins":
                    {
                        var g = new GroupModel { Kind = GroupKind.Pins, Section = sec, Tooltip = sec.Name, BackColor = Theme.Parse(sec.BackColor) };
                        foreach (var it in sec.Items.Where(i => i.Type == "program" || i.Type == "web"))
                            g.Buttons.Add(new BarButton { Key = "pin:" + sec.Id + ":" + sec.Items.IndexOf(it) + ":" + it.Target, Group = g, Pin = it });
                        if (g.Buttons.Count > 0) newGroups.Add(g);
                        break;
                    }
                    case "item":
                    {
                        // one app or link directly on the taskbar
                        var it = sec.Items.FirstOrDefault(i => i.Type == "program" || i.Type == "web");
                        if (it == null) break;
                        var g = new GroupModel { Kind = GroupKind.Pins, Section = sec, Tooltip = it.Name, BackColor = Theme.Parse(sec.BackColor) };
                        g.Buttons.Add(new BarButton { Key = "item:" + sec.Id, Group = g, Pin = it });
                        newGroups.Add(g);
                        break;
                    }
                    case "menu":
                    {
                        var g = new GroupModel { Kind = GroupKind.Menu, Section = sec, Tooltip = sec.Name, BackColor = Theme.Parse(sec.BackColor) };
                        g.Buttons.Add(new BarButton { Key = "menu:" + sec.Id, Group = g, Menu = sec });
                        newGroups.Add(g);
                        break;
                    }
                    case "windows":
                        AddWindowGroups(newGroups, sec, wins, deskList);
                        break;
                }
                // Space + divider between sections
                if (newGroups.Count > before)
                {
                    var first = newGroups[before];
                    first.GapBefore = afterGap ? 0 : cfg.SectionGap;
                    first.DividerBefore = !afterGap && cfg.ShowDividers;
                    afterGap = false;
                }
            }

            var fg = ForegroundRoot();
            var sb = new StringBuilder();
            sb.Append(settingsVersion).Append('|').Append(fg.ToInt64()).Append('|').Append(string.Join(",", flashing)).Append('|').Append(scale).Append('|');
            foreach (var g in newGroups)
            {
                sb.Append((int)g.Kind).Append(g.Desktop?.Id).Append(g.IsCurrent).Append(g.Label).Append(g.GapBefore).Append('[');
                foreach (var b in g.Buttons)
                {
                    sb.Append(b.Key).Append(':');
                    foreach (var w in b.Windows) sb.Append(w.Hwnd.ToInt64()).Append(w.Minimized ? "m" : "").Append(',');
                    sb.Append(';');
                }
                sb.Append(']');
            }
            string sig = sb.ToString();
            if (sig != signature)
            {
                bool deskChanged = desktops.CurrentId != scrolledFor;
                scrolledFor = desktops.CurrentId;
                signature = sig;
                groups = newGroups;
                RemapHover();
                LayoutButtons();
                if (deskChanged) ScrollToCurrent();
                Invalidate();
                if (preview.Visible && preview.Owner_Key is string key && key.StartsWith("win:"))
                {
                    if (!AllButtons().Any(x => "win:" + x.Key == key)) preview.HidePopup();
                }
            }
        }

        void AddWindowGroups(List<GroupModel> into, BarSection sec, List<AppWindow> wins, List<DesktopInfo> deskList)
        {
            Guid cur = desktops.CurrentId;
            var known = new HashSet<Guid>(deskList.Select(d => d.Id));

            if (!sec.GroupByDesktop)
            {
                var list = wins.Where(w => sec.FlatScope == "all" || w.DesktopId == Guid.Empty || w.DesktopId == cur || !known.Contains(w.DesktopId)).ToList();
                var g = new GroupModel { Kind = GroupKind.FlatWindows, Section = sec, Tooltip = sec.Name, BackColor = Theme.Parse(sec.BackColor) };
                FillButtons(g, list, sec.CombineWindows);
                into.Add(g);
                return;
            }

            var allDesk = new List<AppWindow>();
            var byDesk = deskList.ToDictionary(d => d.Id, d => new List<AppWindow>());
            foreach (var w in wins)
            {
                Guid id = w.DesktopId == Guid.Empty ? cur : w.DesktopId;
                if (id != Guid.Empty && byDesk.TryGetValue(id, out var list)) list.Add(w);
                else if (deskList.Count == 1) byDesk[deskList[0].Id].Add(w);
                else allDesk.Add(w);
            }
            bool first = true;
            if (allDesk.Count > 0)
            {
                var g = new GroupModel { Kind = GroupKind.AllDesktops, Section = sec, Label = sec.DesktopLabels == "none" ? null : "All", Tooltip = "Windows shown on all desktops", BackColor = Theme.Parse(sec.BackColor), LabelColor = Theme.Parse(sec.TextColor), LabelBack = Theme.Parse(sec.LabelBackColor) };
                FillButtons(g, allDesk, sec.CombineWindows);
                into.Add(g);
                first = false;
            }
            foreach (var d in deskList)
            {
                var list = byDesk[d.Id];
                var dpins = cfg.FindDesktop(d.Id)?.Pins;
                if (list.Count == 0 && (dpins == null || dpins.Count == 0) && !sec.ShowEmptyDesktops) continue;
                var g = new GroupModel
                {
                    Kind = GroupKind.Desktop, Section = sec, Desktop = d, IsCurrent = d.Id == cur,
                    Label = sec.DesktopLabels == "none" ? null : sec.DesktopLabels == "name" ? Trunc(d.Name, 16)
                          : sec.DesktopLabels == "numname" ? ((d.Index + 1) + " " + Trunc(d.Name.StartsWith("Desktop ") ? "" : d.Name, 16)).Trim()
                          : (d.Index + 1).ToString(),
                    Tooltip = d.Name + (d.Id == cur ? "  (current desktop)" : "  (click to switch)"),
                    BackColor = Theme.Parse(sec.BackColor), LabelColor = Theme.Parse(sec.TextColor), LabelBack = Theme.Parse(sec.LabelBackColor)
                };
                var ds = cfg.FindDesktop(d.Id);
                if (ds != null)
                {
                    if (!string.IsNullOrWhiteSpace(ds.Label)) g.Label = ds.Label;
                    else if (!string.IsNullOrWhiteSpace(ds.ImagePath)) g.Label = null;   // picture only
                    if (!string.IsNullOrWhiteSpace(ds.ImagePath)) g.LabelImage = Launcher.Expand(ds.ImagePath);
                    g.BackColor = Theme.Parse(ds.BackColor) ?? g.BackColor;
                    g.LabelColor = Theme.Parse(ds.TextColor) ?? g.LabelColor;
                    g.LabelBack = Theme.Parse(ds.LabelBackColor) ?? g.LabelBack;
                }
                if (!first) { g.GapBefore = sec.DesktopGap; g.DividerBefore = cfg.ShowDividers; }
                first = false;
                // Apps pinned to this desktop come first; a running window of that app joins its pin
                var rest = new List<AppWindow>(list);
                var pinButtons = new List<BarButton>();
                if (dpins != null)
                    for (int i = 0; i < dpins.Count; i++)
                    {
                        var p = dpins[i];
                        if (p.Type != "program" && p.Type != "web") continue;
                        var b = new BarButton { Key = "dpin:" + d.Id.ToString("N") + ":" + i + ":" + p.Target, Group = g, Pin = p, DeskPin = true };
                        foreach (var w in rest.Where(w => AppIdentity.Matches(p, w)).ToList()) { b.Windows.Add(w); rest.Remove(w); }
                        pinButtons.Add(b);
                    }
                // pinned and unpinned apps share one order on each desktop (drag them anywhere)
                FillButtonsRaw(g, rest, sec.CombineWindows);
                if (cfg.DesktopPinsFirst) g.Buttons.InsertRange(0, pinButtons);
                else
                {
                    // an open pinned app sits where its window would be; closed ones go in front, like pinned apps on the real taskbar
                    var at = new Dictionary<IntPtr, int>();
                    for (int i = 0; i < list.Count; i++) if (!at.ContainsKey(list[i].Hwnd)) at[list[i].Hwnd] = i;
                    int First(BarButton b) => b.Windows.Count == 0 ? -1 : b.Windows.Min(w => at.TryGetValue(w.Hwnd, out int v) ? v : int.MaxValue);
                    var all = g.Buttons.Concat(pinButtons).Select((b, i) => new { b, i, f = First(b) })
                                       .OrderBy(x => x.f).ThenBy(x => x.i).Select(x => x.b).ToList();
                    g.Buttons.Clear();
                    g.Buttons.AddRange(all);
                }
                ApplyOrder(g);
                into.Add(g);
            }
        }

        static string Trunc(string s, int n) => s.Length <= n ? s : s.Substring(0, n - 1) + "…";

        void FillButtons(GroupModel g, List<AppWindow> wins, bool combine)
        {
            FillButtonsRaw(g, wins, combine);
            ApplyOrder(g);
        }

        // The key a button is remembered by in your saved order
        public static string OrderKeyOf(BarButton b) =>
            b.DeskPin ? "dpin:" + (b.Pin.Target ?? "").ToLowerInvariant() + "|" + (b.Pin.Arguments ?? "").ToLowerInvariant()
                      : b.Windows.Count > 0 ? b.Windows[0].GroupKey : b.Key;

        // Put apps in the order you dragged them into on this desktop; others follow in opening order
        void ApplyOrder(GroupModel g)
        {
            string ok = g.Kind == GroupKind.Desktop ? g.Desktop.Id.ToString("N") : g.Kind == GroupKind.FlatWindows ? "flat" : "all";
            var saved = cfg.OrderFor(ok, false)?.Keys;
            if (saved == null || saved.Count == 0) return;
            var pos = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < saved.Count; i++) if (!pos.ContainsKey(saved[i])) pos[saved[i]] = i;
            int Pos(BarButton b)
            {
                if (pos.TryGetValue(OrderKeyOf(b), out int v)) return v;
                if (b.DeskPin && cfg.DesktopPinsFirst) return -1;   // newly pinned: to the front
                // just pinned to this desktop: keep the spot the open app already had
                if (b.DeskPin && b.Windows.Count > 0 && pos.TryGetValue(b.Windows[0].GroupKey, out v)) return v;
                return int.MaxValue;
            }
            var sorted = g.Buttons.Select((b, i) => new { b, i, p = Pos(b) })
                                  .OrderBy(x => x.p).ThenBy(x => x.i).Select(x => x.b).ToList();
            g.Buttons.Clear();
            g.Buttons.AddRange(sorted);
        }

        void FillButtonsRaw(GroupModel g, List<AppWindow> wins, bool combine)
        {
            string deskKey = g.Desktop != null ? g.Desktop.Id.ToString("N") : g.Kind.ToString();
            if (combine)
            {
                var order = new List<string>();
                var map = new Dictionary<string, BarButton>();
                foreach (var w in wins)
                {
                    if (!map.TryGetValue(w.GroupKey, out var b))
                    {
                        b = new BarButton { Key = deskKey + "|" + w.GroupKey, Group = g };
                        map[w.GroupKey] = b; order.Add(w.GroupKey);
                    }
                    b.Windows.Add(w);
                }
                foreach (var k in order) g.Buttons.Add(map[k]);
            }
            else
            {
                foreach (var w in wins)
                    g.Buttons.Add(new BarButton { Key = deskKey + "|" + w.Hwnd.ToInt64(), Group = g, Windows = { w } });
            }
        }

        IEnumerable<BarButton> AllButtons() => groups.SelectMany(g => g.Buttons);

        void RemapHover()
        {
            if (hover is BarButton hb) hover = AllButtons().FirstOrDefault(b => b.Key == hb.Key);
            else if (hover is GroupModel hg) hover = groups.FirstOrDefault(g => g.Kind == hg.Kind && g.Desktop?.Id == hg.Desktop?.Id);
            if (dragBtn == null) pressed = null;
        }

        IntPtr ForegroundRoot()
        {
            IntPtr fg = Native.GetForegroundWindow();
            Native.GetWindowThreadProcessId(fg, out uint pid);
            if (fg != IntPtr.Zero && fg == WindowTracker.OwnSettingsWindow) return fg;   // the settings window is in use
            if (fg == IntPtr.Zero || pid == OwnPid) return lastForeign;
            IntPtr root = GetAncestor(fg, 3);
            return root == IntPtr.Zero ? fg : root;
        }

        // ================= layout & painting =================

        int S(float v) => (int)Math.Round(v * scale);

        Font LabelFont => labelFont ?? (labelFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel));
        Font TextFont => textFont ?? (textFont = new Font("Segoe UI", 12f * scale, FontStyle.Regular, GraphicsUnit.Pixel));

        int IconPx => S(cfg.EffectiveIconSize(SmallButtons));

        string MenuText(BarSection m) => m.ShowLabel ? (string.IsNullOrWhiteSpace(m.Label) ? m.Name : m.Label) : null;

        // Text shown on a menu button or single-item button (null = icon only)
        string ButtonText(BarButton b)
        {
            if (b.Menu != null) return MenuText(b.Menu);
            if (b.Tray != null) return MenuText(b.Tray);
            var sec = b.Group?.Section;
            if (b.Pin != null && sec != null && sec.Type == "item" && sec.ShowLabel)
                return string.IsNullOrWhiteSpace(sec.Label) ? (string.IsNullOrWhiteSpace(b.Pin.Name) ? null : b.Pin.Name) : sec.Label;
            return null;
        }

        bool ButtonShowsIcon(BarButton b)
        {
            var sec = b.Menu ?? b.Tray ?? (b.Pin != null && b.Group?.Section?.Type == "item" ? b.Group.Section : null);
            return sec == null || sec.ShowIcon || ButtonText(b) == null;
        }

        void LayoutButtons()
        {
            if (!IsHandleCreated) return;
            int H = ClientSize.Height;
            int btnW = S(cfg.EffectiveButtonWidth(SmallButtons));
            int minBtn = IconPx + S(10);

            for (int pass = 0; pass < 2; pass++)
            {
                int x = S(2);
                using (var g = CreateGraphics())
                {
                    foreach (var grp in groups)
                    {
                        x += S(grp.GapBefore);
                        int start = x;
                        if (grp.Kind == GroupKind.Gap)
                        {
                            x += S(grp.Section.Width);
                            grp.LabelRect = Rectangle.Empty;
                            grp.Bounds = Rectangle.FromLTRB(start, 0, x, H);
                            continue;
                        }
                        int labelW = 0;
                        if (grp.Label != null || grp.LabelImage != null)
                        {
                            int tw = grp.Label != null ? TextRenderer.MeasureText(g, grp.Label, LabelFont, Size.Empty, TextFormatFlags.NoPadding).Width : 0;
                            int iw = grp.LabelImage != null ? IconPx + (grp.Label != null ? S(6) : 0) : 0;
                            labelW = Math.Max(S(18), tw + iw + S(10));
                        }
                        else if (grp.Kind == GroupKind.Desktop && grp.Buttons.Count == 0) labelW = S(16);
                        grp.LabelRect = new Rectangle(x, 0, labelW, H);
                        x += labelW;
                        if (IsGrid(grp))
                        {
                            // Grid of small icons, filled row by row, like the notification area
                            var sec = grp.Section;
                            int rows = Math.Max(1, Math.Min(6, sec.GridRows));
                            int cell = Math.Max(S(10), (H - S(4)) / rows);
                            int icon = sec.GridIconSize > 0 ? Math.Min(S(sec.GridIconSize), cell - S(2)) : Math.Max(S(8), cell - S(6));
                            int cols = Math.Max(1, (grp.Buttons.Count + rows - 1) / rows);
                            int top = (H - rows * cell) / 2;
                            x += S(3);
                            for (int i = 0; i < grp.Buttons.Count; i++)
                            {
                                var b = grp.Buttons[i];
                                b.GridIcon = icon;
                                b.Rect = new Rectangle(x + (i % cols) * cell, top + (i / cols) * cell, cell, cell);
                            }
                            x += cols * cell + S(3);
                            grp.Bounds = Rectangle.FromLTRB(start, 0, x, H);
                            continue;
                        }
                        foreach (var b in grp.Buttons)
                        {
                            int w = btnW;
                            string t = ButtonText(b);
                            if (t != null)
                                w = S(12) + (ButtonShowsIcon(b) ? IconPx + S(8) : 0) + TextRenderer.MeasureText(g, t, TextFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(14);
                            b.Rect = new Rectangle(x, 0, w, H);
                            x += w;
                        }
                        grp.Bounds = Rectangle.FromLTRB(start, 0, x, H);
                    }
                }
                contentWidth = x + S(2);
                if (contentWidth <= ClientSize.Width || pass == 1 || btnW <= minBtn) break;
                // Too many buttons: squeeze them (like the real taskbar does) before scrolling
                int buttons = groups.Where(gr => !IsGrid(gr)).Sum(gr => gr.Buttons.Count(b => b.Menu == null));
                if (buttons == 0) break;
                int over = contentWidth - ClientSize.Width;
                btnW = Math.Max(minBtn, btnW - (over + buttons - 1) / buttons);
            }
            scrollX = Math.Max(0, Math.Min(scrollX, contentWidth - ClientSize.Width));
        }

        void ScrollToCurrent()
        {
            if (contentWidth <= ClientSize.Width) { scrollX = 0; return; }
            var cur = groups.FirstOrDefault(g => g.IsCurrent);
            if (cur == null) return;
            if (cur.Bounds.Left < scrollX || cur.Bounds.Right > scrollX + ClientSize.Width)
                scrollX = Math.Max(0, Math.Min(cur.Bounds.Left - S(20), contentWidth - ClientSize.Width));
        }

        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            if (rad <= 0) { p.AddRectangle(r); return p; }
            int d = Math.Max(1, Math.Min(rad * 2, Math.Min(r.Width, r.Height)));
            p.AddArc(r.Left, r.Top, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Top, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Fill(Graphics g, Color c, Rectangle r, int rad)
        {
            if (c.A == 0) return;
            if (rad <= 0) { using (var b = new SolidBrush(c)) g.FillRectangle(b, r); return; }
            using (var b = new SolidBrush(c)) using (var p = RoundRect(r, rad)) g.FillPath(b, p);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(theme.Background);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            g.TranslateTransform(-scrollX, 0);

            int H = ClientSize.Height;
            bool w10 = Theme.IsWin10;
            int visH = w10 ? H : Math.Min(H - S(4), S(40));
            int visY = w10 ? 0 : (H - visH) / 2;
            int rad = w10 ? 0 : S(4);
            int iconPx = IconPx;
            IntPtr fg = ForegroundRoot();

            GroupModel prev = null;
            foreach (var grp in groups)
            {
                if (prev != null && grp.DividerBefore)
                {
                    int dx = (prev.Bounds.Right + grp.Bounds.Left) / 2;
                    DrawDivider(g, dx, H);
                }
                prev = grp;

                if (grp.Kind == GroupKind.Gap)
                {
                    if (grp.Section.Divider) DrawDivider(g, grp.Bounds.Left + grp.Bounds.Width / 2, H);
                    continue;
                }

                if (grp.BackColor.HasValue)
                    Fill(g, grp.BackColor.Value, new Rectangle(grp.Bounds.X, visY, grp.Bounds.Width, visH), w10 ? 0 : S(6));
                if (cfg.HighlightCurrentDesktop && grp.IsCurrent)
                    Fill(g, Theme.Parse(cfg.HighlightColor) ?? Shade(grp.BackColor, theme.GroupHighlight, 50, 38), new Rectangle(grp.Bounds.X, visY, grp.Bounds.Width, visH), w10 ? 0 : S(6));

                if (grp.LabelRect.Width > 0)
                {
                    var lr = w10 ? new Rectangle(grp.LabelRect.X, visY, grp.LabelRect.Width, visH)
                                 : new Rectangle(grp.LabelRect.X + S(1), visY, grp.LabelRect.Width - S(2), visH);
                    if (grp.LabelBack.HasValue) Fill(g, grp.LabelBack.Value, lr, rad);
                    var under = grp.LabelBack ?? grp.BackColor;
                    if (ReferenceEquals(hover, grp) && grp.Kind == GroupKind.Desktop) Fill(g, ReferenceEquals(pressed, grp) ? Shade(under, theme.Pressed, 40, 34) : Shade(under, theme.Hover, 55, 42), lr, rad);
                    if (grp.Label != null || grp.LabelImage != null)
                    {
                        var col = grp.LabelColor ?? (grp.IsCurrent ? theme.Text : theme.TextDim);
                        if (grp.LabelColor.HasValue && !grp.IsCurrent) col = Color.FromArgb(Math.Min(col.A, (byte)190), col);
                        var tr = new Rectangle(lr.X, lr.Y, lr.Width, lr.Height);
                        if (grp.LabelImage != null)
                        {
                            var img = Launcher.FromIconFileCached(grp.LabelImage, iconPx);
                            int x0 = grp.Label != null ? lr.X + S(5) : lr.X + (lr.Width - iconPx) / 2;
                            if (img != null) g.DrawImage(img, new Rectangle(x0, visY + (visH - iconPx) / 2, iconPx, iconPx));
                            tr = Rectangle.FromLTRB(x0 + iconPx + S(4), lr.Y, lr.Right, lr.Bottom);
                        }
                        if (grp.Label != null)
                            TextRenderer.DrawText(g, grp.Label, LabelFont, new Rectangle(tr.X - scrollX, tr.Y, tr.Width, tr.Height), col,
                                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                    }
                    else
                    {
                        int d = S(4);
                        using (var br = new SolidBrush(theme.TextDim)) g.FillEllipse(br, lr.X + lr.Width / 2 - d / 2, visY + visH / 2 - d / 2, d, d);
                    }
                    if (grp.IsCurrent && grp.Kind == GroupKind.Desktop)
                    {
                        int w = w10 ? lr.Width - S(4) : S(8), h = Math.Max(2, S(2));
                        var ur = new Rectangle(lr.X + lr.Width / 2 - w / 2, w10 ? visY + visH - h : visY + visH - h - S(3), w, h);
                        Fill(g, theme.ActiveIndicator, ur, w10 ? 0 : h);
                    }
                }

                foreach (var b in grp.Buttons) DrawButton(g, b, fg, visY, visH, iconPx, rad);
            }

            g.ResetTransform();
            PaintDrag(g);
            if (contentWidth > ClientSize.Width)
            {
                int fw = S(18);
                if (scrollX > 0)
                    using (var br = new LinearGradientBrush(new Rectangle(0, 0, fw, H), theme.Background, Color.FromArgb(0, theme.Background), 0f))
                        g.FillRectangle(br, 0, 0, fw, H);
                if (scrollX < contentWidth - ClientSize.Width)
                    using (var br = new LinearGradientBrush(new Rectangle(ClientSize.Width - fw, 0, fw, H), Color.FromArgb(0, theme.Background), theme.Background, 0f))
                        g.FillRectangle(br, ClientSize.Width - fw, 0, fw, H);
            }
        }

        void DrawDivider(Graphics g, int x, int H)
        {
            using (var pen = new Pen(theme.Divider, Math.Max(1f, scale)))
                g.DrawLine(pen, x, H * 0.28f, x, H * 0.72f);
        }

        Bitmap IconOf(BarButton b, int px)
        {
            if (b.Pin != null) return Launcher.IconFor(b.Pin, px, b.Group.Section?.Browser);
            if (b.Tray != null)
            {
                if (!string.IsNullOrWhiteSpace(b.Tray.IconPath)) { var c = Launcher.FromIconFileCached(Launcher.Expand(b.Tray.IconPath), px); if (c != null) return c; }
                return ChevronIcon(px);
            }
            if (b.Menu != null)
            {
                var m = b.Menu;
                if (!string.IsNullOrWhiteSpace(m.IconPath)) { var c = Launcher.FromIconFileCached(Launcher.Expand(m.IconPath), px); if (c != null) return c; }
                return ChevronIcon(px);   // same ^ arrow as a popup grid
            }
            return Icons.ForWindow(b.Windows[0], px);
        }

        public static bool IsGrid(GroupModel g) => false;   // grids now open as a popup (TrayFlyout)

        // The ^ arrow used for popup-grid buttons, like the notification area's
        readonly Dictionary<int, Bitmap> chevrons = new Dictionary<int, Bitmap>();
        Bitmap ChevronIcon(int px)
        {
            int key = px * 1000 + theme.Text.ToArgb() % 997;
            if (chevrons.TryGetValue(key, out var b)) return b;
            b = new Bitmap(px, px);
            using (var g = Graphics.FromImage(b))
            using (var pen = new Pen(theme.Text, Math.Max(1.5f, px / 12f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                float cx = px / 2f, cy = px / 2f, w = px * 0.28f, h = px * 0.16f;
                g.DrawLines(pen, new[] { new PointF(cx - w, cy + h), new PointF(cx, cy - h), new PointF(cx + w, cy + h) });
            }
            chevrons[key] = b;
            return b;
        }

        void DrawGridButton(Graphics g, BarButton b)
        {
            var r = b.Rect;
            var vis = Rectangle.Inflate(r, -Math.Max(1, S(1)), -Math.Max(1, S(1)));
            int rad = Theme.IsWin10 ? 0 : S(3);
            var tint = Theme.Parse(b.Pin?.BackColor);
            if (tint.HasValue) Fill(g, tint.Value, vis, rad);
            bool dragging = dragBtn != null && dragMoved && b.Key == dragBtn.Key;
            if (ReferenceEquals(pressed, b) && !dragMoved) Fill(g, theme.Pressed, vis, rad);
            else if (ReferenceEquals(hover, b) && !dragMoved) Fill(g, theme.Hover, vis, rad);
            int px = b.GridIcon;
            var icon = IconOf(b, px);
            var ir = new Rectangle(r.X + (r.Width - px) / 2, r.Y + (r.Height - px) / 2, px, px);
            if (dragging)
            {
                var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f };
                using (var ia = new System.Drawing.Imaging.ImageAttributes()) { ia.SetColorMatrix(cm); g.DrawImage(icon, ir, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia); }
            }
            else g.DrawImage(icon, ir);
        }

        // ---- notification badges ----
        void StartBadges()
        {
            Badges.CountNotifications = cfg.CountNotificationBadges;
            Badges.OutlookEnabled = cfg.OutlookUnread;
            Action repaint = () => { try { if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Invalidate); } catch { } };
            if (cfg.ShowBadges) Badges.Start(repaint); else Badges.Stop(repaint);
        }

        string BadgeFor(BarButton b)
        {
            if (!cfg.ShowBadges) return null;
            // classic Outlook: its real unread count
            bool outlook = b.Windows.Any(w => Badges.IsOutlook(w.ExePath) || Badges.IsOutlook(w.Aumid));
            if (!outlook && b.Pin != null) { var id = AppIdentity.IdOf(b.Pin); outlook = Badges.IsOutlook(id.exe) || Badges.IsOutlook(id.aumid); }
            if (outlook && cfg.OutlookUnread) { var o = Badges.Outlook(); if (o != null) return o; }
            foreach (var w in b.Windows) { var s = Badges.For(w.Aumid); if (s != null) return s; }
            if (b.Pin != null) return Badges.For(AppIdentity.IdOf(b.Pin).aumid);
            return null;
        }

        // A small pill with the count (or a dot) on the icon's bottom-right corner, like Windows does
        void DrawBadge(Graphics g, Rectangle ir, string text)
        {
            var color = Theme.Parse(cfg.BadgeColor) ?? Color.FromArgb(196, 43, 28);
            int h = Math.Max(S(12), ir.Height * 7 / 12);
            using (var f = new Font("Segoe UI Semibold", h * 0.62f, GraphicsUnit.Pixel))
            {
                bool dot = text == "•";
                int tw = dot ? 0 : TextRenderer.MeasureText(g, text, f, Size.Empty, TextFormatFlags.NoPadding).Width;
                int w = dot ? h * 2 / 3 : Math.Max(h, tw + S(6));
                int bh = dot ? w : h;
                var rc = new Rectangle(ir.Right - w + S(4), ir.Bottom - bh + S(3), w, bh);
                var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var ring = new SolidBrush(theme.Background)) using (var p = RoundRect(Rectangle.Inflate(rc, S(1), S(1)), (bh + S(2)) / 2)) g.FillPath(ring, p);
                using (var br = new SolidBrush(color)) using (var p = RoundRect(rc, bh / 2)) g.FillPath(br, p);
                g.SmoothingMode = sm;
                if (!dot)
                    TextRenderer.DrawText(g, text, f, rc, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }
        }

        void DrawButton(Graphics g, BarButton b, IntPtr fg, int visY, int visH, int iconPx, int rad)
        {
            if (b.GridIcon > 0 && IsGrid(b.Group)) { DrawGridButton(g, b); return; }
            bool w10 = Theme.IsWin10;
            int H = ClientSize.Height;
            var r = b.Rect;
            var vis = w10 ? r : new Rectangle(r.X + S(2), visY, r.Width - S(4), visH);
            bool active = b.Windows.Any(w => w.Hwnd == fg);
            bool flash = !active && b.Windows.Any(w => flashing.Contains(w.Hwnd));
            bool hov = ReferenceEquals(hover, b) && !dragMoved, prs = ReferenceEquals(pressed, b) && !dragMoved;
            bool dragging = dragBtn != null && dragMoved && b.Key == dragBtn.Key;

            // Your own background color for this app / pinned item, or the group's
            Color? tint = b.Pin != null ? Theme.Parse(b.Pin.BackColor) : b.Windows.Count > 0 ? Theme.Parse(cfg.FindApp(b.Windows[0].GroupKey)?.BackColor) : null;
            Color? under = tint ?? b.Group?.BackColor;
            Color bg = Color.Transparent;
            if (flash) bg = Color.FromArgb(theme.Dark ? 100 : 120, theme.Flash);
            else if (prs || dragging) bg = Shade(under, theme.Pressed, 40, 34);
            else if (active && hov) bg = Shade(under, Color.FromArgb(Math.Min(255, theme.ActiveBg.A + 14), theme.ActiveBg), 70, 55);
            else if (hov) bg = Shade(under, theme.Hover, 55, 42);
            else if (active) bg = Shade(under, theme.ActiveBg, 45, 36);
            if (tint.HasValue) { var sm0 = g.SmoothingMode; if (w10) g.SmoothingMode = SmoothingMode.None; Fill(g, tint.Value, vis, rad); g.SmoothingMode = sm0; }
            if (bg.A > 0)
            {
                var sm = g.SmoothingMode;
                if (w10) g.SmoothingMode = SmoothingMode.None;
                Fill(g, bg, vis, rad);
                g.SmoothingMode = sm;
            }

            // Several windows (Windows 10 shows a "stacked" right edge)
            if (w10 && b.Windows.Count > 1 && (active || hov))
                using (var br = new SolidBrush(Color.FromArgb(theme.Dark ? 60 : 50, theme.Dark ? Color.Black : Color.White)))
                    g.FillRectangle(br, r.Right - S(3), r.Top, Math.Max(1, S(1)), H);

            string text = ButtonText(b);
            bool showIcon = ButtonShowsIcon(b);
            int ix = text != null ? r.X + S(12) : r.X + (r.Width - iconPx) / 2;
            int iy = w10 ? (H - iconPx) / 2 : visY + (visH - iconPx) / 2 - S(1);
            var ir = showIcon ? new Rectangle(ix, iy, iconPx, iconPx) : new Rectangle(r.X + S(4), iy, 0, iconPx);
            if (prs && showIcon) ir.Inflate(-S(1), -S(1));
            if (showIcon)
            {
                Bitmap icon = IconOf(b, iconPx);
                if (dragging)
                {
                    var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.35f };
                    using (var ia = new System.Drawing.Imaging.ImageAttributes()) { ia.SetColorMatrix(cm); g.DrawImage(icon, ir, 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia); }
                }
                else g.DrawImage(icon, ir);
                var badge = dragging ? null : BadgeFor(b);
                if (badge != null) DrawBadge(g, ir, badge);
            }

            if (text != null)
            {
                var tr = Rectangle.FromLTRB(ir.Right + S(8), 0, r.Right - S(6), H);
                tr.Offset(-scrollX, 0);
                TextRenderer.DrawText(g, text, TextFont, tr, Theme.Parse((b.Menu ?? b.Tray ?? b.Group.Section)?.TextColor) ?? theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            if (b.Menu != null)
            {
                // tiny caret: this button opens a list
                int cx = text != null ? r.Right - S(8) : ir.Right + S(2), cy = iy + S(2);
                using (var br = new SolidBrush(theme.TextDim))
                    g.FillPolygon(br, new[] { new Point(cx - S(3), cy + S(2)), new Point(cx + S(3), cy + S(2)), new Point(cx, cy - S(1)) });
            }

            if (b.Windows.Count == 0) return;
            if (w10)
            {
                var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                int h = Math.Max(2, S(2));
                var line = new Rectangle(r.X + S(1), H - h, r.Width - S(2), h);
                using (var br = new SolidBrush(flash ? theme.Flash : active ? theme.ActiveIndicator : theme.Indicator))
                {
                    if (b.Windows.Count > 1)
                    {
                        int gapX = line.Right - S(5);
                        g.FillRectangle(br, line.X, line.Y, gapX - line.X, h);
                        g.FillRectangle(br, gapX + S(2), line.Y, line.Right - gapX - S(2), h);
                    }
                    else g.FillRectangle(br, line);
                }
                g.SmoothingMode = sm;
            }
            else
            {
                int w = active ? S(16) : S(6), h = Math.Max(2, S(3));
                var pill = new Rectangle(r.X + (r.Width - w) / 2, visY + visH - h - S(1), w, h);
                Fill(g, flash ? theme.Flash : active ? theme.ActiveIndicator : theme.Indicator, pill, h);
                if (b.Windows.Count > 1 && !active)
                    Fill(g, theme.Indicator, new Rectangle(pill.Right + S(2), pill.Y, h, h), h);
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop(); colorTimer.Stop();
            if (saveTimer.Enabled) { saveTimer.Stop(); cfg.Save(); }
            locator.Stop();
            if (winEventHook != IntPtr.Zero) Native.UnhookWinEvent(winEventHook);
            if (IsHandleCreated) Native.DeregisterShellHookWindow(Handle);
            preview.Dispose();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); }
            base.OnFormClosed(e);
        }
    }
}
