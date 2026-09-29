using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace CLTaskbar
{
    internal partial class BarForm
    {
        // ---- dragging buttons on the bar ----
        BarButton dragBtn;           // button being dragged
        Point dragStart, dragPos;
        bool dragMoved;
        GroupModel dropGroup;        // where it would land
        int dropIndex;
        SettingsForm settingsForm;

        // ================= mouse =================

        object HitTest(Point client)
        {
            var p = new Point(client.X + scrollX, client.Y);
            foreach (var grp in groups)
            {
                foreach (var b in grp.Buttons) if (b.Rect.Contains(p)) return b;
                if (grp.LabelRect.Contains(p)) return grp;
            }
            return null;
        }

        Rectangle ScreenRectOf(object o)
        {
            Rectangle r = o is BarButton b ? b.Rect : o is GroupModel g ? g.LabelRect : Rectangle.Empty;
            r.Offset(-scrollX, 0);
            return RectangleToScreen(r);
        }

        bool dragSection;            // moving a whole group / button (not one icon)
        int sectionDropIndex = -1;

        static bool CanDrag(BarButton b) =>
            (b.Pin != null && (b.DeskPin || b.Group.Section?.Type == "pins")) || b.Windows.Count > 0
            || b.Menu != null || b.Tray != null || b.Group.Section?.Type == "item";

        // Menu buttons and single items always move as a whole; Shift+drag moves any button's whole group
        static bool DragsWholeSection(BarButton b, bool shift) =>
            b.Menu != null || b.Tray != null || (b.Group.Section?.Type == "item" && !b.DeskPin) || (shift && b.Group.Section != null);

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragBtn != null && e.Button == MouseButtons.Left)
            {
                if (!dragMoved && Math.Abs(e.X - dragStart.X) < S(6)) return;
                if (!dragMoved) ClosePopups();   // pressing a menu button opened it; dragging the button closes it again
                dragMoved = true;
                preview.HidePopup();
                previewTimer.Stop();
                dragPos = e.Location;
                if (dragSection) FindSectionDrop(e.Location); else FindDrop(e.Location);
                Cursor = (dragSection ? sectionDropIndex >= 0 : dropGroup != null) ? Cursors.Hand : Cursors.No;
                Invalidate();
                return;
            }
            var h = HitTest(e.Location);
            if (!ReferenceEquals(h, hover))
            {
                hover = h;
                Invalidate();
                previewTimer.Stop();
                if (h == null) return;
                if (preview.Visible && e.Button == MouseButtons.None) ShowPreviewFor(h);
                else if (e.Button == MouseButtons.None)
                {
                    previewTimer.Interval = Math.Max(1, cfg.PreviewDelayMs);
                    previewTimer.Start();
                }
            }
        }

        // Where a whole section would go: between sections, by their position on the bar
        void FindSectionDrop(Point client)
        {
            int x = client.X + scrollX;
            sectionDropIndex = -1;
            var src = dragBtn.Group.Section;
            var spans = new List<(BarSection sec, int left, int right)>();
            foreach (var sec in cfg.Layout)
            {
                var gs = groups.Where(g => g.Section == sec).ToList();
                if (gs.Count == 0) continue;
                spans.Add((sec, gs.Min(g => g.Bounds.Left), gs.Max(g => g.Bounds.Right)));
            }
            if (spans.Count == 0) return;
            int idx = spans.Count;
            for (int i = 0; i < spans.Count; i++)
                if (x < (spans[i].left + spans[i].right) / 2) { idx = i; break; }
            // index into cfg.Layout
            sectionDropIndex = idx < spans.Count ? cfg.Layout.IndexOf(spans[idx].sec) : cfg.Layout.Count;
            sectionMarkX = idx < spans.Count ? spans[idx].left - S(2) : spans[spans.Count - 1].right + S(2);
            if (sectionDropIndex == cfg.Layout.IndexOf(src) || sectionDropIndex == cfg.Layout.IndexOf(src) + 1) { /* no move, still show */ }
        }
        int sectionMarkX;

        // Works out which group and position the dragged button would land in
        void FindDrop(Point client)
        {
            dropGroup = null;
            int x = client.X + scrollX;
            GroupModel best = null; int bestDist = int.MaxValue;
            foreach (var g in groups)
            {
                if (!Accepts(g, dragBtn)) continue;
                int d = x < g.Bounds.Left ? g.Bounds.Left - x : x > g.Bounds.Right ? x - g.Bounds.Right : 0;
                if (d < bestDist) { bestDist = d; best = g; }
            }
            if (best == null || bestDist > S(40)) return;
            var others = best.Buttons.Where(b => b.Key != dragBtn.Key).ToList();
            int idx = 0;
            if (IsGrid(best))
            {
                // grids read left-to-right, top-to-bottom
                int y = client.Y;
                idx = others.Count;
                for (int i = 0; i < others.Count; i++)
                {
                    var r = others[i].Rect;
                    if (y < r.Top || (y < r.Bottom && x < r.Left + r.Width / 2)) { idx = i; break; }
                }
            }
            else foreach (var b in others) if (b.Rect.Left + b.Rect.Width / 2 < x) idx++;
            dropGroup = best;
            dropIndex = idx;
        }

        bool Accepts(GroupModel g, BarButton b)
        {
            if (b.DeskPin) return g.Kind == GroupKind.Desktop;
            if (b.Pin != null) return g.Kind == GroupKind.Pins && g.Section?.Type == "pins";
            if (g.Kind == GroupKind.Pins) return g.Section?.Type == "pins";                 // drop an open app on a group to pin it
            if (g.Kind == GroupKind.Desktop) return b.Group.Kind == GroupKind.Desktop;
            if (g.Kind == GroupKind.FlatWindows || g.Kind == GroupKind.AllDesktops) return b.Group.Kind == g.Kind;
            return false;
        }

        // Draws where the dragged thing will land, and its icon under the cursor
        void PaintDrag(Graphics g)
        {
            if (dragBtn == null || !dragMoved) return;
            int markX = int.MinValue, markTop = S(6), markBottom = ClientSize.Height - S(6);
            if (dragSection) { if (sectionDropIndex >= 0) markX = sectionMarkX - scrollX; }
            else if (dropGroup != null)
            {
                var others = dropGroup.Buttons.Where(b => b.Key != dragBtn.Key).ToList();
                markX = (others.Count == 0 ? dropGroup.LabelRect.Right + S(2)
                      : dropIndex < others.Count ? others[dropIndex].Rect.Left : others[others.Count - 1].Rect.Right) - scrollX;
                if (IsGrid(dropGroup) && others.Count > 0)
                {
                    var rr = dropIndex < others.Count ? others[dropIndex].Rect : others[others.Count - 1].Rect;
                    markTop = rr.Top + S(1); markBottom = rr.Bottom - S(1);
                }
            }
            if (markX != int.MinValue)
                using (var br = new SolidBrush(theme.ActiveIndicator))
                    g.FillRectangle(br, markX - Math.Max(1, S(1)), markTop, Math.Max(2, S(2)), markBottom - markTop);
            int px = IconPx;
            var icon = IconOf(dragBtn, px);
            var cm = new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.75f };
            using (var ia = new System.Drawing.Imaging.ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(icon, new Rectangle(dragPos.X - px / 2, (ClientSize.Height - px) / 2, px, px), 0, 0, icon.Width, icon.Height, GraphicsUnit.Pixel, ia);
            }
        }

        static string OrderKey(GroupModel g) =>
            g.Kind == GroupKind.Desktop ? g.Desktop.Id.ToString("N") : g.Kind == GroupKind.FlatWindows ? "flat" : "all";

        void DropSection(BarSection sec, int index)
        {
            int from = cfg.Layout.IndexOf(sec);
            if (from < 0 || index < 0) return;
            cfg.Layout.RemoveAt(from);
            if (index > from) index--;
            cfg.Layout.Insert(Math.Max(0, Math.Min(cfg.Layout.Count, index)), sec);
            ApplySettings();
            settingsForm?.ReloadTree();
        }

        bool MoveWindowsTo(BarButton db, GroupModel g)
        {
            foreach (var w in db.Windows)
                if (!DesktopMover.Move(w.Hwnd, g.Desktop.Index, desktops.List.Count, out string err))
                {
                    ShowBarMessage("Couldn't move \"" + Icons.FriendlyName(w) + "\" to " + g.Desktop.Name + ". " + err);
                    return false;
                }
            return true;
        }

        void Drop(BarButton db, GroupModel g, int idx)
        {
            var others = g.Buttons.Where(b => b.Key != db.Key).ToList();
            bool sameGroup = db.Group.Kind == g.Kind && (g.Kind != GroupKind.Desktop || OrderKey(db.Group) == OrderKey(g)) && (g.Kind != GroupKind.Pins || db.Group.Section == g.Section);

            if (db.DeskPin)
            {
                // A desktop pin: reorder, or move it (and its open windows) to another desktop
                if (!sameGroup && db.Windows.Count > 0 && !MoveWindowsTo(db, g)) return;
                var srcList = cfg.DesktopFor(db.Group.Desktop.Id).Pins;
                var dstList = cfg.DesktopFor(g.Desktop.Id).Pins;
                var before = others.Skip(idx).FirstOrDefault(b => b.DeskPin)?.Pin;
                srcList.Remove(db.Pin);
                int at = before != null ? dstList.IndexOf(before) : dstList.Count;
                dstList.Insert(at < 0 ? dstList.Count : at, db.Pin);
                SaveOrder(g, others, idx, db);
                ApplySettings();
                settingsForm?.ReloadTree();
                return;
            }

            if (db.Pin != null)
            {
                // Move a pinned item within or between groups
                var before = idx < others.Count ? others[idx].Pin : null;
                db.Group.Section.Items.Remove(db.Pin);
                var list = g.Section.Items;
                int at = before != null ? list.IndexOf(before) : list.Count;
                list.Insert(at < 0 ? list.Count : at, db.Pin);
                ApplySettings();
                settingsForm?.ReloadTree();
                return;
            }

            var w0 = db.Windows[0];
            if (g.Kind == GroupKind.Pins)
            {
                // Pin an open app by dropping it on a group
                var item = PinItemFor(w0);
                if (item == null) return;
                var before = idx < others.Count ? others[idx].Pin : null;
                int at = before != null ? g.Section.Items.IndexOf(before) : g.Section.Items.Count;
                g.Section.Items.Insert(at < 0 ? g.Section.Items.Count : at, item);
                ApplySettings();
                settingsForm?.ReloadTree();
                return;
            }

            // Different desktop: move the window(s) there first
            if (g.Kind == GroupKind.Desktop && !sameGroup && !MoveWindowsTo(db, g)) return;

            // Remember the order you chose on that desktop (apps you haven't placed go at the end)
            SaveOrder(g, others, idx, db);
            if (!sameGroup && db.Group.Kind == GroupKind.Desktop)
            {
                var src = cfg.OrderFor(OrderKey(db.Group), false);
                if (src != null && db.Group.Buttons.Count(b => b.Windows.Count > 0 && b.Windows[0].GroupKey == w0.GroupKey) <= 1)
                    src.Keys.RemoveAll(k => string.Equals(k, w0.GroupKey, StringComparison.OrdinalIgnoreCase));
            }
            ApplySettings();
        }

        // Remember the order of everything in the group (pinned and open apps together), with the dropped one at idx
        void SaveOrder(GroupModel g, List<BarButton> others, int idx, BarButton dropped)
        {
            var visible = others.Where(b => b.DeskPin || b.Windows.Count > 0).Select(OrderKeyOf).ToList();
            int at = Math.Min(Math.Max(0, others.Take(idx).Count(b => b.DeskPin || b.Windows.Count > 0)), visible.Count);
            visible.Insert(at, OrderKeyOf(dropped));
            visible = visible.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var order = cfg.OrderFor(OrderKey(g), true);
            var hidden = order.Keys.Where(k => !visible.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            order.Keys = visible.Concat(hidden).ToList();
        }

        public const int DefaultMenuWidth = 150;   // how wide the right-click menu gets by default

        // The app's jump list: files and folders you recently (or often) opened with it, like the real taskbar.
        // Click one to open it with that app; right-click it for Windows' menu for the file.
        void AddJumpList(ToolStripItemCollection items, List<string> ids, string exe, List<(ToolStripMenuItem item, string title)> titleItems, int ic)
        {
            List<JumpList.Entry> list;
            string title;
            try { list = JumpList.For(ids, 10, out title); } catch { return; }
            if (list.Count == 0) return;
            Menus.Header(items, title);
            foreach (var e in list)
            {
                var ee = e;
                Bitmap icon = null;
                try { icon = Icons.FromShell(e.Path, ic); } catch { }
                var mi = Menus.Item(items, string.IsNullOrWhiteSpace(e.Name) ? Path.GetFileName(e.Path) : e.Name, icon, () => OpenJumpItem(ee, exe));
                titleItems.Add((mi, mi.Text));
                ExplorerMenuOnRightClick(mi, e.Path);
            }
        }

        static void OpenJumpItem(JumpList.Entry e, string exe)
        {
            bool viaApp = exe != null && !e.IsFolder && File.Exists(e.Path) && !WindowTracker.IsHostExe(exe)
                          && !Path.GetFileName(exe).Equals("explorer.exe", StringComparison.OrdinalIgnoreCase);
            if (viaApp) Launch(exe, "\"" + e.Path + "\"");   // open it in the same app, like the real jump list
            else Launch(e.Path);
        }

        static void Shortenable(List<(ToolStripMenuItem item, string title)> list, ToolStripMenuItem mi) => list.Add((mi, mi.Text));

        // Shorten long window titles (and app names) until the whole menu is no wider than the width you chose
        void FitTitles(ContextMenuStrip menu, List<(ToolStripMenuItem item, string title)> titles)
        {
            int limit = S(cfg.MenuMaxWidth > 0 ? cfg.MenuMaxWidth : DefaultMenuWidth);
            // The menu can't get narrower than its other options (like "Run as administrator"), so never cut
            // titles shorter than the room those already make
            foreach (var (item, _) in titles) item.Text = "…";
            int floor = menu.GetPreferredSize(Size.Empty).Width;
            foreach (var (item, title) in titles) item.Text = title;
            limit = Math.Max(limit, floor);

            for (int pass = 0; pass < 4; pass++)
            {
                int excess = menu.GetPreferredSize(Size.Empty).Width - limit;
                if (excess <= 0) break;
                // every shortened title gets the same room: as wide as the widest one, minus what's too much
                int widest = titles.Max(x => TextRenderer.MeasureText(x.item.Text, x.item.Font).Width);
                int budget = Math.Max(S(40), widest - excess);
                bool changed = false;
                foreach (var (item, title) in titles)
                {
                    var t = FitWidth(title, budget, item.Font);
                    if (t != item.Text) { item.Text = t; changed = true; }
                }
                if (!changed) break;
            }
            foreach (var (item, title) in titles) if (item.Text != title) item.ToolTipText = title;   // point at it to see the whole title
        }

        // Shortens text with "…" so it fits in the given width
        static string FitWidth(string text, int px, Font font = null)
        {
            using (var own = font == null ? new Font("Segoe UI", 9f) : null)
            {
                var f = font ?? own;
                if (TextRenderer.MeasureText(text, f).Width <= px) return text;
                int lo = 1, hi = text.Length;
                while (lo < hi)
                {
                    int mid = (lo + hi + 1) / 2;
                    if (TextRenderer.MeasureText(text.Substring(0, mid).TrimEnd() + "…", f).Width <= px) lo = mid; else hi = mid - 1;
                }
                return text.Substring(0, lo).TrimEnd() + "…";
            }
        }

        // Right-click the app's name inside our menu: Windows' own right-click menu for it
        // (Shift+right-click: the longer one with the extras), just like in File Explorer
        ToolStripMenuItem ExplorerMenuOnRightClick(ToolStripMenuItem mi, BarButton b) => ExplorerMenuOnRightClick(mi, ShellPathFor(b));

        ToolStripMenuItem ExplorerMenuOnRightClick(ToolStripMenuItem mi, string path)
        {
            if (path == null) return mi;
            mi.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                bool shift = (ModifierKeys & Keys.Shift) != 0;
                var at = Cursor.Position;
                (mi.Owner as ToolStripDropDown)?.Close(ToolStripDropDownCloseReason.ItemClicked);
                BeginInvoke((Action)(() => ShellMenu.Show(path, Handle, at.X, at.Y, shift, false)));
            };
            return mi;
        }

        void ShowBarMessage(string text)
        {
            preview.ShowText("msg", text, RectangleToScreen(ClientRectangle), theme, scale);
            var t = new Timer { Interval = 4000 };
            t.Tick += (s, e) => { t.Dispose(); if (preview.IsShowingFor("msg")) preview.HidePopup(); };
            t.Start();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (dragBtn != null) return;
            hover = null; pressed = null; previewTimer.Stop();
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressed = HitTest(e.Location);
            pressHandled = false;
            previewTimer.Stop();
            if (e.Button == MouseButtons.Left && pressed is BarButton b && CanDrag(b))
            {
                dragBtn = b; dragStart = e.Location; dragMoved = false; dropGroup = null;
                dragSection = DragsWholeSection(b, (ModifierKeys & Keys.Shift) != 0);
                sectionDropIndex = -1;
                Capture = true;
            }
            if (e.Button == MouseButtons.Left && pressed is BarButton pb && (pb.Menu != null || pb.Tray != null))
            {
                // Menus and popup grids open the moment you press, not when you let go.
                // Whether this press opens or closes it is decided now, while we still know what was open.
                preview.HidePopup();
                bool wasOpen = PopupOpenFor(pb);
                if (wasOpen) ClosePopups();
                else if (pb.Menu != null) ShowLaunchMenu(pb);   // (each closes whatever other menu or grid is open)
                else ShowTrayFlyout(pb);
                pressHandled = true;
            }
            else if (e.Button == MouseButtons.Left && TrayFlyoutOpen) trayFlyout.Close();   // clicked something else on the bar
            menuClosedByClick = false;   // (this press was seen, so the fallback in OnMouseUp isn't needed)
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if (contentWidth <= ClientSize.Width) return;
            scrollX = Math.Max(0, Math.Min(contentWidth - ClientSize.Width, scrollX - e.Delta / 2));
            preview.HidePopup();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool handled = pressHandled;   // the press already opened or closed a menu / grid
            pressHandled = false;
            if (dragBtn != null)
            {
                var db = dragBtn; bool moved = dragMoved; bool whole = dragSection; int sidx = sectionDropIndex;
                var g = dropGroup; int idx = dropIndex;
                dragBtn = null; dragMoved = false; dropGroup = null; dragSection = false; Capture = false;
                Cursor = Cursors.Default;
                if (moved)
                {
                    pressed = null;
                    Invalidate();
                    if (whole) { if (sidx >= 0) DropSection(db.Group.Section, sidx); }
                    else if (g != null) Drop(db, g, idx);
                    return;
                }
            }
            var target = HitTest(e.Location);
            // If an open menu swallowed the press while closing (so we never saw it), treat this release as the click:
            // on the same menu's button it was a "close", on any other menu or grid it should open that one
            if (pressed == null && !handled && e.Button == MouseButtons.Left && target is BarButton sb && (sb.Menu != null || sb.Tray != null)
                && menuClosedByClick && (DateTime.Now - menuClosedAt).TotalMilliseconds < 1500)
            {
                bool sameMenu = closedMenuKey == sb.Key;
                menuClosedByClick = false; closedMenuKey = null;
                Invalidate();
                if (!sameMenu) { preview.HidePopup(); ClosePopups(); if (sb.Menu != null) ShowLaunchMenu(sb); else ShowTrayFlyout(sb); }
                return;
            }
            bool same = ReferenceEquals(target, pressed) || (target is BarButton tb && pressed is BarButton pb && tb.Key == pb.Key);
            pressed = null;
            Invalidate();
            if (!same) return;

            if (e.Button == MouseButtons.Right) { preview.HidePopup(); ShowContextMenu(target, e.Location); return; }

            if (target is BarButton b)
            {
                preview.HidePopup();
                bool newInstance = e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && (ModifierKeys & Keys.Shift) != 0);
                if (b.Menu != null) { if (e.Button == MouseButtons.Left && !handled) ShowLaunchMenu(b); return; }
                if (b.Tray != null) { if (e.Button == MouseButtons.Left && !handled) ShowTrayFlyout(b); return; }
                if (b.DeskPin && (b.Windows.Count == 0 || newInstance)) { LaunchOnDesktop(b.Pin, b.Group.Desktop); return; }
                if (b.Pin != null && !b.DeskPin) { OpenItem(b.Pin, b.Group.Section?.Browser); return; }
                if (newInstance) { NewInstance(b.Windows[0]); return; }
                if (e.Button == MouseButtons.Left) ClickButton(b);
            }
            else if (target is GroupModel g && e.Button == MouseButtons.Left && g.Kind == GroupKind.Desktop && !g.IsCurrent)
            {
                preview.HidePopup();
                SwitchDesktop(g.Desktop.Id, IntPtr.Zero);
            }
        }

        void ClickButton(BarButton b)
        {
            IntPtr fg = ForegroundRoot();
            var wins = b.Windows;
            int activeIdx = wins.FindIndex(w => w.Hwnd == fg);
            if (wins.Count == 1)
            {
                var w = wins[0];
                if (activeIdx == 0 && !Native.IsIconic(w.Hwnd)) Native.ShowWindowAsync(w.Hwnd, Native.SW_MINIMIZE);
                else Activate(w);
                return;
            }
            if (!cfg.ClickCyclesWindows)
            {
                // Like Windows: show the windows to pick from
                preview.ShowWindows("win:" + b.Key, b.Windows, ScreenRectOf(b), theme, scale);
                return;
            }
            if (activeIdx >= 0) { Activate(wins[(activeIdx + 1) % wins.Count]); return; }
            var best = wins.OrderByDescending(w => LastActivated(w.Hwnd)).First();
            Activate(best);
        }

        void ShowPreviewFor(object target)
        {
            if (target == null || !Visible || dragBtn != null) return;
            if (target is BarButton b)
            {
                if (b.Pin != null && b.Windows.Count == 0) { preview.ShowText("pin:" + b.Key, ItemTitle(b.Pin), ScreenRectOf(b), theme, scale); return; }
                if ((b.Tray != null && trayFlyout != null && !trayFlyout.IsDisposed && trayFlyout.Visible && trayFlyout.Tag == b.Tray) || openMenuKey == b.Key) return;
                if ((b.Tray != null || b.Menu != null) && !cfg.ShowGroupNames) { preview.HidePopup(); return; }   // their names on hover are optional (off by default)
                if (b.Tray != null) { preview.ShowText("tray:" + b.Key, string.IsNullOrWhiteSpace(b.Tray.Name) ? "Group" : b.Tray.Name, ScreenRectOf(b), theme, scale); return; }
                if (b.Menu != null) { preview.ShowText("menu:" + b.Key, string.IsNullOrWhiteSpace(b.Menu.Name) ? "Menu" : b.Menu.Name, ScreenRectOf(b), theme, scale); return; }
                if (!cfg.ShowPreviews)
                {
                    preview.ShowText("win:" + b.Key, b.Windows.Count == 1 ? b.Windows[0].Title : Icons.FriendlyName(b.Windows[0]) + $" ({b.Windows.Count} windows)", ScreenRectOf(b), theme, scale);
                    return;
                }
                if (preview.IsShowingFor("win:" + b.Key)) return;
                preview.ShowWindows("win:" + b.Key, b.Windows, ScreenRectOf(b), theme, scale);
            }

        }

        static string ItemTitle(LaunchItem it) =>
            !string.IsNullOrWhiteSpace(it.Name) ? it.Name : Path.GetFileNameWithoutExtension(Launcher.Expand(it.Target));

        // ================= actions =================

        public void Activate(AppWindow w)
        {
            IntPtr h = w.Hwnd;
            if (!Native.IsWindow(h)) { dirty = true; return; }
            if (TrayFlyoutOpen) trayFlyout.Close();
            if (Native.IsIconic(h)) Native.ShowWindowAsync(h, Native.SW_RESTORE);
            if (!Native.SetForegroundWindow(h)) Native.SwitchToThisWindow(h, true);

            // Windows normally jumps to the window's desktop by itself. If it didn't, do it ourselves.
            Guid target = w.DesktopId;
            if (target != Guid.Empty && target != desktops.CurrentId && desktops.Find(target) != null)
            {
                var t = new Timer { Interval = 300 };
                t.Tick += (s, e) =>
                {
                    t.Dispose();
                    if (Native.IsWindow(h) && desktops.IsOnCurrent(h) == false) SwitchDesktop(target, h);
                };
                t.Start();
            }
            dirty = true;
        }

        // Switches virtual desktop using only documented APIs: a tiny invisible window of OUR app is moved
        // to the target desktop and activated, which makes Windows switch to that desktop.
        void SwitchDesktop(Guid id, IntPtr thenActivate)
        {
            if (TrayFlyoutOpen) trayFlyout.Close();
            var helper = new Form
            {
                FormBorderStyle = FormBorderStyle.None, ShowInTaskbar = true, StartPosition = FormStartPosition.Manual,
                Size = new Size(1, 1), Location = new Point(lastBounds.X, lastBounds.Y), Opacity = 0.01, Text = ""
            };
            helper.Show();
            desktops.MoveOwnWindow(helper.Handle, id);
            Native.SetForegroundWindow(helper.Handle);
            var t = new Timer { Interval = 350 };
            t.Tick += (s, e) =>
            {
                t.Dispose();
                IntPtr next = thenActivate;
                if (next == IntPtr.Zero || !Native.IsWindow(next))
                {
                    // the window you used last on that desktop, else the desktop itself
                    var w = lastWindows.Where(x => x.DesktopId == id && !x.Minimized).OrderByDescending(x => LastActivated(x.Hwnd)).FirstOrDefault();
                    next = w != null ? w.Hwnd : GetShellWindow();
                }
                if (next != IntPtr.Zero) Native.SetForegroundWindow(next);
                helper.Close();
                helper.Dispose();
                dirty = true;
            };
            t.Start();
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr GetShellWindow();

        static void CloseWindow(IntPtr h) => Native.PostMessage(h, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_CLOSE, IntPtr.Zero);

        static void Launch(string path, string args = null, bool admin = false)
        {
            try
            {
                var psi = new ProcessStartInfo(path) { UseShellExecute = true };
                if (args != null) psi.Arguments = args;
                if (admin) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch { }
        }

        // Opens a desktop's pinned app on that desktop (switching there first if needed)
        void LaunchOnDesktop(LaunchItem it, DesktopInfo d, int delay = 0)
        {
            if (d == null || d.Id == desktops.CurrentId) { if (delay == 0) OpenItem(it, null); else After(delay, () => OpenItem(it, null)); return; }
            SwitchDesktop(d.Id, IntPtr.Zero);
            After(450 + delay, () => OpenItem(it, null));
        }

        public void OpenAllOnDesktopPublic(DesktopInfo d) => OpenAllOnDesktop(d);

        void OpenAllOnDesktop(DesktopInfo d)
        {
            var pins = cfg.FindDesktop(d.Id)?.Pins;
            if (pins == null) return;
            var running = ButtonsForDesktop(d.Id);
            int n = 0;
            foreach (var p in pins)
            {
                if (running.Any(b => b.Pin == p && b.Windows.Count > 0)) continue;   // already open
                LaunchOnDesktop(p, d, n++ * 250);
            }
        }

        void After(int ms, Action a)
        {
            var t = new Timer { Interval = Math.Max(1, ms) };
            t.Tick += (s, e) => { t.Dispose(); a(); };
            t.Start();
        }

        void OpenItem(LaunchItem it, BrowserOptions menuBrowser)
        {
            if (it.Type == "web") Launcher.OpenWeb(it, menuBrowser, this);
            else Launcher.Run(it);
        }

        static bool IsPackaged(AppWindow w) =>
            w.Aumid != null && (WindowTracker.IsHostExe(w.ExePath) || (w.ExePath ?? "").IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0);

        void NewInstance(AppWindow w)
        {
            // your settings for the app (arguments, start in, run as administrator) apply to new windows
            if (cfg.FindApp(w.GroupKey)?.ChangesLaunch == true) { var li = PinItemFor(w); if (li != null) { Launcher.Run(li); return; } }
            if (IsPackaged(w)) Launch("explorer.exe", @"shell:AppsFolder\" + w.Aumid);
            else if (w.ExePath != null && !WindowTracker.IsHostExe(w.ExePath)) Launch(w.ExePath);
        }

        public void OpenNewWindow(AppWindow w) => NewInstance(w);

        // Opens a new window of an open app as administrator
        public void RunAsAdmin(AppWindow w)
        {
            var li = PinItemFor(w);
            if (li != null) { li.RunAsAdmin = true; Launcher.Run(li); }
        }

        // ---- pins ----

        static string PinTargetFor(AppWindow w) => IsPackaged(w) ? @"shell:AppsFolder\" + w.Aumid : w.ExePath;

        // Finds this app among your pinned groups (matching the real app, not just the name)
        LaunchItem FindPin(AppWindow w)
        {
            string t = PinTargetFor(w);
            foreach (var sec in cfg.Layout.Where(s => s.Type == "pins" || s.Type == "item"))
                foreach (var it in sec.Items)
                {
                    if (t != null && string.Equals(Launcher.Expand(it.Target), t, StringComparison.OrdinalIgnoreCase)) return it;
                    if (AppIdentity.Matches(it, w)) return it;
                }
            return null;
        }

        LaunchItem PinItemFor(AppWindow w)
        {
            string target = PinTargetFor(w);
            if (target == null) return null;
            var st = cfg.FindApp(w.GroupKey);
            return new LaunchItem
            {
                Type = "program", Name = Icons.FriendlyName(w), Target = target, IconPath = st?.IconPath ?? "", BackColor = st?.BackColor ?? "",
                Arguments = st?.Arguments ?? "", StartIn = st?.StartIn ?? "", RunAsAdmin = st?.RunAsAdmin ?? false
            };
        }

        void PinToDesktop(AppWindow w, DesktopInfo d)
        {
            var item = PinItemFor(w);
            if (item == null) return;
            cfg.DesktopFor(d.Id).Pins.Add(item);
            if (!cfg.DesktopPinsFirst)
            {
                // keep it exactly where it is on the bar: remember the current order with the new pin in the app's place
                var g = groups.FirstOrDefault(x => x.Kind == GroupKind.Desktop && x.Desktop?.Id == d.Id);
                if (g != null)
                {
                    string pinKey = OrderKeyOf(new BarButton { DeskPin = true, Pin = item });
                    var keys = g.Buttons.Where(b => b.DeskPin || b.Windows.Count > 0)
                                        .Select(b => !b.DeskPin && b.Windows.Any(x => x.GroupKey == w.GroupKey) ? pinKey : OrderKeyOf(b))
                                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var order = cfg.OrderFor(OrderKey(g), true);
                    var hidden = order.Keys.Where(k => !keys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
                    order.Keys = keys.Concat(hidden).ToList();
                }
            }
            ApplySettings();
            settingsForm?.ReloadTree();
        }

        void PinWindow(AppWindow w)
        {
            string target = PinTargetFor(w);
            if (target == null) return;
            var sec = cfg.Layout.FirstOrDefault(s => s.Type == "pins");
            if (sec == null) { sec = new BarSection { Type = "pins", Name = "Pinned apps" }; cfg.Layout.Insert(0, sec); }
            sec.Items.Add(PinItemFor(w));
            ApplySettings();
            settingsForm?.ReloadTree();
        }

        // Unpinning from a desktop: if the app is open it stays exactly where it is on the bar
        // (its place in your saved order passes from the pin to the open app)
        public void KeepPlaceOnUnpin(LaunchItem it)
        {
            foreach (var g in groups.Where(x => x.Kind == GroupKind.Desktop))
            {
                var pb = g.Buttons.FirstOrDefault(b => b.DeskPin && ReferenceEquals(b.Pin, it));
                if (pb == null) continue;
                var keys = new List<string>();
                foreach (var b in g.Buttons)
                {
                    if (ReferenceEquals(b, pb)) { if (b.Windows.Count > 0) keys.Add(b.Windows[0].GroupKey); }
                    else if (b.DeskPin || b.Windows.Count > 0) keys.Add(OrderKeyOf(b));
                }
                keys = keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var order = cfg.OrderFor(OrderKey(g), true);
                string pinKey = OrderKeyOf(pb);
                var hidden = order.Keys.Where(k => !keys.Contains(k, StringComparer.OrdinalIgnoreCase) && !string.Equals(k, pinKey, StringComparison.OrdinalIgnoreCase)).ToList();
                order.Keys = keys.Concat(hidden).ToList();
            }
        }

        void Unpin(LaunchItem it)
        {
            KeepPlaceOnUnpin(it);
            foreach (var sec in cfg.Layout) sec.Items.Remove(it);
            cfg.Layout.RemoveAll(x => x.Type == "item" && x.Items.Count == 0);
            foreach (var d in cfg.Desktops) d.Pins.Remove(it);
            ApplySettings();
            settingsForm?.ReloadTree();
        }

        void MovePin(BarButton b, int dir)
        {
            var list = b.Group.Section.Items;
            int i = list.IndexOf(b.Pin), j = i + dir;
            if (i < 0 || j < 0 || j >= list.Count) return;
            list.RemoveAt(i); list.Insert(j, b.Pin);
            ApplySettings();
            settingsForm?.ReloadTree();
        }

        // ================= menus =================

        int MenuIcon => Math.Max(16, S(16));

        // The menu or popup grid that's open, so a press on the bar can tell whether it opens or closes one
        ContextMenuStrip openMenu;           // any menu shown from the bar (a Menu button's menu, or a right-click menu)
        string openMenuKey, closedMenuKey;   // which Menu button it belongs to (null for right-click menus)
        DateTime menuClosedAt;
        bool menuClosedByClick;              // it closed because you clicked outside it (maybe on its own button)
        bool pressHandled;                   // this press on the bar already opened or closed a menu / grid

        bool TrayFlyoutOpen => trayFlyout != null && !trayFlyout.IsDisposed;

        // Was this button's menu / grid open when the press started? An open menu closes itself the instant
        // you press outside it (just before the bar hears about the press), so "closed a moment ago by a click" counts as open.
        bool PopupOpenFor(BarButton b)
        {
            if (b.Menu != null)
                return (openMenu != null && openMenuKey == b.Key)
                    || (closedMenuKey == b.Key && menuClosedByClick && (DateTime.Now - menuClosedAt).TotalMilliseconds < 250);
            return TrayFlyoutOpen && trayFlyout.Tag == b.Tray;
        }

        void ClosePopups()
        {
            if (TrayFlyoutOpen) trayFlyout.Close();
            if (openMenu != null && !openMenu.IsDisposed) openMenu.Close();
        }

        // Before a new menu opens: move the focus to the bar FIRST, then close the grid. Closing the grid while it has
        // the focus lets Windows hand the focus to some other app, which then closes the new menu the moment it opens.
        void ClosePopupsForNewMenu()
        {
            if (TrayFlyoutOpen) Native.SetForegroundWindow(Handle);
            ClosePopups();
        }

        void TrackMenu(ContextMenuStrip menu, string key)
        {
            openMenu = menu;
            openMenuKey = key;
            menu.Closed += (s, e) =>
            {
                if (openMenu == menu) { openMenu = null; openMenuKey = null; }
                closedMenuKey = key; menuClosedAt = DateTime.Now;
                menuClosedByClick = e.CloseReason == ToolStripDropDownCloseReason.AppClicked;
            };
        }

        void ShowLaunchMenu(BarButton b)
        {
            var sec = b.Menu;
            // The menu's own colors and text size, if you set them
            var mt = theme.WithMenuColors(Theme.Parse(sec.MenuBackColor), Theme.Parse(sec.MenuTextColor), Theme.Parse(sec.MenuHoverColor));
            Font font = sec.MenuFontSize > 0 ? new Font("Segoe UI", sec.MenuFontSize) : null;
            var menu = Menus.Create(mt, scale);
            if (font != null) menu.Font = font;
            AddLaunchItems(menu.Items, sec.Items, sec.Browser, sec);
            if (sec.Items.Count == 0)
            {
                Menus.Item(menu.Items, "This menu is empty", null, null).Enabled = false;
                Menus.Item(menu.Items, "Add items…", null, () => OpenSettings(sec));
            }
            Menus.StyleTree(menu.Items, mt, scale, font);
            ApplySizes(menu.Items, sec);
            ShowMenuAbove(menu, b, false, b.Key);
        }

        // Fixed item heights and menu width (per menu, or per item)
        void ApplySizes(ToolStripItemCollection items, BarSection sec)
        {
            foreach (ToolStripItem it in items)
            {
                if (it is ToolStripSeparator) continue;
                var look = it.Tag as ItemLook;
                int h = look != null && look.Height > 0 ? S(look.Height) : sec.MenuItemHeight > 0 ? S(sec.MenuItemHeight) : 0;
                int w = sec.MenuWidth > 0 ? S(sec.MenuWidth) : 0;
                if (look?.Picture != null)
                {
                    h = Math.Max(h, look.Picture.Height + S(10));
                    w = Math.Max(w, look.Picture.Width + S(24));
                }
                if (h > 0 || w > 0)
                {
                    var pref = it.GetPreferredSize(Size.Empty);
                    it.AutoSize = false;
                    it.Size = new Size(w > 0 ? Math.Max(w, sec.MenuWidth > 0 ? 0 : pref.Width) : pref.Width, h > 0 ? h : pref.Height);
                }
                if (it is ToolStripMenuItem mi && mi.HasDropDownItems) ApplySizes(mi.DropDownItems, sec);
            }
        }

        void AddLaunchItems(ToolStripItemCollection into, List<LaunchItem> items, BrowserOptions browser, BarSection sec = null)
        {
            foreach (var it in items)
            {
                var item = it;
                switch (it.Type)
                {
                    case "separator": Menus.Sep(into); break;
                    case "folder":
                    {
                        int isz = ItemIconPx(it, sec);
                        var sub = Menus.Item(into, string.IsNullOrWhiteSpace(it.Name) ? "Folder" : it.Name,
                            !string.IsNullOrWhiteSpace(it.IconPath) ? Launcher.IconFor(it, isz) : FolderIcon(isz), null);
                        Look(sub, it, sec);
                        AddLaunchItems(sub.DropDownItems, it.Children, browser, sec);
                        if (it.Children.Count == 0) sub.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
                        break;
                    }
                    default:
                    {
                        var mi = Menus.Item(into, ItemTitle(it), Launcher.IconFor(it, ItemIconPx(it, sec), browser), () => OpenItem(item, browser));
                        if (it.RunAsAdmin) mi.ToolTipText = ItemTitle(it) + " (runs as administrator)";
                        Look(mi, it, sec);
                        break;
                    }
                }
            }
        }

        int ItemIconPx(LaunchItem it, BarSection sec) =>
            S(it.IconSize > 0 ? it.IconSize : sec != null && sec.MenuIconSize > 0 ? sec.MenuIconSize : 16);

        // The plain folder icon for sub-menus, asked from Windows once per size
        readonly Dictionary<int, Bitmap> folderIcons = new Dictionary<int, Bitmap>();
        Bitmap FolderIcon(int px)
        {
            if (!folderIcons.TryGetValue(px, out var b)) { b = Launcher.IconForTarget("shell:Personal", px); if (b != null) folderIcons[px] = b; }
            return b;
        }

        // Loads the icons of popup grids and menus ahead of time (shortly after starting and after a settings change),
        // so opening one never has to wait for Windows to hand the icons over
        Timer warmTimer;
        void ScheduleIconWarmup()
        {
            if (warmTimer == null) { warmTimer = new Timer { Interval = 800 }; warmTimer.Tick += (s, e) => { warmTimer.Stop(); WarmPopupIcons(); }; }
            warmTimer.Stop();
            warmTimer.Start();
        }

        void WarmPopupIcons()
        {
            try
            {
                foreach (var sec in cfg.Layout)
                {
                    if (sec.Type == "pins" && sec.Style == "grid")
                    {
                        int px = (int)Math.Round((sec.GridIconSize > 0 ? sec.GridIconSize : 24) * scale);   // same size the grid draws
                        foreach (var it in sec.Items) if (it.Type == "program" || it.Type == "web") Launcher.IconFor(it, px, sec.Browser);
                    }
                    else if (sec.Type == "menu") WarmMenuIcons(sec.Items, sec);
                }
            }
            catch { }
        }

        void WarmMenuIcons(List<LaunchItem> items, BarSection sec)
        {
            foreach (var it in items)
            {
                if (it.Type == "separator") continue;
                int px = ItemIconPx(it, sec);
                if (it.Type == "folder")
                {
                    if (!string.IsNullOrWhiteSpace(it.IconPath)) Launcher.IconFor(it, px); else FolderIcon(px);
                    WarmMenuIcons(it.Children, sec);
                }
                else Launcher.IconFor(it, px, sec.Browser);
            }
        }

        // Colors, bold text, and "picture only" for one menu item
        void Look(ToolStripMenuItem mi, LaunchItem it, BarSection sec)
        {
            mi.ImageScaling = ToolStripItemImageScaling.None;   // keep each item's own icon size
            var look = new ItemLook { Back = Theme.Parse(it.BackColor), Fore = Theme.Parse(it.TextColor), Height = it.Height };
            if (it.Bold) mi.Font = new Font(mi.Font, FontStyle.Bold);
            if (!it.ShowText)
            {
                mi.ToolTipText = mi.Text;
                mi.Text = "";
                if (mi.Image != null) { look.Picture = (Bitmap)mi.Image; mi.Image = null; }
            }
            mi.Tag = look;
        }

        void ShowMenuAbove(ContextMenuStrip menu, object anchorTarget, bool style = true, string key = null)
        {
            ClosePopupsForNewMenu();   // only one menu or grid at a time
            if (style) Menus.StyleTree(menu.Items, theme, scale);
            // Take focus while the menu is open so it closes when you click elsewhere (the real taskbar does the same)
            Native.SetForegroundWindow(Handle);
            var anchor = anchorTarget != null ? ScreenRectOf(anchorTarget) : new Rectangle(Cursor.Position, Size.Empty);
            // Centered above what you clicked, like the real taskbar (kept on the screen)
            var size = menu.GetPreferredSize(Size.Empty);
            int x = anchorTarget != null ? anchor.Left + anchor.Width / 2 - size.Width / 2 : anchor.Left;
            var work = Screen.FromPoint(new Point(anchor.Left + anchor.Width / 2, Top)).Bounds;
            x = Math.Max(work.Left + S(4), Math.Min(x, work.Right - S(4) - size.Width));
            TrackMenu(menu, key);
            menu.Show(new Point(x, Top - S(4)), ToolStripDropDownDirection.AboveRight);
        }

        // ---- pinning helpers ----
        static string SecName(BarSection s) => string.IsNullOrWhiteSpace(s.Name) ? "Group" : s.Name;

        public LaunchItem ItemInGroup(BarSection sec, AppWindow w) => ItemIn(sec.Items, w);

        public LaunchItem ItemIn(List<LaunchItem> list, AppWindow w)
        {
            string t = PinTargetFor(w);
            return list.FirstOrDefault(i => (t != null && string.Equals(Launcher.Expand(i.Target), t, StringComparison.OrdinalIgnoreCase)) || AppIdentity.Matches(i, w));
        }

        // "Pin to group" submenu: every group, ticked where the app is already pinned; click to pin or unpin
        void AddPinToGroupMenu(ToolStripItemCollection items, AppWindow w)
        {
            var sub = Menus.Item(items, "Pin to group", null, null);
            foreach (var sec in cfg.Layout.Where(x => x.Type == "pins"))
            {
                var sc = sec;
                var existing = ItemInGroup(sc, w);
                var mi = Menus.Item(sub.DropDownItems, SecName(sc), null, () =>
                {
                    var ex = ItemInGroup(sc, w);
                    if (ex != null) sc.Items.Remove(ex); else { var it = PinItemFor(w); if (it != null) sc.Items.Add(it); }
                    ApplySettings(); settingsForm?.ReloadTree();
                });
                mi.Checked = existing != null;
            }
            Menus.Sep(sub.DropDownItems);
            Menus.Item(sub.DropDownItems, "New group", null, () =>
            {
                var it = PinItemFor(w);
                if (it == null) return;
                var sec = new BarSection { Type = "pins", Name = "New group" };
                sec.Items.Add(it);
                int at = cfg.Layout.FindIndex(x => x.Type == "windows");
                cfg.Layout.Insert(at < 0 ? cfg.Layout.Count : at, sec);
                ApplySettings(); settingsForm?.ReloadTree();
            });
        }

        // Path for Explorer's own menu / Properties
        string ShellPathFor(BarButton b)
        {
            if (b.Pin != null && b.Pin.Type == "program")
            {
                string t = Launcher.Expand(b.Pin.Target);
                if (t.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || File.Exists(t) || Directory.Exists(t)) return t;
                string r = Launcher.Resolve(t);
                return File.Exists(r) ? r : null;
            }
            if (b.Windows.Count > 0)
            {
                var w = b.Windows[0];
                if (IsPackaged(w)) return @"shell:AppsFolder\" + w.Aumid;
                var pin = FindPin(w);
                if (pin != null && pin.Target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(Launcher.Expand(pin.Target))) return Launcher.Expand(pin.Target);
                return w.ExePath != null && File.Exists(w.ExePath) ? w.ExePath : null;
            }
            return null;
        }

        TrayFlyout trayFlyout;

        void ShowTrayFlyout(BarButton b)
        {
            if (TrayFlyoutOpen && trayFlyout.Tag == b.Tray) { trayFlyout.Close(); return; }   // its button again: close it
            if (openMenu != null && !openMenu.IsDisposed) openMenu.Close();
            // Open the new grid first and only then close another one that's open, so the focus goes straight
            // from one to the other instead of passing through some other app (which would close the new one)
            var old = TrayFlyoutOpen ? trayFlyout : null;
            trayFlyout = new TrayFlyout(this, b.Tray, theme, scale, ScreenRectOf(b)) { Tag = b.Tray };
            trayFlyout.Show();
            trayFlyout.Activate();
            Native.SetForegroundWindow(trayFlyout.Handle);
            old?.Close();
        }

        public void OpenFromGrid(LaunchItem it, BarSection sec) => OpenItem(it, sec.Browser);

        // Every right-click option: menu, id, text, on by default. Ids ending in "|" are divider lines.
        // The settings page lets you turn each one on or off and drag them into your own order.
        public static readonly string[][] MenuOptions =
        {
            new[] { "Open apps", "w.jumplist", "Recent files (jump list)", "0" },
            new[] { "Open apps", "w.sep0|", "── divider ──", "1" },
            new[] { "Open apps", "w.windows", "List of the app's windows", "1" },
            new[] { "Open apps", "w.sep1|", "── divider ──", "1" },
            new[] { "Open apps", "w.admin", "Run as administrator", "1" },
            new[] { "Open apps", "w.location", "Open file location", "1" },
            new[] { "Open apps", "w.properties", "Properties", "1" },
            new[] { "Open apps", "w.copypath", "Copy path", "0" },
            new[] { "Open apps", "w.sep2|", "── divider ──", "1" },
            new[] { "Open apps", "w.pingroup", "Pin to group", "1" },
            new[] { "Open apps", "w.pindesk", "Pin to this desktop", "1" },
            new[] { "Open apps", "w.movedesk", "Move to desktop", "0" },
            new[] { "Open apps", "w.switchdesk", "Go to this app's desktop", "0" },
            new[] { "Open apps", "w.customize", "Customize…", "1" },
            new[] { "Open apps", "w.sep3|", "── divider ──", "1" },
            new[] { "Open apps", "w.minimize", "Minimize / Restore", "0" },
            new[] { "Open apps", "w.maximize", "Maximize", "0" },
            new[] { "Open apps", "w.endtask", "End task", "0" },
            new[] { "Open apps", "w.new", "The app's name (opens a new window)", "1" },
            new[] { "Open apps", "w.close", "Close window", "1" },

            new[] { "Pinned apps", "p.jumplist", "Recent files (jump list)", "0" },
            new[] { "Pinned apps", "p.sep0|", "── divider ──", "1" },
            new[] { "Pinned apps", "p.admin", "Run as administrator", "1" },
            new[] { "Pinned apps", "p.location", "Open file location", "1" },
            new[] { "Pinned apps", "p.properties", "Properties", "1" },
            new[] { "Pinned apps", "p.copypath", "Copy path", "0" },
            new[] { "Pinned apps", "p.sep1|", "── divider ──", "1" },
            new[] { "Pinned apps", "p.customize", "Customize…", "1" },
            new[] { "Pinned apps", "p.open", "The app's name (opens it)", "1" },
            new[] { "Pinned apps", "p.unpin", "Unpin", "1" },

            new[] { "Menus and popup grids", "m.customize", "Customize…", "1" },
            new[] { "Menus and popup grids", "m.open", "Its name (opens it)", "0" },
            new[] { "Menus and popup grids", "m.remove", "Unpin", "1" },

            new[] { "Desktop numbers", "d.switch", "Switch to this desktop", "0" },
            new[] { "Desktop numbers", "d.openall", "Open all apps pinned to this desktop", "1" },
            new[] { "Desktop numbers", "d.customize", "Customize this desktop…", "1" },
            new[] { "Desktop numbers", "d.minimizeall", "Minimize all windows on this desktop", "0" },
            new[] { "Desktop numbers", "d.closeall", "Close all windows on this desktop", "1" },

            new[] { "Empty space on the bar", "b.settings", "CL-Taskbar settings (always shown)", "1" },
            new[] { "Empty space on the bar", "b.wintaskbar", "Windows taskbar settings", "1" },
            new[] { "Empty space on the bar", "b.taskmgr", "Task Manager", "0" },
            new[] { "Empty space on the bar", "b.showdesktop", "Show desktop", "0" },
            new[] { "Empty space on the bar", "b.sep1|", "── divider ──", "1" },
            new[] { "Empty space on the bar", "b.exit", "Exit CL-Taskbar", "1" },
        };

        public bool On(string id)
        {
            if (id == "b.settings") return true;
            if (cfg.MenuChoices.TryGetValue(id, out bool v)) return v;
            var o = MenuOptions.FirstOrDefault(x => x[1] == id);
            return o == null || o[3] == "1";
        }

        // The ids of one menu, in your order (new options go where they are by default)
        public List<string> OrderFor(string menu)
        {
            var defaults = MenuOptions.Where(o => o[0] == menu).Select(o => o[1]).ToList();
            var mine = cfg.MenuOrder.Where(defaults.Contains).ToList();
            foreach (var id in defaults)
                if (!mine.Contains(id))
                {
                    int di = defaults.IndexOf(id);
                    var prev = defaults.Take(di).LastOrDefault(mine.Contains);
                    mine.Insert(prev == null ? 0 : mine.IndexOf(prev) + 1, id);
                }
            return mine;
        }

        // Builds a menu from your order: each id either adds its item(s) or a divider line
        void Emit(ToolStripItemCollection items, string menu, Dictionary<string, Action> builders)
        {
            foreach (var id in OrderFor(menu))
            {
                if (!On(id)) continue;
                if (id.EndsWith("|")) { Menus.Sep(items); continue; }
                if (builders.TryGetValue(id, out var act)) act();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);

        void ShowContextMenu(object target, Point clientPt)
        {
            ClosePopupsForNewMenu();
            // Shift+right-click: Explorer's own full menu for the app.
            // With "Use Windows' own right-click menus for apps" on, a plain right-click shows Windows' normal menu too.
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (target is BarButton sb && (shift || cfg.WindowsAppMenus) && sb.Menu == null && sb.Tray == null)
            {
                string sp = ShellPathFor(sb);
                if (sp != null)
                {
                    var r = ScreenRectOf(sb);
                    if (ShellMenu.Show(sp, Handle, r.Left + r.Width / 2, Top - S(4), shift)) return;
                }
                // no file behind it (a web link, for example): fall back to CL-Taskbar's menu
            }

            var menu = Menus.Create(theme, scale);
            menu.ShowItemToolTips = true;
            var titleItems = new List<(ToolStripMenuItem item, string title)>();   // window titles, shortened to fit below
            var items = menu.Items;
            int ic = MenuIcon;
            var B = new Dictionary<string, Action>();

            if (target is BarButton pb && pb.Pin != null && pb.Windows.Count == 0)
            {
                // A pinned app that isn't open
                var it = pb.Pin;
                var browser = pb.Group.Section?.Browser;
                var owner = pb.Group.Section;
                B["p.open"] = () => Shortenable(titleItems, ExplorerMenuOnRightClick(Menus.Item(items, ItemTitle(it), Launcher.IconFor(it, ic, browser),
                    () => { if (pb.DeskPin) LaunchOnDesktop(it, pb.Group.Desktop); else OpenItem(it, browser); }, true), pb));
                if (it.Type == "program")
                {
                    var jid = AppIdentity.IdOf(it);
                    B["p.jumplist"] = () => AddJumpList(items, JumpList.IdsFor(jid.aumid, jid.exe), jid.aumid != null && jid.exe == null ? null : jid.exe, titleItems, ic);
                    string path = Launcher.Resolve(it.Target);
                    string sp = ShellPathFor(pb);
                    bool isFolder = false;
                    try { isFolder = Directory.Exists(path); } catch { }
                    if (!isFolder)
                        B["p.admin"] = () => Menus.Item(items, "Run as administrator", null, () => Launcher.Run(new LaunchItem { Target = it.Target, Arguments = it.Arguments, StartIn = it.StartIn, RunAsAdmin = true, Name = it.Name }));
                    if (File.Exists(path) || (isFolder && Path.GetDirectoryName(path.TrimEnd('\\')) != null)) B["p.location"] = () => Menus.Item(items, "Open file location", null, () => Launch("explorer.exe", "/select,\"" + path + "\""));
                    if (sp != null) B["p.properties"] = () => Menus.Item(items, "Properties", null, () => ShellMenu.Properties(sp, Handle));
                }
                B["p.copypath"] = () => Menus.Item(items, "Copy path", null, () => { try { Clipboard.SetText(Launcher.Expand(it.Target)); } catch { } });
                B["p.customize"] = () => Menus.Item(items, "Customize…", null, () => OpenSettings(pb.DeskPin || owner.Type != "item" ? (object)it : owner));
                B["p.unpin"] = () =>
                {
                    if (pb.DeskPin) Menus.Item(items, "Unpin from this desktop", null, () => Unpin(it));
                    else if (owner.Type == "item") Menus.Item(items, "Remove from taskbar", null, () => { cfg.Layout.Remove(owner); ApplySettings(); settingsForm?.ReloadTree(); });
                    else Menus.Item(items, "Unpin from " + SecName(owner), null, () => Unpin(it));
                };
                Emit(items, "Pinned apps", B);
            }
            else if (target is BarButton mb && (mb.Menu != null || mb.Tray != null))
            {
                var sec = mb.Menu ?? mb.Tray;
                B["m.open"] = () => Menus.Item(items, SecName(sec), IconOf(mb, ic), () =>
                {
                    // wait for this right-click menu to finish closing, then open it
                    var t = new Timer { Interval = 150 };
                    t.Tick += (s, e) => { t.Dispose(); closedMenuKey = null; if (mb.Menu != null) ShowLaunchMenu(mb); else ShowTrayFlyout(mb); };
                    t.Start();
                }, true);
                B["m.customize"] = () => Menus.Item(items, "Customize…", null, () => OpenSettings(sec));
                B["m.remove"] = () => Menus.Item(items, "Unpin from taskbar", null, () =>
                {
                    if (MessageBox.Show("Remove \"" + sec.Name + "\" and everything in it from the taskbar?", "CL-Taskbar",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                    cfg.Layout.Remove(sec);
                    ApplySettings(); settingsForm?.ReloadTree();
                });
                Emit(items, "Menus and popup grids", B);
            }
            else if (target is BarButton wb && wb.Windows.Count > 0)
            {
                // An open app (possibly also pinned to this desktop)
                var w0 = wb.Windows[0];
                var grp = wb.Group;
                bool canLaunch = IsPackaged(w0) || (w0.ExePath != null && !WindowTracker.IsHostExe(w0.ExePath));
                B["w.jumplist"] = () => AddJumpList(items, JumpList.IdsFor(w0.Aumid, w0.ExePath), IsPackaged(w0) ? null : w0.ExePath, titleItems, ic);
                if (wb.Windows.Count > 1)
                    B["w.windows"] = () =>
                    {
                        Menus.Header(items, "Windows");
                        foreach (var w in wb.Windows.Take(15))
                        {
                            var ww = w;
                            string title = string.IsNullOrEmpty(w.Title) ? "(untitled)" : w.Title;
                            var mi = Menus.Item(items, Trunc(title, 200), Icons.ForWindow(w, ic), () => Activate(ww));
                            titleItems.Add((mi, title));
                        }
                    };
                if (wb.DeskPin) B["w.new"] = () => Shortenable(titleItems, ExplorerMenuOnRightClick(Menus.Item(items, Icons.FriendlyName(w0), Icons.ForWindow(w0, ic), () => OpenItem(wb.Pin, null), true), wb));
                else if (canLaunch) B["w.new"] = () => Shortenable(titleItems, ExplorerMenuOnRightClick(Menus.Item(items, Icons.FriendlyName(w0), Icons.ForWindow(w0, ic), () => NewInstance(w0), true), wb));
                if (canLaunch)
                    B["w.admin"] = () => Menus.Item(items, "Run as administrator", null, () =>
                    {
                        var li = PinItemFor(w0);
                        if (li != null) { li.RunAsAdmin = true; Launcher.Run(li); }
                    });
                if (w0.ExePath != null && !WindowTracker.IsHostExe(w0.ExePath) && !IsPackaged(w0))
                {
                    B["w.location"] = () => Menus.Item(items, "Open file location", null, () => Launch("explorer.exe", "/select,\"" + w0.ExePath + "\""));
                    B["w.copypath"] = () => Menus.Item(items, "Copy path", null, () => { try { Clipboard.SetText(w0.ExePath); } catch { } });
                }
                string spw = ShellPathFor(wb);
                if (spw != null) B["w.properties"] = () => Menus.Item(items, "Properties", null, () => ShellMenu.Properties(spw, Handle));
                if (canLaunch) B["w.pingroup"] = () => AddPinToGroupMenu(items, w0);
                if (grp.Kind == GroupKind.Desktop)
                {
                    B["w.pindesk"] = () =>
                    {
                        if (wb.DeskPin) Menus.Item(items, "Unpin from this desktop", null, () => Unpin(wb.Pin));
                        else if (canLaunch) Menus.Item(items, "Pin to this desktop", null, () => PinToDesktop(w0, grp.Desktop));
                    };
                    B["w.movedesk"] = () =>
                    {
                        var sub = Menus.Item(items, "Move to desktop", null, null);
                        foreach (var d in desktops.List.Where(d => d.Id != grp.Desktop.Id))
                        {
                            var dd = d;
                            var target = groups.FirstOrDefault(x => x.Kind == GroupKind.Desktop && x.Desktop.Id == dd.Id)
                                         ?? new GroupModel { Kind = GroupKind.Desktop, Desktop = dd };
                            Menus.Item(sub.DropDownItems, DeskTitle(dd), null, () => MoveWindowsTo(wb, target));
                        }
                        if (sub.DropDownItems.Count == 0) sub.Enabled = false;
                    };
                    if (!grp.IsCurrent) B["w.switchdesk"] = () => Menus.Item(items, "Go to " + DeskTitle(grp.Desktop), null, () => SwitchDesktop(grp.Desktop.Id, w0.Hwnd));
                }
                B["w.customize"] = () => Menus.Item(items, "Customize…", null, () => OpenSettings("app:" + w0.GroupKey));
                B["w.minimize"] = () =>
                {
                    bool allMin = wb.Windows.All(w => Native.IsIconic(w.Hwnd));
                    Menus.Item(items, allMin ? "Restore" : "Minimize", null, () => { foreach (var w in wb.Windows) Native.ShowWindowAsync(w.Hwnd, allMin ? Native.SW_RESTORE : Native.SW_MINIMIZE); });
                };
                B["w.maximize"] = () => Menus.Item(items, "Maximize", null, () => { Native.ShowWindowAsync(w0.Hwnd, 3); Activate(w0); });
                B["w.endtask"] = () => Menus.Item(items, "End task", null, () =>
                {
                    if (MessageBox.Show("End " + Icons.FriendlyName(w0) + "? Anything you haven't saved in it will be lost.", "CL-Taskbar",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                    foreach (var pid in wb.Windows.Select(w => w.Pid).Distinct())
                        try { System.Diagnostics.Process.GetProcessById((int)pid).Kill(); } catch { }
                });
                B["w.close"] = () =>
                {
                    if (wb.Windows.Count == 1) Menus.Item(items, "Close window", null, () => CloseWindow(w0.Hwnd));
                    else Menus.Item(items, "Close all windows", null, () => { foreach (var w in wb.Windows) CloseWindow(w.Hwnd); });
                };
                Emit(items, "Open apps", B);
            }
            else if (target is GroupModel g && g.Kind == GroupKind.Desktop)
            {
                var d = g.Desktop;
                if (!g.IsCurrent) B["d.switch"] = () => Menus.Item(items, "Switch to this desktop", null, () => SwitchDesktop(d.Id, IntPtr.Zero), true);
                if (g.Buttons.Any(x => x.DeskPin && x.Windows.Count == 0))
                    B["d.openall"] = () => Menus.Item(items, "Open all apps pinned to this desktop", null, () => OpenAllOnDesktop(d), true);
                B["d.customize"] = () => Menus.Item(items, "Customize this desktop…", null, () => OpenSettings("desk:" + d.Id.ToString("N")));
                if (g.Buttons.Any(x => x.Windows.Count > 0))
                {
                    B["d.minimizeall"] = () => Menus.Item(items, "Minimize all windows on this desktop", null, () => { foreach (var bb in g.Buttons) foreach (var w in bb.Windows) Native.ShowWindowAsync(w.Hwnd, Native.SW_MINIMIZE); });
                    B["d.closeall"] = () => Menus.Item(items, "Close all windows on this desktop", null, () => { foreach (var bb in g.Buttons) foreach (var w in bb.Windows) CloseWindow(w.Hwnd); });
                }
                Emit(items, "Desktop numbers", B);
                Menus.Sep(items);
                AddBarItems(items);
            }
            else AddBarItems(items);

            // tidy up divider lines left over from options you turned off
            while (items.Count > 0 && items[items.Count - 1] is ToolStripSeparator) items.RemoveAt(items.Count - 1);
            while (items.Count > 0 && items[0] is ToolStripSeparator) items.RemoveAt(0);
            if (items.Count == 0) AddBarItems(items);
            Menus.StyleTree(menu.Items, theme, scale);
            if (cfg.NarrowWindowList && titleItems.Count > 0) FitTitles(menu, titleItems);
            ShowMenuAbove(menu, target, false, "ctx:" + ((target as BarButton)?.Key ?? ""));
        }

        string DeskTitle(DesktopInfo d)
        {
            var ds = cfg.FindDesktop(d.Id);
            return !string.IsNullOrWhiteSpace(ds?.Label) ? ds.Label : d.Name;
        }

        void AddBarItems(ToolStripItemCollection items)
        {
            var B = new Dictionary<string, Action>
            {
                ["b.settings"] = () => Menus.Item(items, "CL-Taskbar settings", null, () => OpenSettings(null)),
                ["b.wintaskbar"] = () => Menus.Item(items, "Windows taskbar settings", null, () => Launch("ms-settings:taskbar")),
                ["b.taskmgr"] = () => Menus.Item(items, "Task Manager", null, () => Launch("taskmgr.exe")),
                ["b.showdesktop"] = () => Menus.Item(items, "Show desktop", null, ToggleDesktop),
                ["b.exit"] = () => Menus.Item(items, "Exit CL-Taskbar", null, ExitApp),
            };
            Emit(items, "Empty space on the bar", B);
        }

        static void ToggleDesktop()
        {
            try
            {
                var t = Type.GetTypeFromProgID("Shell.Application");
                var shell = Activator.CreateInstance(t);
                t.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod, null, shell, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shell);
            }
            catch { }
        }

        public void OpenSettings(object select)
        {
            if (settingsForm == null || settingsForm.IsDisposed)
            {
                settingsForm = new SettingsForm(this);
                TrackSettingsWindow(settingsForm);
                settingsForm.FormClosed += (s, e) => { settingsForm = null; saveTimer.Stop(); cfg.Save(); };
                settingsForm.Show();
            }
            settingsForm.WindowState = FormWindowState.Normal;
            settingsForm.Activate();
            Native.SetForegroundWindow(settingsForm.Handle);
            if (select != null) settingsForm.SelectObject(select);
        }

        // Rebuild the settings window in place (after switching it between dark and light)
        public void ReopenSettings(Rectangle bounds, string page)
        {
            var old = settingsForm;
            cfg.Save();
            settingsForm = new SettingsForm(this);
            TrackSettingsWindow(settingsForm);
            settingsForm.FormClosed += (s, e) => { settingsForm = null; saveTimer.Stop(); cfg.Save(); };
            settingsForm.Bounds = bounds;
            settingsForm.StartPage = page;
            settingsForm.Show();
            if (old != null && !old.IsDisposed)
            {
                // the old window's close handler must not clear the new one
                old.Hide();
                old.BeginInvoke((Action)(() => { var keep = settingsForm; old.Close(); settingsForm = keep; }));
            }
            settingsForm.Activate();
        }

        // Let the settings window appear on the bar while it's open
        void TrackSettingsWindow(Form f)
        {
            // (the settings window creates its handle while it's being built, so it may already exist here)
            void Set() { if (!f.IsDisposed && f.IsHandleCreated) { WindowTracker.OwnSettingsWindow = f.Handle; dirty = true; } }
            Set();
            f.HandleCreated += (s, e) => Set();
            f.Shown += (s, e) => Set();
            f.Activated += (s, e) => { if (WindowTracker.OwnSettingsWindow != f.Handle) Set(); };
            f.FormClosed += (s, e) => { if (WindowTracker.OwnSettingsWindow == f.Handle) WindowTracker.OwnSettingsWindow = IntPtr.Zero; dirty = true; };
        }

        void ExitApp()
        {
            preview.HidePopup();
            settingsForm?.Close();
            if (trayIcon != null) { trayIcon.Visible = false; trayIcon.Dispose(); trayIcon = null; }
            Close();
        }

        void SetupTrayIcon()
        {
            var menu = new ContextMenuStrip();
            menu.Items.Add("CL-Taskbar settings", null, (s, e) => OpenSettings(null));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (s, e) => ExitApp());
            Icon ico;
            try { ico = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { ico = SystemIcons.Application; }
            trayIcon = new NotifyIcon { Icon = ico, Text = "CL-Taskbar", ContextMenuStrip = menu, Visible = cfg.ShowTrayIcon };
            trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) OpenSettings(null); };
        }

        public List<AppWindow> OpenAppsForPicker()
        {
            // one entry per app, for "Pin a running app" in settings
            return lastWindows.GroupBy(w => w.GroupKey).Select(g => g.First())
                .Where(w => IsPackaged(w) || (w.ExePath != null && !WindowTracker.IsHostExe(w.ExePath))).ToList();
        }

        public static string PinTarget(AppWindow w) => PinTargetFor(w);
    }
}
