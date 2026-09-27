using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CLTaskbar
{
    // The settings window. Every change applies to the bar right away and is saved automatically.
    internal partial class SettingsForm : Form
    {
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool RedrawWindow(IntPtr h, IntPtr rect, IntPtr rgn, uint flags);

        // Freeze drawing while a page is rebuilt, then show it in one go (no half-built flash)
        public static bool WindowsIsDark() =>
            ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0;

        void Freeze(Control c)
        {
            if (c.IsHandleCreated) try { SendMessage(c.Handle, 0x000B, IntPtr.Zero, IntPtr.Zero); } catch { }   // WM_SETREDRAW off
        }
        void Thaw(Control c)
        {
            if (!c.IsHandleCreated) return;
            try
            {
                SendMessage(c.Handle, 0x000B, (IntPtr)1, IntPtr.Zero);
                RedrawWindow(c.Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100 | 0x0400);   // INVALIDATE | ERASE | ALLCHILDREN | UPDATENOW | FRAME
            }
            catch { c.Invalidate(true); }
        }

        readonly BarForm bar;
        Config cfg => bar.Settings;
        readonly float s;
        readonly bool dark;
        readonly Color cBg, cSurface, cText, cDim, cBorder, cInput, cHover, cAccent;
        readonly Font fBody, fTitle, fSection, fSmall;

        readonly Panel header = new Panel();
        readonly Panel content = new Panel();
        readonly List<Control> tabs = new List<Control>();
        string page = "layout";
        public string StartPage { set { page = value; } }
        readonly Timer applyTimer = new Timer { Interval = 250 };
        object pendingSelect;
        readonly ToolTip tips = new ToolTip();

        TreeView tree;
        Panel props;
        ImageList treeIcons;
        bool building;

        public SettingsForm(BarForm owner)
        {
            bar = owner;
            using (var g0 = Graphics.FromHwnd(IntPtr.Zero)) s = g0.DpiX / 96f;   // opens on the main screen
            dark = cfg.SettingsTheme == "dark" ? true : cfg.SettingsTheme == "light" ? false : WindowsIsDark();
            cAccent = AccentColor();
            if (dark)
            {
                cBg = Color.FromArgb(32, 32, 32); cSurface = Color.FromArgb(43, 43, 43);
                cText = Color.White; cDim = Color.FromArgb(190, 190, 190); cBorder = Color.FromArgb(62, 62, 62);
                cInput = Color.FromArgb(52, 52, 52); cHover = Color.FromArgb(60, 60, 60);
            }
            else
            {
                cBg = Color.FromArgb(243, 243, 243); cSurface = Color.FromArgb(251, 251, 251);
                cText = Color.FromArgb(27, 27, 27); cDim = Color.FromArgb(100, 100, 100); cBorder = Color.FromArgb(222, 222, 222);
                cInput = Color.White; cHover = Color.FromArgb(232, 232, 232);
            }
            fBody = new Font("Segoe UI", 9f);
            fSmall = new Font("Segoe UI", 8.25f);
            fSection = new Font("Segoe UI Semibold", 9.75f);
            fTitle = new Font("Segoe UI Semibold", 13f);

            Text = "CL-Taskbar settings";
            AutoScaleMode = AutoScaleMode.None;
            Font = fBody;
            BackColor = cBg;
            ForeColor = cText;
            StartPosition = FormStartPosition.Manual;
            var work = Screen.PrimaryScreen.WorkingArea;
            var size = new Size(Math.Min(P(985), (int)(work.Width * 0.95)), Math.Min(P(920), (int)(work.Height * 0.95)));
            Bounds = new Rectangle(work.Left + (work.Width - size.Width) / 2, work.Top + (work.Height - size.Height) / 2, size.Width, size.Height);
            MinimumSize = new Size(P(820), P(520));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            header.Dock = DockStyle.Top; header.Height = P(46); header.BackColor = cBg;
            header.Paint += (s2, e2) => { using (var p = new Pen(cBorder)) e2.Graphics.DrawLine(p, 0, header.Height - 1, header.Width, header.Height - 1); };
            content.Dock = DockStyle.Fill; content.BackColor = cBg; content.Padding = new Padding(P(16), P(12), P(16), P(16));
            Controls.Add(content);
            Controls.Add(header);
            int x = P(12);
            foreach (var t in new[] { new[] { "layout", "Your taskbar" }, new[] { "appearance", "General" }, new[] { "menus", "Right-click menus" }, new[] { "tips", "Tips" } })
            {
                var key = t[0];
                var tab = new TabItem(this, t[1]) { Tag = key, Top = P(6), Left = x, Height = P(40) };
                using (var g = CreateGraphics()) tab.Width = TextRenderer.MeasureText(g, t[1], fSection, Size.Empty, TextFormatFlags.NoPrefix).Width + P(28);
                tab.Click += (s2, e2) => ShowPage(key);
                header.Controls.Add(tab);
                tabs.Add(tab);
                x += tab.Width + P(4);
            }
            applyTimer.Tick += (s2, e2) => { applyTimer.Stop(); bar.ApplySettings(); };
            KeyPreview = true;
        }

        int P(float v) => (int)Math.Round(v * s);

        static int ReadDword(string path, string name, int def)
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(path)) return k?.GetValue(name) is int v ? v : def; } catch { return def; }
        }

        static Color AccentColor()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\DWM"))
                    if (k?.GetValue("AccentColor") is int v) return Color.FromArgb(v & 255, (v >> 8) & 255, (v >> 16) & 255);
            }
            catch { }
            return Color.FromArgb(0, 120, 212);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try
            {
                if (dark)
                {
                    int on = 1;
                    if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(Handle, 19, ref on, 4);
                }
            }
            catch { }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            WarmPresetIcons();
            if (tree == null && props == null) ShowPage(page);
            if (pendingSelect != null) { var o = pendingSelect; pendingSelect = null; SelectObject(o, false); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (applyTimer.Enabled) { applyTimer.Stop(); bar.ApplySettings(true); }
            base.OnFormClosing(e);
        }

        class TabItem : Control
        {
            readonly SettingsForm f; bool hot;
            public TabItem(SettingsForm form, string text) { f = form; Text = text; DoubleBuffered = true; Cursor = Cursors.Hand; Font = form.fSection; }
            bool Selected => Equals(Tag, f.page);
            protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); }
            protected override void OnMouseLeave(EventArgs e) { hot = false; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics;
                g.Clear(f.cBg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (hot && !Selected)
                    using (var b = new SolidBrush(f.cHover)) using (var p = BarForm.RoundRect(new Rectangle(0, f.P(2), Width - 1, Height - f.P(10)), f.P(5))) g.FillPath(b, p);
                TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - f.P(6)), Selected ? f.cText : f.cDim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                if (Selected)
                    using (var b = new SolidBrush(f.cAccent))
                    using (var p = BarForm.RoundRect(new Rectangle(f.P(12), Height - f.P(4), Width - f.P(24), f.P(3)), f.P(1))) g.FillPath(b, p);
            }
        }

        void ShowPage(string key)
        {
            page = key;
            foreach (var t in tabs) t.Invalidate();
            Freeze(content);
            try
            {
                content.SuspendLayout();
                foreach (Control c in content.Controls.Cast<Control>().ToList()) { content.Controls.Remove(c); c.Dispose(); }
                tree = null; props = null;
                if (key == "layout") BuildLayoutPage(); else if (key == "tips") BuildTipsPage(); else if (key == "menus") BuildMenusPage(); else BuildAppearancePage();
                content.ResumeLayout();
            }
            finally { Thaw(content); }
        }

        void Changed(bool rebuildTree = false)
        {
            if (building) return;
            if (rebuildTree) { applyTimer.Stop(); bar.ApplySettings(); ReloadTree(); return; }
            applyTimer.Stop(); applyTimer.Start();
            var n = tree?.SelectedNode;
            if (n != null) n.Text = NodeText(n.Tag);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Delete && tree != null && tree.Focused) { RemoveSelected(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        // ============================================================
        // Taskbar layout page
        // ============================================================

        class AppNode
        {
            public string Key;
            public AppWindow Sample;
        }

        class DesktopNode { public DesktopInfo D; }
        class BufferedPanel : Panel { public BufferedPanel() { DoubleBuffered = true; } }
        class ClosedAppsNode { }

        void BuildLayoutPage()
        {
            var body = new Panel { Dock = DockStyle.Fill, BackColor = cBg };
            content.Controls.Add(body);
            var hint = new Label { Dock = DockStyle.Top, Height = P(26), ForeColor = cDim, Font = fBody, Text = "Drag things to rearrange them, into or out of groups. Right-click for more options. Changes apply right away." };
            content.Controls.Add(hint);

            var left = new Panel { Dock = DockStyle.Left, Width = P(300), BackColor = cBg, Padding = new Padding(0, 0, P(12), 0) };
            var right = new Panel { Dock = DockStyle.Fill, BackColor = cBg };
            body.Controls.Add(right);
            body.Controls.Add(left);

            treeIcons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(P(16), P(16)) };
            tree = new TreeView
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = cSurface, ForeColor = cText, Font = fBody,
                HideSelection = false, FullRowSelect = true, ShowLines = false, ItemHeight = P(26), ImageList = treeIcons,
                AllowDrop = true, Indent = P(18)
            };
            tree.HandleCreated += (s2, e2) =>
            {
                try
                {
                    SetWindowTheme(tree.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
                    SendMessage(tree.Handle, 0x1125 /*TVM_SETINSERTMARKCOLOR*/, IntPtr.Zero, (IntPtr)(cAccent.R | cAccent.G << 8 | cAccent.B << 16));
                }
                catch { }
            };
            tree.AfterSelect += (s2, e2) => BuildProps();
            tree.NodeMouseClick += (s2, e2) => { if (e2.Button == MouseButtons.Right) ShowItemMenu(e2.Node, e2.Location); };
            tree.MouseUp += (s2, e2) =>
            {
                if (e2.Button == MouseButtons.Right && tree.GetNodeAt(e2.Location) == null) ShowTaskbarAddMenu(tree, e2.Location);
            };
            tree.ItemDrag += (s2, e2) =>
            {
                var n = e2.Item as TreeNode;
                if (n == null || n.Tag == null || n.Tag is DesktopNode || n.Tag is ClosedAppsNode) return;
                if (e2.Button != MouseButtons.Left) return;
                tree.SelectedNode = n;
                tree.DoDragDrop(n, DragDropEffects.Move);
                ClearDropMarks();
            };
            tree.GiveFeedback += (s2, e2) => { e2.UseDefaultCursors = false; Cursor.Current = e2.Effect == DragDropEffects.None ? Cursors.No : Cursors.Hand; };
            tree.DragOver += TreeDragOver;
            tree.DragDrop += TreeDragDrop;
            tree.DragLeave += (s2, e2) => ClearDropMarks();

            var treeBox = new Panel { Dock = DockStyle.Fill, BackColor = cBorder, Padding = new Padding(1) };
            treeBox.Controls.Add(tree);

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = P(42), BackColor = cBg, Padding = new Padding(0, P(8), 0, 0), WrapContents = false };
            var add = MakeButton("Add  ▴", true);
            add.Click += (s2, e2) => ShowTaskbarAddMenu(add, new Point(0, -P(2)));   // opens upward, like the taskbar's menus
            var remove = MakeButton("Remove");
            remove.Click += (s2, e2) => RemoveSelected();
            buttons.Controls.AddRange(new Control[] { add, remove });

            left.Controls.Add(treeBox);
            left.Controls.Add(buttons);

            props = NewPropsPanel();
            var propsBox = new Panel { Dock = DockStyle.Fill, BackColor = cBorder, Padding = new Padding(1) };
            propsBox.Controls.Add(props);
            right.Controls.Add(propsBox);

            ReloadTree();
            if (tree.Nodes.Count > 0 && tree.SelectedNode == null) tree.SelectedNode = tree.Nodes[0];
        }

        Panel NewPropsPanel()
        {
            var p = new BufferedPanel { Dock = DockStyle.Fill, BackColor = cSurface, AutoScroll = true };
            p.HandleCreated += (s2, e2) => { try { if (dark) SetWindowTheme(p.Handle, "DarkMode_Explorer", null); } catch { } };
            return p;
        }

        public void ReloadTree()
        {
            if (tree == null || page != "layout") return;
            object sel = tree.SelectedNode?.Tag;
            string selApp = (sel as AppNode)?.Key;
            string selDesk = (sel as DesktopNode)?.D.Id.ToString("N");
            building = true;
            tree.BeginUpdate();
            tree.Nodes.Clear();
            treeIcons.Images.Clear();
            foreach (var sec in cfg.Layout)
            {
                var n = new TreeNode(NodeText(sec)) { Tag = sec };
                SetNodeIcon(n, SectionIcon(sec));
                if (sec.Type == "pins" || sec.Type == "menu") { AddItemNodes(n.Nodes, sec.Items, sec); n.Expand(); }
                if (sec.Type == "windows") { AddDesktopNodes(n, sec); n.Expand(); }
                tree.Nodes.Add(n);
            }
            tree.EndUpdate();
            building = false;
            if (selDesk != null) SelectObject("desk:" + selDesk, false);
            else if (selApp != null) SelectObject("app:" + selApp, false);
            else if (sel != null) SelectObject(sel, false);
            if (tree.SelectedNode == null) BuildProps();
        }

        void AddItemNodes(TreeNodeCollection into, List<LaunchItem> items, BarSection sec)
        {
            foreach (var it in items)
            {
                var n = new TreeNode(NodeText(it)) { Tag = it };
                SetNodeIcon(n, it.Type == "separator" ? null : it.Type == "folder" && string.IsNullOrWhiteSpace(it.IconPath) ? Launcher.IconForTarget("shell:Personal", P(16)) : Launcher.IconFor(it, P(16), sec.Browser));
                if (it.Type == "folder") { AddItemNodes(n.Nodes, it.Children, sec); n.Expand(); }
                into.Add(n);
            }
        }

        // Each virtual desktop, with its pinned apps and open apps in the same order as on the taskbar
        void AddDesktopNodes(TreeNode parent, BarSection sec)
        {
            var shownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (sec.GroupByDesktop)
            {
                foreach (var d in bar.DesktopList())
                {
                    var dn = new TreeNode(NodeText(new DesktopNode { D = d })) { Tag = new DesktopNode { D = d } };
                    SetNodeIcon(dn, null);
                    foreach (var b in bar.ButtonsForDesktop(d.Id)) AddButtonNode(dn.Nodes, b, shownKeys);
                    // pins of a desktop that isn't shown on the bar right now
                    if (dn.Nodes.Count == 0)
                        foreach (var p in cfg.FindDesktop(d.Id)?.Pins ?? new List<LaunchItem>())
                        {
                            var pn = new TreeNode(ItemText(p)) { Tag = p };
                            SetNodeIcon(pn, Launcher.IconFor(p, P(16)));
                            dn.Nodes.Add(pn);
                        }
                    parent.Nodes.Add(dn);
                    dn.Expand();
                }
            }
            else
                foreach (var b in bar.FlatButtons()) AddButtonNode(parent.Nodes, b, shownKeys);

            // Apps you've customized that aren't open right now
            var closed = cfg.Apps.Where(a => !shownKeys.Contains(a.Key)).ToList();
            if (closed.Count > 0)
            {
                var cn = new TreeNode("Customized apps (not open)") { Tag = new ClosedAppsNode() };
                foreach (var a in closed.OrderBy(x => string.IsNullOrWhiteSpace(x.Name) ? x.AppName : x.Name))
                {
                    var an = new AppNode { Key = a.Key };
                    var n = new TreeNode(NodeText(an)) { Tag = an };
                    SetNodeIcon(n, !string.IsNullOrWhiteSpace(a.IconPath) ? Launcher.FromIconFile(Launcher.Expand(a.IconPath), P(16)) : Launcher.IconForTarget(a.ExePath, P(16)));
                    cn.Nodes.Add(n);
                }
                parent.Nodes.Add(cn);
            }
        }

        void AddButtonNode(TreeNodeCollection into, BarButton b, HashSet<string> shownKeys)
        {
            if (b.DeskPin)
            {
                var pn = new TreeNode(ItemText(b.Pin) + (b.Windows.Count > 0 ? "  (open)" : "")) { Tag = b.Pin };
                SetNodeIcon(pn, Launcher.IconFor(b.Pin, P(16)));
                into.Add(pn);
                foreach (var w in b.Windows) shownKeys.Add(w.GroupKey);
                return;
            }
            if (b.Windows.Count == 0) return;
            var w0 = b.Windows[0];
            if (!shownKeys.Add(w0.GroupKey) && into.Cast<TreeNode>().Any(x => x.Tag is AppNode a && a.Key == w0.GroupKey)) return;
            var an = new AppNode { Key = w0.GroupKey, Sample = w0 };
            var n = new TreeNode(NodeText(an)) { Tag = an };
            SetNodeIcon(n, Icons.ForWindow(w0, P(16)));
            into.Add(n);
        }

        void SetNodeIcon(TreeNode n, Image img)
        {
            if (img == null) { n.ImageIndex = n.SelectedImageIndex = -1; return; }
            treeIcons.Images.Add(new Bitmap(img, treeIcons.ImageSize));
            n.ImageIndex = n.SelectedImageIndex = treeIcons.Images.Count - 1;
        }

        Bitmap Chevron(int px)
        {
            var b = new Bitmap(px, px);
            using (var g = Graphics.FromImage(b))
            using (var pen = new Pen(cText, Math.Max(1.5f, px / 12f)) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round, LineJoin = System.Drawing.Drawing2D.LineJoin.Round })
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                float cx = px / 2f, cy = px / 2f, w = px * 0.28f, h = px * 0.16f;
                g.DrawLines(pen, new[] { new PointF(cx - w, cy + h), new PointF(cx, cy - h), new PointF(cx + w, cy + h) });
            }
            return b;
        }

        Image SectionIcon(BarSection sec)
        {
            switch (sec.Type)
            {
                case "pins": return Launcher.IconForTarget("shell:::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", P(16));
                case "item": return sec.Items.Count > 0 ? Launcher.IconFor(sec.Items[0], P(16), sec.Browser) : null;
                case "menu":
                    if (!string.IsNullOrWhiteSpace(sec.IconPath)) return Launcher.FromIconFile(Launcher.Expand(sec.IconPath), P(16));
                    return Chevron(P(16));   // same ^ arrow the menu shows on the taskbar
                case "windows": return Launcher.IconForTarget("taskmgr.exe", P(16));
                default: return null;
            }
        }

        string NodeText(object tag)
        {
            if (tag is BarSection s)
            {
                switch (s.Type)
                {
                    case "pins": return string.IsNullOrWhiteSpace(s.Name) ? "Icon Group" : s.Name;
                    case "menu": return string.IsNullOrWhiteSpace(s.Name) ? "Menu" : s.Name;
                    case "item": return s.Items.Count > 0 ? ItemText(s.Items[0]) : "(empty)";
                    case "windows": return s.GroupByDesktop ? "Virtual desktops" : "Open apps";
                    case "gap": return "Space (" + s.Width + " px)" + (s.Divider ? " with line" : "");
                }
            }
            if (tag is LaunchItem it) return ItemText(it);
            if (tag is DesktopNode dn)
            {
                var ds = cfg.FindDesktop(dn.D.Id);
                return dn.D.Name + (!string.IsNullOrWhiteSpace(ds?.Label) ? "  (\"" + ds.Label + "\")" : "");
            }
            if (tag is AppNode an)
            {
                var st = cfg.FindApp(an.Key);
                string name = !string.IsNullOrWhiteSpace(st?.Name) ? st.Name
                            : an.Sample != null ? Icons.RealName(an.Sample)
                            : !string.IsNullOrWhiteSpace(st?.AppName) ? st.AppName : an.Key;
                return name + (st != null && !st.IsEmpty ? "  •" : "");
            }
            return "";
        }

        static string ItemText(LaunchItem it)
        {
            switch (it.Type)
            {
                case "separator": return "────────";
                case "folder": return (string.IsNullOrWhiteSpace(it.Name) ? "Sub-menu" : it.Name) + "  ›";
                case "web": return string.IsNullOrWhiteSpace(it.Name) ? (Launcher.HostOf(it.Target) ?? "Web link") : it.Name;
                default:
                    if (!string.IsNullOrWhiteSpace(it.Name)) return it.Name;
                    string n = Path.GetFileNameWithoutExtension(Launcher.Expand(it.Target));
                    return string.IsNullOrEmpty(n) ? "New item" : n;
            }
        }

        public void SelectObject(object o, bool switchPage = true)
        {
            if (switchPage && page != "layout" && IsHandleCreated && Visible) ShowPage("layout");
            if (tree == null) { pendingSelect = o; page = "layout"; return; }
            TreeNode n = o is string key && key.StartsWith("app:") ? FindApp(tree.Nodes, key.Substring(4))
                       : o is string dk && dk.StartsWith("desk:") ? FindDesk(tree.Nodes, dk.Substring(5))
                       : FindNode(tree.Nodes, o);
            if (n != null) { tree.SelectedNode = n; n.EnsureVisible(); }
        }

        static TreeNode FindNode(TreeNodeCollection nodes, object tag)
        {
            foreach (TreeNode n in nodes)
            {
                if (ReferenceEquals(n.Tag, tag)) return n;
                var c = FindNode(n.Nodes, tag);
                if (c != null) return c;
            }
            return null;
        }

        static TreeNode FindDesk(TreeNodeCollection nodes, string id)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is DesktopNode d && d.D.Id.ToString("N") == id) return n;
                var c = FindDesk(n.Nodes, id);
                if (c != null) return c;
            }
            return null;
        }

        static TreeNode FindApp(TreeNodeCollection nodes, string key)
        {
            foreach (TreeNode n in nodes)
            {
                if (n.Tag is AppNode an && string.Equals(an.Key, key, StringComparison.OrdinalIgnoreCase)) return n;
                var c = FindApp(n.Nodes, key);
                if (c != null) return c;
            }
            return null;
        }

        static bool IsContainer(object tag) =>
            (tag is BarSection s && (s.Type == "pins" || s.Type == "menu")) || (tag is LaunchItem it && it.Type == "folder") || tag is DesktopNode;

        List<LaunchItem> ListOf(object container) =>
            container is BarSection s ? s.Items : container is LaunchItem f ? f.Children : container is DesktopNode d ? cfg.DesktopFor(d.D.Id).Pins : null;

        static string ContainerName(object c) =>
            c is BarSection s ? (string.IsNullOrWhiteSpace(s.Name) ? (s.Type == "menu" ? "this menu" : "this group") : s.Name)
            : c is LaunchItem f ? (string.IsNullOrWhiteSpace(f.Name) ? "this sub-menu" : f.Name)
            : c is DesktopNode d ? d.D.Name : "";

        // The group / menu / sub-menu a node belongs to (or is)
        object ContainerContext(TreeNode n)
        {
            if (n == null) return null;
            if (IsContainer(n.Tag)) return n.Tag;
            if (n.Parent != null && IsContainer(n.Parent.Tag)) return n.Parent.Tag;
            return null;
        }

        BarSection SectionOf(TreeNode n)
        {
            while (n != null && !(n.Tag is BarSection)) n = n.Parent;
            return n?.Tag as BarSection;
        }

        // ---- moving things between the taskbar and groups ----

        static BarSection ToSection(LaunchItem it)
        {
            if (it == null) return null;
            if (it.Type == "program" || it.Type == "web")
                return new BarSection { Type = "item", Name = it.Name, Items = { it } };
            if (it.Type == "folder")
                return new BarSection { Type = "menu", Name = it.Name, Label = it.Name, IconPath = it.IconPath, Items = it.Children ?? new List<LaunchItem>() };
            return null;
        }

        static LaunchItem ToItem(BarSection s)
        {
            if (s.Type == "item") return s.Items.FirstOrDefault();
            if (s.Type == "menu") return new LaunchItem { Type = "folder", Name = s.Name, IconPath = s.IconPath, Children = s.Items };
            return null;
        }

        static bool Fits(object container, LaunchItem it)
        {
            if (it == null) return false;
            if ((container is BarSection s && s.Type == "pins") || container is DesktopNode) return it.Type == "program" || it.Type == "web";
            return true;
        }

        LaunchItem ItemFromApp(AppNode an)
        {
            var st = cfg.FindApp(an.Key);
            string target = an.Sample != null ? BarForm.PinTarget(an.Sample) : st?.ExePath;
            if (string.IsNullOrEmpty(target)) return null;
            string name = !string.IsNullOrWhiteSpace(st?.Name) ? st.Name : an.Sample != null ? Icons.RealName(an.Sample) : st?.AppName ?? "App";
            return new LaunchItem { Type = "program", Name = name, Target = target, IconPath = st?.IconPath ?? "", BackColor = st?.BackColor ?? "" };
        }

        // ---- drag & drop in the list ----

        enum DropPos { Before, After, Into }
        TreeNode dropNode; DropPos dropPos; bool dropEnd;

        void ClearDropMarks()
        {
            if (tree == null || !tree.IsHandleCreated) return;
            try
            {
                SendMessage(tree.Handle, 0x111A /*TVM_SETINSERTMARK*/, IntPtr.Zero, IntPtr.Zero);
                SendMessage(tree.Handle, 0x110B /*TVM_SELECTITEM*/, (IntPtr)8 /*TVGN_DROPHILITE*/, IntPtr.Zero);
            }
            catch { }
        }

        void TreeDragOver(object sender, DragEventArgs e)
        {
            var src = e.Data.GetData(typeof(TreeNode)) as TreeNode;
            var pt = tree.PointToClient(new Point(e.X, e.Y));
            // scroll while dragging near the edges
            if (pt.Y < P(20)) SendMessage(tree.Handle, 0x115 /*WM_VSCROLL*/, IntPtr.Zero, IntPtr.Zero);
            else if (pt.Y > tree.ClientSize.Height - P(20)) SendMessage(tree.Handle, 0x115, (IntPtr)1, IntPtr.Zero);

            ClearDropMarks();
            dropNode = null; dropEnd = false;
            e.Effect = DragDropEffects.None;
            if (src == null) return;

            var n = tree.GetNodeAt(pt);
            if (n == null)
            {
                // below everything: the end of the taskbar
                if (CanDropTopLevel(src))
                {
                    dropEnd = true;
                    e.Effect = DragDropEffects.Move;
                    if (tree.Nodes.Count > 0) SendMessage(tree.Handle, 0x111A, (IntPtr)1, tree.Nodes[tree.Nodes.Count - 1].Handle);
                }
                return;
            }
            for (var p = n; p != null; p = p.Parent) if (p == src) return;   // not into itself

            var b = n.Bounds;
            double rel = (pt.Y - b.Top) / (double)Math.Max(1, b.Height);
            DropPos pos = IsContainer(n.Tag) && rel > 0.28 && rel < 0.72 ? DropPos.Into : rel < 0.5 ? DropPos.Before : DropPos.After;
            if (pos == DropPos.After && IsContainer(n.Tag) && n.IsExpanded && n.Nodes.Count > 0) pos = DropPos.Into;
            if (!CanDrop(src, n, pos))
            {
                if (pos == DropPos.Into) pos = rel < 0.5 ? DropPos.Before : DropPos.After;
                if (!CanDrop(src, n, pos)) return;
            }
            dropNode = n; dropPos = pos;
            e.Effect = DragDropEffects.Move;
            if (pos == DropPos.Into) SendMessage(tree.Handle, 0x110B, (IntPtr)8, n.Handle);
            else SendMessage(tree.Handle, 0x111A, (IntPtr)(pos == DropPos.After ? 1 : 0), n.Handle);
        }

        bool CanDropTopLevel(TreeNode src)
        {
            if (src.Tag is BarSection) return true;
            if (src.Tag is LaunchItem it) return it.Type == "program" || it.Type == "web" || it.Type == "folder";
            if (src.Tag is AppNode an) return ItemFromApp(an) != null;
            return false;
        }

        // What the dragged thing would become inside a group or menu
        LaunchItem PayloadItem(TreeNode src)
        {
            if (src.Tag is LaunchItem it) return it;
            if (src.Tag is BarSection s)
            {
                if (s.Type == "item") return s.Items.FirstOrDefault();
                if (s.Type == "menu") return new LaunchItem { Type = "folder" };
                return null;
            }
            if (src.Tag is AppNode an) return ItemFromApp(an);
            return null;
        }

        bool CanDrop(TreeNode src, TreeNode n, DropPos pos)
        {
            if (n.Tag is AppNode || n.Tag is ClosedAppsNode) return false;
            if (pos == DropPos.Into) return IsContainer(n.Tag) && Fits(n.Tag, PayloadItem(src));
            if (n.Parent == null) return CanDropTopLevel(src);
            if (!IsContainer(n.Parent.Tag)) return false;
            return Fits(n.Parent.Tag, PayloadItem(src));
        }

        void TreeDragDrop(object sender, DragEventArgs e)
        {
            var src = e.Data.GetData(typeof(TreeNode)) as TreeNode;
            ClearDropMarks();
            if (src == null || (dropNode == null && !dropEnd)) return;

            object destContainer = null; object refObj = null; bool after = false;
            if (!dropEnd)
            {
                if (dropPos == DropPos.Into) destContainer = dropNode.Tag;
                else { destContainer = dropNode.Parent?.Tag; refObj = dropNode.Tag; after = dropPos == DropPos.After; }
            }

            // Take it out of where it was, and turn it into the right kind of thing for where it's going
            LaunchItem asItem = null; BarSection asSection = null;
            if (src.Tag is BarSection ss)
            {
                if (destContainer != null) { asItem = ToItem(ss); if (asItem == null) return; }
                else asSection = ss;
                cfg.Layout.Remove(ss);
            }
            else if (src.Tag is LaunchItem si)
            {
                if (destContainer == null) { asSection = ToSection(si); if (asSection == null) return; }
                else asItem = si;
                ListOf(src.Parent?.Tag)?.Remove(si);
            }
            else if (src.Tag is AppNode an)
            {
                var it = ItemFromApp(an);
                if (it == null) return;
                if (destContainer != null) asItem = it; else asSection = ToSection(it);
            }

            object moved = null;
            if (destContainer == null && asSection != null)
            {
                int idx = refObj is BarSection rs ? cfg.Layout.IndexOf(rs) + (after ? 1 : 0) : cfg.Layout.Count;
                cfg.Layout.Insert(Math.Max(0, Math.Min(cfg.Layout.Count, idx)), asSection);
                moved = asSection;
            }
            else if (destContainer != null && asItem != null)
            {
                var list = ListOf(destContainer);
                int idx = refObj is LaunchItem ri ? list.IndexOf(ri) + (after ? 1 : 0) : list.Count;
                list.Insert(Math.Max(0, Math.Min(list.Count, idx)), asItem);
                moved = asItem;
            }
            Changed(true);
            if (moved != null) SelectObject(moved, false);
        }

        // ---- add / remove ----

        Theme MenuTheme() => Theme.Build(new Config { Color = dark ? "#2B2B2B" : "#F9F9F9" }, null);

        // The Add button: things that go straight onto the taskbar, most likely ones first
        void ShowTaskbarAddMenu(Control anchor, Point at)
        {
            selectAfterAdd = true;
            var theme = MenuTheme();
            var menu = Menus.Create(theme, s);
            Menus.Item(menu.Items, "Icon Group", null, () => AddSection(new BarSection { Type = "pins", Name = "New group" }));
            Menus.Item(menu.Items, "Menu", null, () => AddSection(new BarSection { Type = "menu", Name = "New menu", Label = "Menu" }));
            Menus.Item(menu.Items, "Space", null, () => AddSection(new BarSection { Type = "gap", Width = 16 }));
            Menus.Sep(menu.Items);
            AddItemChoices(menu.Items, null, null, theme);
            if (!cfg.Layout.Any(x => x.Type == "windows"))
            {
                Menus.Sep(menu.Items);
                Menus.Item(menu.Items, "Virtual desktops (open apps)", null, () => AddSection(new BarSection { Type = "windows", Name = "Open windows" }));
            }
            Menus.StyleTree(menu.Items, theme, s);
            // from the Add button: open upward so it doesn't cover the button; elsewhere: at the mouse
            if (anchor is Button) menu.Show(anchor, at, ToolStripDropDownDirection.AboveRight);
            else menu.Show(anchor, at);
        }

        // Right-click on something in the list: things that apply to that item
        void ShowItemMenu(TreeNode n, Point at)
        {
            selectAfterAdd = false;
            var theme = MenuTheme();
            var menu = Menus.Create(theme, s);
            object tag = n.Tag;
            // show which row the menu is for, without switching the settings on the right
            try { SendMessage(tree.Handle, 0x110B, (IntPtr)8, n.Handle); } catch { }
            menu.Closed += (s2, e2) => ClearDropMarks();

            if (IsContainer(tag))
            {
                AddItemChoices(menu.Items, tag, null, theme);
            }
            else if (tag is AppNode an)
            {
                if (an.Sample != null && ItemFromApp(an) != null) Menus.Item(menu.Items, "Run as administrator", null, () => bar.RunAsAdmin(an.Sample));
                if (ItemFromApp(an) != null) AddPinMenus(menu.Items, an);
                if (cfg.FindApp(an.Key) != null) Menus.Item(menu.Items, "Reset to default", null, () => { cfg.Apps.RemoveAll(a => string.Equals(a.Key, an.Key, StringComparison.OrdinalIgnoreCase)); Changed(true); });
            }

            if (tag is LaunchItem li && li.Type == "program" && !string.IsNullOrWhiteSpace(li.Target))
            {
                Menus.Item(menu.Items, "Open it", null, () => Launcher.Run(li, this));
                Menus.Item(menu.Items, "Run as administrator", null, () =>
                    Launcher.Run(new LaunchItem { Type = "program", Name = li.Name, Target = li.Target, Arguments = li.Arguments, StartIn = li.StartIn, RunAsAdmin = true }, this));
            }
            if (!(tag is AppNode) && !(tag is DesktopNode) && !(tag is ClosedAppsNode))
            {
                if (menu.Items.Count > 0) Menus.Sep(menu.Items);
                string removeText = n.Parent?.Tag is DesktopNode pdn ? "Unpin from this desktop"
                                  : n.Parent?.Tag is BarSection ps && ps.Type == "pins" ? "Unpin from " + (string.IsNullOrWhiteSpace(ps.Name) ? "this group" : ps.Name)
                                  : "Remove";
                Menus.Item(menu.Items, removeText, null, () => RemoveNode(n));
            }
            if (menu.Items.Count == 0) { menu.Dispose(); ClearDropMarks(); return; }
            Menus.StyleTree(menu.Items, theme, s);
            menu.Show(tree, at);
        }

        // Is this app pinned in that list? (matches the real app, not just the name)
        LaunchItem FindIn(List<LaunchItem> list, AppNode an)
        {
            if (list == null) return null;
            if (an.Sample != null) return bar.ItemIn(list, an.Sample);
            var it = ItemFromApp(an);
            return it == null ? null : list.FirstOrDefault(i => string.Equals(Launcher.Expand(i.Target), Launcher.Expand(it.Target), StringComparison.OrdinalIgnoreCase));
        }

        // "Pin to group" and "Pin to desktop" submenus, built fresh each time (so renamed groups show their new names)
        void AddPinMenus(ToolStripItemCollection items, AppNode an)
        {
            var g = Menus.Item(items, "Pin to group", null, null);
            foreach (var sec in cfg.Layout.Where(x => x.Type == "pins"))
            {
                var sc = sec;
                var mi = Menus.Item(g.DropDownItems, string.IsNullOrWhiteSpace(sc.Name) ? "Group" : sc.Name, null, () =>
                {
                    var ex = FindIn(sc.Items, an);
                    if (ex != null) sc.Items.Remove(ex); else { var it = ItemFromApp(an); if (it != null) sc.Items.Add(it); }
                    Changed(true);
                });
                mi.Checked = FindIn(sc.Items, an) != null;
            }
            Menus.Sep(g.DropDownItems);
            Menus.Item(g.DropDownItems, "New group", null, () =>
            {
                var it = ItemFromApp(an); if (it == null) return;
                var sec = new BarSection { Type = "pins", Name = "New group" };
                sec.Items.Add(it);
                int at = cfg.Layout.FindIndex(x => x.Type == "windows");
                cfg.Layout.Insert(at < 0 ? cfg.Layout.Count : at, sec);
                Changed(true);
                SelectObject(sec, false);
            });
            var desks = bar.DesktopList();
            if (desks.Count > 0)
            {
                var d = Menus.Item(items, "Pin to desktop", null, null);
                foreach (var desk in desks)
                {
                    var dk = desk;
                    var ds = cfg.FindDesktop(dk.Id);
                    var mi = Menus.Item(d.DropDownItems, dk.Name + (!string.IsNullOrWhiteSpace(ds?.Label) ? "  (" + ds.Label + ")" : ""), null, () =>
                    {
                        var list = cfg.DesktopFor(dk.Id).Pins;
                        var ex = FindIn(list, an);
                        if (ex != null) { bar.KeepPlaceOnUnpin(ex); list.Remove(ex); } else { var it = ItemFromApp(an); if (it != null) list.Add(it); }
                        Changed(true);
                    });
                    mi.Checked = FindIn(ds?.Pins, an) != null;
                }
            }
        }

        void PinToTaskbar(AppNode an)
        {
            var sec = cfg.Layout.FirstOrDefault(x => x.Type == "pins");
            if (sec == null) { sec = new BarSection { Type = "pins", Name = "Pinned apps" }; cfg.Layout.Insert(0, sec); }
            var item = ItemFromApp(an);
            if (item == null) return;
            sec.Items.Add(item);
            Changed(true);
            SelectObject(item, false);
        }

        // Icons for the Windows tools list, loaded in the background so the menu opens instantly
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap> presetIcons = new System.Collections.Concurrent.ConcurrentDictionary<string, Bitmap>();
        static bool presetWarmStarted;

        void WarmPresetIcons()
        {
            if (presetWarmStarted) return;
            presetWarmStarted = true;
            int px = P(16);
            var th = new System.Threading.Thread(() =>
            {
                foreach (var p in Presets.All)
                    try { var b = Launcher.IconForTarget(p.Target, px); if (b != null) presetIcons[p.Target + "|" + px] = b; } catch { }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal };
            th.SetApartmentState(System.Threading.ApartmentState.STA);
            th.Start();
        }

        Bitmap PresetIcon(string target)
        {
            if (presetIcons.TryGetValue(target + "|" + P(16), out var b)) return b;
            return null;   // still loading; the menu shows without it rather than waiting
        }

        // A sub-menu that's only filled in when you open it
        void Lazy(ToolStripMenuItem parent, Action<ToolStripItemCollection> fill, Theme theme)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("…") { Enabled = false });
            bool filled = false;
            parent.DropDownOpening += (s2, e2) =>
            {
                if (filled) return;
                filled = true;
                parent.DropDown.SuspendLayout();
                parent.DropDownItems.Clear();
                fill(parent.DropDownItems);
                Menus.StyleTree(parent.DropDownItems, theme, s);
                parent.DropDown.ResumeLayout();
            };
        }

        // Things you can add: into ctx (a group, menu, sub-menu or desktop), or onto the taskbar when ctx is null
        void AddItemChoices(ToolStripItemCollection items, object ctx, TreeNode near, Theme theme)
        {
            Menus.Item(items, "App or file…", null, () => AddProgramFromFile(ctx));
            Menus.Item(items, "Folder…", null, () => AddFolder(ctx));
            Menus.Item(items, "Web link", null, () => AddNew(new LaunchItem { Type = "web", Name = "New link", Target = "https://" }, ctx));
            Menus.Sep(items);
            // two ways to add Windows things: pick from the list, or type it like in Win+R
            Menus.Item(items, "Pick from a list…  (tools, settings, open apps)", null, () => ShowPicker(ctx, PickerDialog.AllCat));
            Menus.Item(items, "Type a command…  (like Win+R)", null, () => AddCommand(ctx));
            if (ctx is LaunchItem || (ctx is BarSection cs && cs.Type == "menu"))
            {
                Menus.Item(items, "Sub-menu", null, () => AddNew(new LaunchItem { Type = "folder", Name = "New sub-menu" }, ctx));
                Menus.Item(items, "Separator line", null, () => AddNew(new LaunchItem { Type = "separator" }, ctx));
            }
        }

        bool selectAfterAdd = true;   // false while adding from a right-click menu: keep what you had selected

        void ShowAdded(object o)
        {
            if (selectAfterAdd) { SelectObject(o, false); return; }
            var node = FindNode(tree.Nodes, o);
            node?.EnsureVisible();
        }

        void AddSection(BarSection sec)
        {
            int idx = cfg.Layout.Count;
            var n = tree.SelectedNode;
            while (n?.Parent != null) n = n.Parent;
            if (n?.Tag is BarSection cur) idx = cfg.Layout.IndexOf(cur) + 1;
            cfg.Layout.Insert(idx, sec);
            Changed(true);
            ShowAdded(sec);
        }

        // Adds inside the given group/menu/desktop, or straight onto the taskbar when ctx is null
        void AddNew(LaunchItem it, object ctx)
        {
            if (it == null) return;
            if (ctx == null)
            {
                var sec = ToSection(it);
                if (sec != null) AddSection(sec);
                return;
            }
            if (!Fits(ctx, it)) return;
            var list = ListOf(ctx);
            list.Add(it);
            Changed(true);
            ShowAdded(it);
        }

        // Several at once (from the picker): one refresh at the end instead of one per item
        void AddMany(List<LaunchItem> items, object ctx)
        {
            if (items.Count == 1) { AddNew(items[0], ctx); return; }
            object last = null;
            foreach (var it in items)
            {
                if (ctx == null)
                {
                    var sec = ToSection(it);
                    if (sec == null) continue;
                    int idx = cfg.Layout.Count;
                    var n = tree.SelectedNode;
                    while (n?.Parent != null) n = n.Parent;
                    if (last is BarSection prev) idx = cfg.Layout.IndexOf(prev) + 1;
                    else if (n?.Tag is BarSection cur) idx = cfg.Layout.IndexOf(cur) + 1;
                    cfg.Layout.Insert(idx, sec);
                    last = sec;
                }
                else
                {
                    if (!Fits(ctx, it)) continue;
                    ListOf(ctx).Add(it);
                    last = it;
                }
            }
            if (last == null) return;
            tree.BeginUpdate();
            try { Changed(true); }
            finally { tree.EndUpdate(); }
            ShowAdded(last);
        }

        void AddProgramFromFile(object ctx)
        {
            using (var dlg = new OpenFileDialog { Title = "Choose an app, file or shortcut", Filter = "Apps and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.msc;*.cpl;*.url|All files|*.*", DereferenceLinks = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                AddNew(new LaunchItem { Type = "program", Name = Path.GetFileNameWithoutExtension(dlg.FileName), Target = dlg.FileName }, ctx);
            }
        }

        void AddFolder(object ctx)
        {
            string path = FolderPicker.Pick(this, "Choose a folder to add");
            if (string.IsNullOrEmpty(path)) return;
            string name = Path.GetFileName(path.TrimEnd('\\'));
            if (string.IsNullOrEmpty(name)) name = path;   // a whole drive, like D:\
            AddNew(new LaunchItem { Type = "program", Name = name, Target = path }, ctx);
        }

        void AddCommand(object ctx)
        {
            using (var dlg = new CommandDialog(this))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                string cmd = dlg.Command.Trim();
                if (cmd.Length == 0) return;
                SplitCommand(cmd, out string file, out string args);
                AddNew(new LaunchItem { Type = "program", Name = string.IsNullOrWhiteSpace(dlg.ItemName) ? cmd : dlg.ItemName.Trim(), Target = file, Arguments = args }, ctx);
            }
        }

        // "control printers" -> control + printers ;  "C:\My app\a.exe" -x -> path + -x
        static void SplitCommand(string cmd, out string file, out string args)
        {
            if (cmd.StartsWith("\""))
            {
                int end = cmd.IndexOf('"', 1);
                if (end > 0) { file = cmd.Substring(1, end - 1); args = cmd.Substring(end + 1).Trim(); return; }
            }
            if (File.Exists(Launcher.Expand(cmd)) || Directory.Exists(Launcher.Expand(cmd))) { file = cmd; args = ""; return; }
            int sp = cmd.IndexOf(' ');
            if (sp < 0) { file = cmd; args = ""; return; }
            file = cmd.Substring(0, sp); args = cmd.Substring(sp + 1).Trim();
        }

        void RemoveSelected()
        {
            var n = tree?.SelectedNode;
            if (n != null) RemoveNode(n);
        }

        void RemoveNode(TreeNode n)
        {
            if (n == null || n.Tag is AppNode || n.Tag is DesktopNode || n.Tag is ClosedAppsNode) return;
            if (n.Tag is BarSection sec)
            {
                if ((sec.Type == "pins" || sec.Type == "menu") && sec.Items.Count > 0 &&
                    MessageBox.Show(this, "Remove \"" + NodeText(sec).Trim(' ', '▴') + "\" and everything in it?", "CL-Taskbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                cfg.Layout.Remove(sec);
            }
            else if (n.Tag is LaunchItem it)
            {
                if (n.Parent?.Tag is DesktopNode) bar.KeepPlaceOnUnpin(it);
                ListOf(n.Parent?.Tag)?.Remove(it);
            }
            var next = n.NextNode ?? n.PrevNode ?? n.Parent;
            object nextTag = next?.Tag;
            Changed(true);
            if (nextTag != null && !(nextTag is DesktopNode)) SelectObject(nextTag, false);
        }

        void ResetLook(object tag)
        {
            if (tag is BarSection s)
            {
                s.BackColor = ""; s.TextColor = ""; s.LabelBackColor = ""; s.ShowIcon = true;
                s.MenuWidth = 0; s.MenuItemHeight = 0; s.MenuIconSize = 0; s.MenuFontSize = 0;
                s.MenuBackColor = ""; s.MenuTextColor = ""; s.MenuHoverColor = "";
                s.Browser = new BrowserOptions();
                if (s.Type == "windows") { s.DesktopGap = 18; s.DesktopLabels = "number"; s.ShowEmptyDesktops = true; s.CombineWindows = true; }
                if (s.Type == "gap") { s.Width = 16; s.Divider = false; }
                if (s.Type == "item" && s.Items.Count > 0) ResetLook(s.Items[0]);
            }
            else if (tag is LaunchItem it)
            {
                it.ShowText = true; it.TextColor = ""; it.BackColor = ""; it.Height = 0; it.IconSize = 0; it.Bold = false;
                it.IconPath = ""; it.UseMenuBrowser = true; it.Browser = new BrowserOptions(); it.OpenModeOverride = ""; it.PrivateOverride = "";
            }
        }
    }
}
