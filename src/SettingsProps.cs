using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CLTaskbar
{
    // The right-hand property editor and the Taskbar appearance page.
    internal partial class SettingsForm
    {
        int py;
        int X0 => P(20);
        int LW => P(170);
        int X1 => X0 + LW + P(10);
        int TW => P(300);

        Panel oldProps;

        // Builds the new page off-screen, then swaps it in all at once (no flicker or fade)
        void BeginProps()
        {
            oldProps = props;
            props = NewPropsPanel();
            props.SuspendLayout();
            py = P(14);
        }

        void EndProps()
        {
            py += P(20);
            props.Controls.Add(new Panel { Location = new Point(0, py), Size = new Size(1, 1) });  // bottom spacing when scrolling
            props.ResumeLayout(false);
            var host = oldProps?.Parent;
            if (host != null)
            {
                host.SuspendLayout();
                host.Controls.Add(props);
                host.Controls.Remove(oldProps);
                host.ResumeLayout(true);
                oldProps.Dispose();
            }
            oldProps = null;
        }

        void BuildProps()
        {
            if (props == null || tree == null || building) return;
            BeginProps();
            var n = tree.SelectedNode;
            if (n == null) Note("Pick something on the left, or click Add.");
            else if (n.Tag is BarSection sec) SectionProps(sec);
            else if (n.Tag is LaunchItem it) ItemProps(it, n.Parent?.Tag, SectionOf(n));
            else if (n.Tag is AppNode an) AppProps(an, n.Parent?.Tag as DesktopNode);
            else if (n.Tag is DesktopNode dn) DesktopProps(dn.D);
            else if (n.Tag is ClosedAppsNode) Note("Apps you've given your own icon, name or color that aren't open right now. Pick one to change it.");
            EndProps();
        }

        // ---------------- per-selection editors ----------------

        // "Your 7 icons: 2 rows (4 across, then 3)" — so it's clear what the numbers do
        static string GridShapeText(BarSection sec)
        {
            int n = sec.Items.Count(i => i.Type == "program" || i.Type == "web");
            string icons = n == 1 ? "Your 1 icon" : "Your " + n + " icons";
            if (n == 0) return "Add some apps to this group and the grid shows them here.";
            if (sec.GridShape == "row") return icons + " show in one row, side by side.";
            if (sec.GridShape == "column") return icons + " show in one column, stacked.";
            int cols = Math.Max(1, sec.GridColumns > 0 ? sec.GridColumns : 4);
            if (cols >= n) return icons + " all fit in one row.";
            if (cols == 1) return icons + " show in one column, stacked.";
            int rows = (n + cols - 1) / cols, last = n - (rows - 1) * cols;
            return icons + ": " + rows + " rows of " + cols + (last != cols ? " (the last row has " + last + ")" : "") + ".";
        }

        void SectionProps(BarSection sec)
        {
            switch (sec.Type)
            {
                case "pins":
                    Heading(string.IsNullOrWhiteSpace(sec.Name) ? "Icon Group" : sec.Name);
                    Note("Icons that sit right on the taskbar, or tucked into a popup grid. Each one opens with a single click.");
                    if (sec.Style != "grid") Note("Icons side by side on the taskbar. Drag to reorder here or right on the taskbar.");
                    TextRow("Name", () => sec.Name, v => sec.Name = v);
                    ComboRow("Style", new[] { new[] { "row", "Taskbar buttons" }, new[] { "grid", "Popup grid (^ button)" } }, () => sec.Style, v => { sec.Style = v; Later(BuildProps); });
                    if (sec.Style == "grid")
                    {
                        Note("Like the notification area's ^ button: one button on the taskbar that opens a grid of these apps above it. Drag icons around inside the grid to reorder them.");
                        ComboRow("Shape", new[] { new[] { "grid", "Grid (rows and columns)" }, new[] { "row", "One row, side by side" }, new[] { "column", "One column, stacked" } },
                            () => string.IsNullOrEmpty(sec.GridShape) ? "grid" : sec.GridShape, v => { sec.GridShape = v; Later(BuildProps); });
                        Label shapeNote = null;
                        Action describe = () => { if (shapeNote != null) shapeNote.Text = GridShapeText(sec); };
                        if ((sec.GridShape ?? "grid") == "grid")
                            NumberRow("Icons across", () => sec.GridColumns, v => { sec.GridColumns = Math.Max(1, Math.Min(30, v)); describe(); }, 4, "Per row", true);
                        Note(GridShapeText(sec));
                        shapeNote = props.Controls.OfType<Label>().LastOrDefault();
                        NumberRow("Icon size", () => sec.GridIconSize, v => sec.GridIconSize = v, 24, "Default: 24 px");
                        ColorRow("Grid background", () => sec.MenuBackColor, v => sec.MenuBackColor = v);
                        ColorRow("Grid highlight", () => sec.MenuHoverColor, v => sec.MenuHoverColor = v);
                        Section("The button on the taskbar");
                        CheckRow("Show text on the button", () => sec.ShowLabel, v => sec.ShowLabel = v);
                        TextRow("Button text", () => sec.Label, v => sec.Label = v, "Leave empty to use the name");
                        CheckRow("Show an icon on the button", () => sec.ShowIcon, v => sec.ShowIcon = v);
                        PathRow("Button icon", () => sec.IconPath, v => sec.IconPath = v, "icon", "File or web address; empty = ^ arrow");
                        ColorRow("Text color", () => sec.TextColor, v => sec.TextColor = v);
                    }
                    ColorRow("Background", () => sec.BackColor, v => sec.BackColor = v);
                    BrowserRows("Web links in this group", sec.Browser);
                    Section(null);
                    Note("Import from Windows: adds any apps pinned to the real Windows taskbar that aren't already in your groups.");
                    Buttons(("Import pins from the Windows taskbar", () =>
                            {
                                int added = 0;
                                foreach (var p in Config.ImportWindowsPins())
                                {
                                    bool have = cfg.Layout.Where(x => x.Type == "pins" || x.Type == "item").SelectMany(x => x.Items)
                                        .Any(i => string.Equals(i.Name, p.Name, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(Path.GetFileName(Launcher.Expand(i.Target)), Path.GetFileName(Launcher.Expand(p.Target)), StringComparison.OrdinalIgnoreCase));
                                    if (!have) { sec.Items.Add(p); added++; }
                                }
                                Changed(true);
                                MessageBox.Show(this, added == 0 ? "All your Windows taskbar pins are already here." : "Added " + added + " app" + (added == 1 ? "" : "s") + ".", "CL-Taskbar");
                            }),
                            ("Reset look to default", () => { ResetLook(sec); Changed(true); }));
                    break;

                case "menu":
                    Heading(string.IsNullOrWhiteSpace(sec.Name) ? "Menu" : sec.Name);
                    Note("One button that opens a list with names. Good for lots of tools, settings, web links, and sub-menus.");
                    Section("Button");
                    TextRow("Name", () => sec.Name, v => sec.Name = v, "Shown as the tooltip and menu title");
                    CheckRow("Show text on the button", () => sec.ShowLabel, v => sec.ShowLabel = v);
                    TextRow("Button text", () => sec.Label, v => sec.Label = v, "Leave empty to use the name");
                    CheckRow("Show an icon on the button", () => sec.ShowIcon, v => sec.ShowIcon = v);
                    PathRow("Icon", () => sec.IconPath, v => sec.IconPath = v, "icon", "File or web address; empty = ^ arrow");
                    ColorRow("Background", () => sec.BackColor, v => sec.BackColor = v);
                    ColorRow("Text color", () => sec.TextColor, v => sec.TextColor = v);
                    Section("The list that opens");
                    NumberRow("Width", () => sec.MenuWidth, v => sec.MenuWidth = v, null, "Fits the items");
                    NumberRow("Item height", () => sec.MenuItemHeight, v => sec.MenuItemHeight = v, null, "Automatic");
                    NumberRow("Icon size", () => sec.MenuIconSize, v => sec.MenuIconSize = v, 16, "Windows default: 16 px");
                    NumberRow("Text size (pt)", () => (int)sec.MenuFontSize, v => sec.MenuFontSize = v, 9, "Windows default: 9 pt");
                    ColorRow("Background", () => sec.MenuBackColor, v => sec.MenuBackColor = v);
                    ColorRow("Text color", () => sec.MenuTextColor, v => sec.MenuTextColor = v);
                    ColorRow("Highlight", () => sec.MenuHoverColor, v => sec.MenuHoverColor = v);
                    BrowserRows("Web links in this menu", sec.Browser);
                    Section(null);
                    Buttons(("Reset look to default", () => { ResetLook(sec); Changed(true); }));
                    break;

                case "item":
                {
                    var it = sec.Items.FirstOrDefault();
                    if (it == null) { Note("This button is empty."); break; }
                    Heading(ItemText(it));
                    Note("A single " + (it.Type == "web" ? "link" : "app") + " directly on the taskbar. Drag it into a group or menu to move it there.");
                    ItemCore(it, sec);
                    Section("On the taskbar");
                    CheckRow("Show an icon", () => sec.ShowIcon, v => sec.ShowIcon = v);
                    CheckRow("Show text", () => sec.ShowLabel, v => sec.ShowLabel = v);
                    TextRow("Text", () => sec.Label, v => sec.Label = v, "Leave empty to use the name");
                    ColorRow("Background", () => sec.BackColor, v => sec.BackColor = v);
                    ColorRow("Text color", () => sec.TextColor, v => sec.TextColor = v);
                    if (it.Type == "web") BrowserRows("Opening the link", sec.Browser);
                    Section(null);
                    Buttons(("Reset look to default", () => { ResetLook(sec); sec.ShowLabel = false; Changed(true); }));
                    break;
                }

                case "windows":
                    Heading(sec.GroupByDesktop ? "Virtual desktops" : "Open apps");
                    Note("Your open apps" + (sec.GroupByDesktop ? ", grouped by desktop. Pick a desktop below for its own label, colors and pinned apps, or an app to give it your own icon, name or color." : ". Pick an app below to give it your own icon, name or color.") + " On the taskbar, drag apps to reorder them" + (sec.GroupByDesktop ? " or onto another desktop to move them there." : "."));
                    CheckRow("Group by virtual desktop", () => sec.GroupByDesktop, v => { sec.GroupByDesktop = v; Later(BuildProps); });
                    if (sec.GroupByDesktop)
                    {
                        ComboRow("Desktop labels", new[] { new[] { "number", "Number (1, 2, 3)" }, new[] { "name", "Desktop name" }, new[] { "numname", "Number and name (1 Work)" }, new[] { "none", "None" } }, () => sec.DesktopLabels, v => sec.DesktopLabels = v);
                        CheckRow("Show desktops with nothing open", () => sec.ShowEmptyDesktops, v => sec.ShowEmptyDesktops = v);
                        NumberRow("Space between desktops", () => sec.DesktopGap, v => sec.DesktopGap = v, 18, "Default: 18 px", true);
                    }
                    else
                        ComboRow("Show windows from", new[] { new[] { "current", "Only this desktop" }, new[] { "all", "All desktops" } }, () => sec.FlatScope, v => sec.FlatScope = v);
                    CheckRow("One button per app (combine its windows)", () => sec.CombineWindows, v => sec.CombineWindows = v);
                    ColorRow(sec.GroupByDesktop ? "Group background" : "Background", () => sec.BackColor, v => sec.BackColor = v);
                    if (sec.GroupByDesktop)
                    {
                        ColorRow("Label color", () => sec.TextColor, v => sec.TextColor = v);
                        ColorRow("Label background", () => sec.LabelBackColor, v => sec.LabelBackColor = v);
                        Note("These apply to every desktop. Each desktop can override them: pick it below.");
                    }
                    if (cfg.Orders.Count > 0)
                    {
                        Section(null);
                        Buttons(("Forget my app order", () => { cfg.Orders.Clear(); Changed(); }),
                                ("Reset look to default", () => { ResetLook(sec); Changed(true); }));
                    }
                    else { Section(null); Buttons(("Reset look to default", () => { ResetLook(sec); Changed(true); })); }
                    break;

                case "gap":
                    Heading("Space");
                    NumberRow("Width", () => sec.Width, v => sec.Width = v, 16, "Default: 16 px", true);
                    CheckRow("Show a divider line", () => sec.Divider, v => sec.Divider = v);
                    break;
            }
        }

        void ItemProps(LaunchItem it, object parent, BarSection sec)
        {
            if (it.Type == "separator") { Heading("Separator line"); Note("Splits the list into sections."); return; }
            Heading(ItemText(it));
            ItemCore(it, sec);
            bool inPins = (parent is BarSection ps && ps.Type == "pins") || parent is DesktopNode;
            if (it.Type == "web")
            {
                LinkBrowserRows(it, parent is DesktopNode ? null : sec?.Browser, parent is DesktopNode ? null : inPins ? "group" : "menu");
            }
            if (inPins)
            {
                Section("On the taskbar");
                ColorRow("Background", () => it.BackColor, v => it.BackColor = v);
            }
            else
            {
                Section("In the list");
                CheckRow("Show the name", () => it.ShowText, v => it.ShowText = v);
                CheckRow("Bold", () => it.Bold, v => it.Bold = v);
                int defIcon = sec != null && sec.MenuIconSize > 0 ? sec.MenuIconSize : 16;
                NumberRow("Icon or picture size", () => it.IconSize, v => it.IconSize = v, defIcon, "Menu default: " + defIcon + " px");
                NumberRow("Height", () => it.Height, v => it.Height = v, null, sec != null && sec.MenuItemHeight > 0 ? "Menu default: " + sec.MenuItemHeight + " px" : "Automatic");
                ColorRow("Text color", () => it.TextColor, v => it.TextColor = v);
                ColorRow("Background", () => it.BackColor, v => it.BackColor = v);
                Note("For a picture with no text: pick a custom icon above, make it bigger here, and turn off Show the name.");
            }
            Section(null);
            Buttons(("Reset look to default", () => { ResetLook(it); Changed(true); }));
        }

        // Name, what it opens, icon
        void ItemCore(LaunchItem it, BarSection sec)
        {
            switch (it.Type)
            {
                case "folder":
                    TextRow("Name", () => it.Name, v => it.Name = v);
                    PathRow("Icon", () => it.IconPath, v => it.IconPath = v, "icon", "File or web address; empty = folder icon");
                    break;
                case "web":
                    TextRow("Name", () => it.Name, v => it.Name = v);
                    TextRow("Web address", () => it.Target, v => it.Target = v);
                    PathRow("Icon", () => it.IconPath, v => it.IconPath = v, "icon", "File or web address; empty = the site's icon");
                    Buttons(("Open it now", () => Launcher.OpenWeb(it, sec?.Browser, bar)));
                    break;
                default:
                    if (IsFolderItem(it))
                    {
                        // a folder: opens in File Explorer
                        TextRow("Name", () => it.Name, v => it.Name = v);
                        PathRow("Folder", () => it.Target, v => it.Target = v, "folder", "Any folder or drive, like D:\\ or %USERPROFILE%\\Downloads");
                        PathRow("Icon", () => it.IconPath, v => it.IconPath = v, "icon", "File or web address; empty = the folder's icon");
                        Buttons(("Open it now", () => Launcher.Run(it, this)));
                        break;
                    }
                    TextRow("Name", () => it.Name, v => it.Name = v);
                    PathRow("Opens", () => it.Target, v => it.Target = v, "file", "App, file, folder, or a command like mmsys.cpl");
                    TextRow("Arguments", () => it.Arguments, v => it.Arguments = v, "Optional");
                    PathRow("Start in", () => it.StartIn, v => it.StartIn = v, "folder", "Optional");
                    CheckRow("Run as administrator", () => it.RunAsAdmin, v => it.RunAsAdmin = v);
                    PathRow("Icon", () => it.IconPath, v => it.IconPath = v, "icon", "File or web address; empty = the app's icon");
                    Buttons(("Open it now", () => Launcher.Run(it, this)));
                    break;
            }
        }

        static bool IsFolderItem(LaunchItem it)
        {
            if (it.Type != "program" || string.IsNullOrWhiteSpace(it.Target) || !string.IsNullOrWhiteSpace(it.Arguments)) return false;
            try { return System.IO.Directory.Exists(Launcher.Expand(it.Target)); } catch { return false; }
        }

        void DesktopProps(DesktopInfo d)
        {
            DesktopStyle Get() => cfg.FindDesktop(d.Id);
            DesktopStyle GetOrAdd() => cfg.DesktopFor(d.Id);
            Heading(d.Name);
            Note("How this desktop's group looks on the taskbar, and apps pinned to it. Pinned apps stay on this desktop's group even when closed; clicking one opens it here. To pin an app, drag it onto this desktop, or right-click it on the taskbar > Pin to " + d.Name + ".");
            TextRow("Label", () => Get()?.Label ?? "", v => GetOrAdd().Label = v, "Default: " + (d.Index + 1));
            PathRow("Picture", () => Get()?.ImagePath ?? "", v => GetOrAdd().ImagePath = v, "icon", "Optional: a file or web address");
            ColorRow("Group background", () => Get()?.BackColor ?? "", v => GetOrAdd().BackColor = v);
            ColorRow("Label color", () => Get()?.TextColor ?? "", v => GetOrAdd().TextColor = v);
            ColorRow("Label background", () => Get()?.LabelBackColor ?? "", v => GetOrAdd().LabelBackColor = v);
            Section(null);
            Buttons(("Open all pinned apps", () => bar.OpenAllOnDesktopPublic(d)),
                    ("Reset look to default", () => { var ds = Get(); if (ds != null) { ds.Label = ""; ds.ImagePath = ""; ds.BackColor = ""; ds.TextColor = ""; ds.LabelBackColor = ""; } Changed(true); }));
        }

        void AppProps(AppNode an, DesktopNode desk)
        {
            string real = an.Sample != null ? Icons.RealName(an.Sample) : cfg.FindApp(an.Key)?.AppName ?? an.Key;
            Heading(real);
            Note("Changes here apply wherever this app shows up, and to new windows you open from the taskbar. Apps you don't change aren't remembered.");
            AppStyle Get() => cfg.FindApp(an.Key);
            AppStyle GetOrAdd()
            {
                var st = Get();
                if (st == null) { st = new AppStyle { Key = an.Key, AppName = real, ExePath = an.Sample?.ExePath ?? "" }; cfg.Apps.Add(st); }
                return st;
            }
            // the same settings a pinned app has, so an open app and a pinned one work alike
            TextRow("Name", () => Get()?.Name ?? "", v => GetOrAdd().Name = v, real);
            bool canOpen = an.Sample != null && ItemFromApp(an) != null;
            string opens = an.Sample != null ? (ItemFromApp(an)?.Target ?? an.Sample.ExePath ?? "") : (Get()?.ExePath ?? "");
            RowLabel("Opens");
            var ob = Input(X1, TW, opens, null);
            ob.ReadOnly = true; ob.ForeColor = cDim;
            py += ob.Height + P(8);
            TextRow("Arguments", () => Get()?.Arguments ?? "", v => GetOrAdd().Arguments = v, "Optional");
            PathRow("Start in", () => Get()?.StartIn ?? "", v => GetOrAdd().StartIn = v, "folder", "Optional");
            CheckRow("Run as administrator", () => Get()?.RunAsAdmin == true, v => GetOrAdd().RunAsAdmin = v);
            PathRow("Icon", () => Get()?.IconPath ?? "", v => GetOrAdd().IconPath = v, "icon", "File or web address; empty = the app's icon");
            if (canOpen) Buttons(("Open it now", () => bar.OpenNewWindow(an.Sample)));
            Section("On the taskbar");
            ColorRow("Background", () => Get()?.BackColor ?? "", v => GetOrAdd().BackColor = v);
            Section(null);
            Note("To pin this app, right-click it (here or on the taskbar) > Pin to group or Pin to desktop.");
            Buttons(("Reset to default", () => { cfg.Apps.RemoveAll(a => string.Equals(a.Key, an.Key, StringComparison.OrdinalIgnoreCase)); Changed(true); }));
        }

        void BrowserRows(string title, BrowserOptions o)
        {
            Section(title);
            ComboRow("Browser", Launcher.BrowserChoices, () => o.Browser, v => { o.Browser = v; Launcher.ClearIcons(); Later(() => { if (page == "layout") Changed(true); else BuildPropsOrPage(); }); });
            if (o.Browser == "custom")
                PathRow("Browser program", () => o.CustomPath, v => o.CustomPath = v, "exe", null);
            ComboRow("Where links open", Launcher.OpenModes, () => o.OpenMode, v => o.OpenMode = v);
            CheckRow("Prefer browser windows that aren't minimized", () => o.PreferVisible, v => o.PreferVisible = v);
            CheckRow("Private (incognito) window", () => o.Private, v => o.Private = v);
        }

        // Each web link can follow its menu (or group), or choose its own browser, where it opens, and regular or private
        void LinkBrowserRows(LaunchItem it, BrowserOptions menu, string whose)
        {
            Section("Opening this link");
            string Name(string[][] list, string key) => list.FirstOrDefault(x => x[0] == key)?[1] ?? key;
            bool follows = it.UseMenuBrowser && menu != null;
            var browsers = new List<string[]>();
            if (menu != null) browsers.Add(new[] { "@menu", "Same as the " + whose + " (" + Name(Launcher.BrowserChoices, menu.Browser) + ")" });
            browsers.AddRange(Launcher.BrowserChoices);
            ComboRow("Browser", browsers.ToArray(), () => follows ? "@menu" : (it.Browser ?? new BrowserOptions()).Browser, v =>
            {
                if (v == "@menu") it.UseMenuBrowser = true;
                else
                {
                    if (it.UseMenuBrowser && menu != null)
                    {
                        // start from what it did before, then use the browser you picked
                        var start = Launcher.EffectiveBrowser(it, menu);
                        it.Browser = start;
                    }
                    if (it.Browser == null) it.Browser = new BrowserOptions();
                    it.Browser.Browser = v;
                    it.UseMenuBrowser = false;
                }
                Launcher.ClearIcons();
                Later(() => Changed(true));   // refresh the icons in the list and on the taskbar
            });
            if (follows)
            {
                var modes = new List<string[]> { new[] { "", "Same as the " + whose + " (" + Name(Launcher.OpenModes, menu.OpenMode) + ")" } };
                modes.AddRange(Launcher.OpenModes);
                ComboRow("Where it opens", modes.ToArray(), () => it.OpenModeOverride ?? "", v => it.OpenModeOverride = v);
                ComboRow("Window", new[] { new[] { "", "Same as the " + whose + " (" + (menu.Private ? "private" : "regular") + ")" }, new[] { "regular", "Regular window" }, new[] { "private", "Private (incognito) window" } },
                    () => it.PrivateOverride ?? "", v => it.PrivateOverride = v);
            }
            else
            {
                var o = it.Browser ?? (it.Browser = new BrowserOptions());
                if (o.Browser == "custom") PathRow("Browser program", () => o.CustomPath, v => o.CustomPath = v, "exe", null);
                ComboRow("Where it opens", Launcher.OpenModes, () => o.OpenMode, v => o.OpenMode = v);
                ComboRow("Window", new[] { new[] { "regular", "Regular window" }, new[] { "private", "Private (incognito) window" } }, () => o.Private ? "private" : "regular", v => o.Private = v == "private");
                CheckRow("Prefer browser windows that aren't minimized", () => o.PreferVisible, v => o.PreferVisible = v);
            }
        }

        void Later(Action a) { if (IsHandleCreated) BeginInvoke(a); else a(); }

        void BuildPropsOrPage() { if (page == "layout") BuildProps(); else ShowPage(page); }

        // Label / picture / colors for each virtual desktop
        void DesktopRows()
        {
            var list = bar.DesktopList();
            if (list.Count == 0) return;
            foreach (var d in list)
            {
                var id = d.Id;
                DesktopStyle Get() => cfg.FindDesktop(id);
                DesktopStyle GetOrAdd()
                {
                    var x = Get();
                    if (x == null) { x = new DesktopStyle { Id = id.ToString("N") }; cfg.Desktops.Add(x); }
                    return x;
                }
                Section(d.Name);
                TextRow("Label", () => Get()?.Label ?? "", v => GetOrAdd().Label = v, "Default: " + (d.Index + 1));
                PathRow("Picture", () => Get()?.ImagePath ?? "", v => GetOrAdd().ImagePath = v, "icon", "Optional");
                ColorRow("Background", () => Get()?.BackColor ?? "", v => GetOrAdd().BackColor = v);
                ColorRow("Label color", () => Get()?.TextColor ?? "", v => GetOrAdd().TextColor = v);
            }
        }

        // ============================================================
        // Right-click menus page
        // ============================================================

        void BuildMenusPage()
        {
            props = NewPropsPanel();
            var box = new Panel { Dock = DockStyle.Fill, BackColor = cBorder, Padding = new Padding(1) };
            box.Controls.Add(props);
            content.Controls.Add(box);
            BeginProps();
            Section("Apps");
            CheckRow("Right-clicking an app shows only Windows' own menu", () => cfg.WindowsAppMenus, v => cfg.WindowsAppMenus = v);
            Note("The same menu File Explorer shows for the app (Shift+right-click for the longer one), instead of CL-Taskbar's. Things like Close window, Pin to group and Customize… are then only in these settings. Web links and anything without a file behind it still use CL-Taskbar's menu.");
            CheckRow("Keep the menu narrow by shortening long window titles and app names", () => cfg.NarrowWindowList, v => { cfg.NarrowWindowList = v; Later(() => ShowPage("menus")); });
            if (cfg.NarrowWindowList)
                NumberRow("Widest the menu gets", () => cfg.MenuMaxWidth, v => cfg.MenuMaxWidth = v, BarForm.DefaultMenuWidth, "Default: " + BarForm.DefaultMenuWidth + " px");
            Note("Long window titles (like a browser tab with a long name) and long app names (like a Terminal window) are shortened with … so the menu stays this wide. Options with fixed names are never cut, so the menu is at least as wide as the longest one you have turned on. Point at one to see its whole title. Turn this off to always show full titles.");
            Section("CL-Taskbar's menus");
            Note("Each list is one of the taskbar's right-click menus, in the order it appears. Click an option to turn it on or off; drag it (or a divider line) up or down to move it.");
            var lists = new List<MenuOrderList>();
            CheckRow("Show only the options that are turned on", () => cfg.MenuEditorEnabledOnly, v =>
            {
                cfg.MenuEditorEnabledOnly = v;
                // lists change height: slide everything below each one up or down to match
                foreach (var l in lists.OrderByDescending(x => x.Top))
                {
                    int oldBottom = l.Bottom;
                    l.SetEnabledOnly(v);
                    int delta = l.Bottom - oldBottom;
                    if (delta == 0) continue;
                    foreach (Control c in props.Controls)
                        if (c != l && c.Top >= oldBottom) c.Top += delta;
                }
            });
            foreach (var grp in BarForm.MenuOptions.GroupBy(o => o[0]))
            {
                Section("Right-click on: " + grp.Key.ToLowerInvariant());
                var list = new MenuOrderList(bar, grp.Key, s, cText, cDim, cAccent, cBorder, cHover, cSurface, () => Changed()) { Location = new Point(X0, py) };
                list.SetEnabledOnly(cfg.MenuEditorEnabledOnly);
                lists.Add(list);
                props.Controls.Add(list);
                py += list.Height + P(6);
            }
            Buttons(("Reset right-click menus", () => { cfg.MenuChoices.Clear(); cfg.MenuOrder.Clear(); Changed(); Later(() => ShowPage("menus")); }));

            EndProps();
        }

        // ============================================================
        // General page: settings for the whole bar (look + behavior)
        // ============================================================

        void BuildAppearancePage()
        {
            props = NewPropsPanel();
            var box = new Panel { Dock = DockStyle.Fill, BackColor = cBorder, Padding = new Padding(1) };
            box.Controls.Add(props);
            content.Controls.Add(box);
            BeginProps();

            Section("Bar");
            string mode = cfg.Color.StartsWith("#") ? "custom" : cfg.Color;
            ComboRow("Color", new[] { new[] { "sample", "Match the taskbar" }, new[] { "theme", "Windows theme colors" }, new[] { "custom", "Custom color" } }, () => mode, v =>
            {
                if (v == "custom") { if (!cfg.Color.StartsWith("#")) cfg.Color = "#202020"; }
                else cfg.Color = v;
                Changed();
                Later(() => ShowPage("appearance"));
            });
            if (cfg.Color.StartsWith("#")) ColorRow("Custom color", () => cfg.Color, v => cfg.Color = string.IsNullOrEmpty(v) ? "sample" : v);
            CheckRow("Highlight the desktop I'm on", () => cfg.HighlightCurrentDesktop, v => cfg.HighlightCurrentDesktop = v);
            ColorRow("Highlight color", () => cfg.HighlightColor, v => cfg.HighlightColor = v);
            ColorRow("Line under open apps", () => cfg.IndicatorColor, v => cfg.IndicatorColor = v);
            ColorRow("Line under the app in use", () => cfg.ActiveIndicatorColor, v => cfg.ActiveIndicatorColor = v);
            Note("Pick line colors that stand out against the highlight, so you can still see what's open on the desktop you're on.");
            CheckRow("Divider lines between sections", () => cfg.ShowDividers, v => cfg.ShowDividers = v);

            Section("Sizes");
            int di = bar.WindowsIconSize, dw = bar.WindowsButtonWidth;
            NumberRow("Icon size", () => cfg.IconSize, v => cfg.IconSize = v, di, "Windows default: " + di + " px");
            NumberRow("Button width", () => cfg.ButtonWidth, v => cfg.ButtonWidth = v, dw, "Windows default: " + dw + " px");
            NumberRow("Space between sections", () => cfg.SectionGap, v => cfg.SectionGap = v, 18, "Default: 18 px", true);

            Section("Previews");
            CheckRow("Show window previews when pointing at an app", () => cfg.ShowPreviews, v => cfg.ShowPreviews = v);
            NumberRow("Delay (ms)", () => cfg.PreviewDelayMs, v => cfg.PreviewDelayMs = v, 0, "Default: 0 ms (instant)", true);
            CheckRow("Pointing at a preview shows that window on the screen", () => cfg.PeekWindows, v => cfg.PeekWindows = v);
            CheckRow("Show the name when pointing at a popup grid or menu", () => cfg.ShowGroupNames, v => cfg.ShowGroupNames = v);

            Section("Position");
            NumberRow("Left edge nudge", () => cfg.LeftOffset, v => cfg.LeftOffset = v, 0, "Positive moves it right", true, true);
            NumberRow("Right edge nudge", () => cfg.RightOffset, v => cfg.RightOffset = v, 0, "Positive moves it left", true, true);

            Section("Clicking apps");
            CheckRow("Clicking an app with several windows switches between them", () => cfg.ClickCyclesWindows, v => cfg.ClickCyclesWindows = v);
            Note("Turn this off to get the Windows behavior: clicking shows the windows so you can pick one.");

            Section("Notification badges");
            CheckRow("Show unread counts on apps (like Teams, Outlook and Mail)", () => cfg.ShowBadges, v => cfg.ShowBadges = v);
            CheckRow("Classic Outlook: show unread email in your inboxes", () => cfg.OutlookUnread, v => cfg.OutlookUnread = v);
            CheckRow("Also count notifications waiting in the notification center", () => cfg.CountNotificationBadges, v => cfg.CountNotificationBadges = v);
            ColorRow("Badge color", () => cfg.BadgeColor, v => cfg.BadgeColor = v);
            Note("Apps tell Windows their unread count (a badge). CL-Taskbar reads a private copy of Windows' notification list to show it, then deletes the copy. Classic Outlook doesn't give Windows a count, so CL-Taskbar asks Outlook itself how many unread emails your inboxes have (read-only); it goes down as you read them. The last option is for other apps that don't set a count: it counts their notifications, and they count as seen once you switch to the app.");

            Section("This settings window");
            ComboRow("Theme", new[] { new[] { "dark", "Dark" }, new[] { "light", "Light" }, new[] { "auto", "Match Windows (" + (WindowsIsDark() ? "dark" : "light") + " right now)" } },
                () => string.IsNullOrEmpty(cfg.SettingsTheme) ? "dark" : cfg.SettingsTheme,
                v => { cfg.SettingsTheme = v; Later(() => bar.ReopenSettings(Bounds, "appearance")); });

            Section("Behavior");
            CheckRow("Apps I pin to a desktop jump to the front of its group", () => cfg.DesktopPinsFirst, v => cfg.DesktopPinsFirst = v);
            CheckRow("Hide when a fullscreen app or game is in front", () => cfg.HideWhenFullscreen, v => cfg.HideWhenFullscreen = v);
            CheckRow("Show the icon in the notification area", () => cfg.ShowTrayIcon, v => cfg.ShowTrayIcon = v);
            CheckRow("Start when I sign in (adds a shortcut to your Startup folder)", () => File.Exists(StartupLink), v =>
            {
                try
                {
                    if (v) CreateShortcut(StartupLink, Application.ExecutablePath);
                    else if (File.Exists(StartupLink)) File.Delete(StartupLink);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "CL-Taskbar"); }
            });

            Section("Your settings");
            Note("Saved automatically in " + Config.FilePath);
            Buttons(("Open settings folder", () => System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + Config.FilePath + "\"")),
                    ("Reset appearance", () =>
                    {
                        var d = new Config();
                        cfg.Color = d.Color; cfg.IconSize = 0; cfg.ButtonWidth = 0; cfg.SectionGap = d.SectionGap; cfg.ShowDividers = d.ShowDividers;
                        cfg.HighlightCurrentDesktop = true; cfg.ShowPreviews = true; cfg.PreviewDelayMs = d.PreviewDelayMs; cfg.ShowGroupNames = d.ShowGroupNames; cfg.PeekWindows = d.PeekWindows;
                        cfg.LeftOffset = 0; cfg.RightOffset = 0; cfg.HideWhenFullscreen = true; cfg.ShowTrayIcon = true;
                        cfg.HighlightColor = ""; cfg.IndicatorColor = ""; cfg.ActiveIndicatorColor = ""; cfg.ClickCyclesWindows = true;
                        Changed(); Later(() => ShowPage("appearance"));
                    }),
                    ("Reset taskbar layout", () =>
                    {
                        if (MessageBox.Show(this, "Put the taskbar layout back to how it started? Your groups, menus and pins will be replaced (your pins are re-copied from Windows).",
                            "CL-Taskbar", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                        cfg.Layout = Config.DefaultLayout(true, null);
                        Changed();
                    }));
            EndProps();
        }

        // ============================================================
        // Tips page
        // ============================================================

        void BuildTipsPage()
        {
            props = NewPropsPanel();
            var box = new Panel { Dock = DockStyle.Fill, BackColor = cBorder, Padding = new Padding(1) };
            box.Controls.Add(props);
            content.Controls.Add(box);
            BeginProps();
            void Tip(string title, params string[] lines)
            {
                Section(title);
                foreach (var l in lines) TipLine(l);
            }
            Tip("Clicking",
                "Click an app to switch to it, even if it's on another desktop. Click the app you're using to minimize it.",
                "An app with several windows: clicking switches between them, or shows them to pick from (see General > Clicking apps).",
                "Middle-click, or Shift+click, opens a new window of the app.",
                "Click a desktop's number to switch to that desktop.",
                "Point at an app to see its windows. Click a preview to open it; middle-click it or use its X to close it.");
            Tip("Dragging on the taskbar",
                "Drag an app left or right to reorder it. The order is remembered for each desktop, even after the app closes. New apps go at the end. Apps pinned to a desktop can be dragged in among the open apps too.",
                "Drag an app onto another desktop's group to move its window to that desktop.",
                "Drag an open app onto an Icon Group to pin it there.",
                "Drag Menus and single items to move them.",
                "Hold Shift while dragging any icon to move its whole group.",
                "The mouse wheel scrolls the bar when it's too full.");
            Tip("Right-click on the taskbar",
                "Right-click an app for Run as administrator, Properties, Pin to group, Pin to this desktop, Customize and Close.",
                "Turn on Right-click menus > \"Recent files (jump list)\" to list the files you recently opened with an app, like the real taskbar. Click one to open it in that app, or right-click it for Windows' menu for that file.",
                "Apps like Teams show their unread count on their icon, and classic Outlook shows how many unread emails are in your inboxes (General > Notification badges).",
                "The Right-click menus tab lists every option for each right-click menu: turn any on or off, and drag options and divider lines into your own order. Extra options like Move to desktop, End task, Minimize and Show desktop are there, off until you turn them on.",
                "Tick \"Show only the options that are turned on\" at the top to see just what's in your menus, which makes reordering easier. Anything you turn off stays in view until you tick it again.",
                "Shift+right-click an app for Explorer's full menu (Open with, Send to, Copy and so on). Or right-click the app's name inside the taskbar's right-click menu for Windows' normal right-click menu, and Shift+right-click it for the full one.",
                "Right-click a desktop's number to open all the apps pinned to that desktop, customize it, or close its windows.",
                "Right-click an empty spot for these settings, Task Manager and Exit.",
                "Prefer Windows' own menus? Right-click menus > \"Right-clicking an app shows only Windows' own menu\" swaps CL-Taskbar's app menu for the one File Explorer shows.");
            Tip("Icon Groups, Menus and Spaces",
                "An Icon Group is a row of your own pinned apps, separate from Windows' pins. Each app gets its own button and opens with one click. You can have as many groups as you like.",
                "A Menu is one button that opens a list with names. Use it for lots of tools, settings or web links, with sub-menus and divider lines if you want.",
                "A Space is an empty gap between things on the taskbar, with an optional line.");
            Tip("Pinning",
                "A group can be normal taskbar buttons, or a popup grid: one ^ button that opens a grid of the apps above the taskbar, like the notification area. Set it with Style in the group's settings, then pick its Shape: a grid, one row, or one column. Drag icons inside the grid to reorder them.",
                "Folders work anywhere apps do: Add > Folder… puts one in a group, a menu or straight on the taskbar, and clicking it opens it in File Explorer.",
                "Give a pinned app a custom icon and its open windows use that icon too.",
                "Import pins from the Windows taskbar (in a group's settings) adds any apps pinned to the real taskbar that you don't have yet.",
                "Pinning to a desktop keeps the app on that desktop's group even when it's closed. Clicking it opens it on that desktop. A newly pinned app stays where it was; turn on General > Behavior > \"Apps I pin to a desktop jump to the front\" if you'd rather it moved to the front.",
                "The same app can be pinned in several groups and to several desktops.");
            Tip("In these settings",
                "Drag things in the list to rearrange them, or into and out of groups and menus.",
                "Right-click something in the list for options that apply to it, like adding into a group, unpinning, or pinning an open app to a group or desktop.",
                "Desktop labels can be the number, the desktop's name, both, or nothing: select Virtual desktops, then Desktop labels. Each desktop can also have its own label, picture and colors. Label background colors just the number or name, separately from the group behind the apps; set it for all desktops under Virtual desktops, or per desktop.",
                "The Add button adds things straight onto the taskbar.",
                "Each web link can follow its menu's browser settings or have its own: pick it and use Browser, Where it opens and Window (regular or private) under Opening this link. A link set to a private (incognito) window shows that browser's private icon.",
                "Pick from a list… opens a picker of Windows tools, settings pages, folders and apps that are open: type to search, pick a category on the left, double-click to add one, or hold Ctrl or Shift to pick several and click Add. Type a command… is for anything you'd type in Win+R.",
                "Every color and size has a Default button, and every item has Reset look to default.",
                "Pick… next to a color opens a palette of colors that look good on a taskbar: Windows' own accent colors, soft shades for text, deep tints for backgrounds, and grays. More colors… opens the full color picker, and your picks from it show up at the top of the palette next time.",
                "Icons can be a picture file, an .ico, .exe or .dll file, or a web address of a picture (PNG, JPG, GIF, BMP, ICO or WebP), including the long data:image addresses Google Images copies. A note under the Icon box tells you if a picture loaded or why it couldn't.",
                "The settings window follows you when you switch desktops.",
                "General > This settings window > Theme switches this window between dark and light, or matches Windows.");
            Tip("Good to know",
                "CL-Taskbar never changes your real taskbar or Windows settings. Exit it and the normal taskbar is right there.",
                "While the Start menu or search is open, Windows shows its own taskbar on top. When they close, the bar comes back and focus returns to the app you were using.",
                "Your settings are saved automatically next to CL-Taskbar.exe.");
            EndProps();
        }

        void TipLine(string text)
        {
            int w = X1 + P(420) - X0 - P(16);
            props.Controls.Add(new Label { Text = "•", Font = fBody, ForeColor = cDim, AutoSize = true, Location = new Point(X0, py) });
            var l = new Label { Text = text, Font = fBody, ForeColor = cText, AutoSize = false, Location = new Point(X0 + P(16), py), Width = w };
            using (var g = CreateGraphics()) l.Height = TextRenderer.MeasureText(g, text, fBody, new Size(w, 0), TextFormatFlags.WordBreak).Height + P(2);
            props.Controls.Add(l);
            py += l.Height + P(6);
        }

        static string StartupLink => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CL-Taskbar.lnk");

        internal static void CreateShortcut(string lnk, string target)
        {
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                var st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { Path.GetDirectoryName(target) });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
                Marshal.FinalReleaseComObject(sc);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        // ============================================================
        // Compact row helpers: label on the left, control on the right
        // ============================================================

        void Heading(string text)
        {
            props.Controls.Add(new Label { Text = text, Font = fTitle, ForeColor = cText, AutoSize = true, Location = new Point(X0 - P(2), py) });
            py += P(32);
        }

        void Section(string title)
        {
            if (py > P(20))
            {
                py += P(6);
                props.Controls.Add(new Panel { BackColor = cBorder, Location = new Point(X0, py), Size = new Size(X1 + P(420) - X0, 1) });
                py += P(8);
            }
            if (title != null)
            {
                props.Controls.Add(new Label { Text = title, Font = fSection, ForeColor = cText, AutoSize = true, Location = new Point(X0, py) });
                py += P(26);
            }
        }

        void Note(string text)
        {
            int w = X1 + P(420) - X0;
            var l = new Label { Text = text, Font = fSmall, ForeColor = cDim, AutoSize = false, Location = new Point(X0, py), Width = w };
            using (var g = CreateGraphics()) l.Height = TextRenderer.MeasureText(g, text, fSmall, new Size(w, 0), TextFormatFlags.WordBreak).Height + P(2);
            props.Controls.Add(l);
            py += l.Height + P(8);
        }

        void RowLabel(string text)
        {
            var l = new Label { Text = text, Font = fBody, ForeColor = cText, AutoSize = false, AutoEllipsis = true, Location = new Point(X0, py + P(3)), Size = new Size(LW, P(20)) };
            tips.SetToolTip(l, text);
            props.Controls.Add(l);
        }

        TextBox Input(int x, int width, string text, string cue)
        {
            var tb = new TextBox { Text = text ?? "", Location = new Point(x, py), Width = width, BorderStyle = BorderStyle.FixedSingle, BackColor = cInput, ForeColor = cText, Font = fBody };
            if (!string.IsNullOrEmpty(cue)) tb.HandleCreated += (s2, e2) => { try { SendMessage(tb.Handle, 0x1501 /*EM_SETCUEBANNER*/, (IntPtr)1, cue); } catch { } };
            props.Controls.Add(tb);
            return tb;
        }

        void TextRow(string label, Func<string> get, Action<string> set, string cue = null)
        {
            RowLabel(label);
            var tb = Input(X1, TW, get(), cue);
            tb.TextChanged += (s2, e2) => { set(tb.Text); Changed(); };
            py += tb.Height + P(8);
        }

        // kind: file | folder | icon | exe
        void PathRow(string label, Func<string> get, Action<string> set, string kind, string cue)
        {
            RowLabel(label);
            var tb = Input(X1, TW - P(84), get(), cue);
            tb.TextChanged += (s2, e2) => { set(tb.Text); Changed(); };
            var b = MakeButton("Browse…");
            b.Location = new Point(tb.Right + P(6), py - P(1));
            b.Click += (s2, e2) =>
            {
                if (kind == "folder")
                {
                    var picked = FolderPicker.Pick(this, "Choose a folder", tb.Text);
                    if (picked != null) tb.Text = picked;
                    return;
                }
                string filter = kind == "icon" ? "Pictures and icons|*.ico;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.exe;*.dll|All files|*.*"
                              : kind == "exe" ? "Programs|*.exe" : "Apps and shortcuts|*.exe;*.lnk;*.bat;*.cmd;*.msc;*.cpl|All files|*.*";
                using (var dlg = new OpenFileDialog { Filter = filter, DereferenceLinks = false })
                    if (dlg.ShowDialog(this) == DialogResult.OK) tb.Text = dlg.FileName;
            };
            props.Controls.Add(b);
            py += Math.Max(tb.Height, b.Height) + P(8);
            if (kind == "icon")
            {
                // a live note under the box: downloading / loaded / why a picture can't be used
                var status = new Label { Font = fSmall, ForeColor = cDim, AutoSize = true, Location = new Point(X1, py - P(6)) };
                props.Controls.Add(status);
                var timer = new Timer { Interval = 600 };
                void Update() { var st = Launcher.IconStatus(tb.Text); status.Text = st ?? ""; status.Visible = st != null; }
                timer.Tick += (s2, e2) => { if (status.IsDisposed) { timer.Dispose(); return; } Update(); };
                status.Disposed += (s2, e2) => timer.Dispose();
                Update();
                timer.Start();
                py += P(14);
            }
        }

        // A number. 0 in the settings = "use the default"; the box shows the default value.
        void NumberRow(string label, Func<int> get, Action<int> set, int? defValue, string hint, bool zeroIsReal = false, bool allowNegative = false)
        {
            RowLabel(label);
            string Show(int v) => zeroIsReal ? v.ToString() : v > 0 ? v.ToString() : defValue?.ToString() ?? "";
            var tb = Input(X1, P(64), Show(get()), defValue == null ? "Auto" : null);
            tb.KeyPress += (s2, e2) =>
            {
                if (char.IsControl(e2.KeyChar) || char.IsDigit(e2.KeyChar)) return;
                if (allowNegative && e2.KeyChar == '-' && tb.SelectionStart == 0) return;
                e2.Handled = true;
            };
            bool quiet = false;
            tb.TextChanged += (s2, e2) =>
            {
                if (quiet) return;
                string t = tb.Text.Trim();
                if (t.Length == 0 || t == "-") { set(zeroIsReal ? (defValue ?? 0) : 0); Changed(); return; }
                if (!int.TryParse(t, out int v)) return;
                if (!zeroIsReal && defValue.HasValue && v == defValue.Value && get() == 0) return;   // still the default
                set(v); Changed();
            };
            var px = new Label { Text = label.Contains("(") ? "" : "px", Font = fBody, ForeColor = cDim, AutoSize = true, Location = new Point(tb.Right + P(4), py + P(3)) };
            if (label.Contains("(ms)") || label.Contains("(pt)") || label.Contains("per row") || label.Contains("across") || label == "Rows") px.Text = "";
            props.Controls.Add(px);
            var d = MakeButton("Default", false, true);
            d.Location = new Point(tb.Right + P(28), py - P(1));
            d.Click += (s2, e2) =>
            {
                quiet = true;
                set(zeroIsReal ? (defValue ?? 0) : 0);
                tb.Text = Show(get());
                quiet = false;
                Changed();
            };
            props.Controls.Add(d);
            if (hint != null)
                props.Controls.Add(new Label { Text = hint, Font = fSmall, ForeColor = cDim, AutoSize = true, Location = new Point(d.Left + d.PreferredSize.Width + P(8), py + P(4)) });
            py += Math.Max(tb.Height, d.Height) + P(8);
        }

        // A color: type a hex value or pick one. Empty = default.
        void ColorRow(string label, Func<string> get, Action<string> set)
        {
            RowLabel(label);
            var sw = new Panel { Location = new Point(X1, py + P(1)), Size = new Size(P(22), P(22)), BorderStyle = BorderStyle.FixedSingle };
            props.Controls.Add(sw);
            var tb = Input(X1 + P(28), P(90), get() ?? "", "Default");
            void Swatch()
            {
                var c = Theme.Parse(tb.Text);
                sw.BackColor = c.HasValue ? Color.FromArgb(255, c.Value) : cSurface;
                sw.Invalidate();
            }
            sw.Paint += (s2, e2) =>
            {
                if (Theme.Parse(tb.Text) == null)
                    using (var pen = new Pen(cDim)) e2.Graphics.DrawLine(pen, 0, sw.Height, sw.Width, 0);
            };
            tb.TextChanged += (s2, e2) =>
            {
                string v = tb.Text.Trim();
                if (v.Length == 0 || Theme.Parse(v) != null) { set(v.Length == 0 ? "" : (v.StartsWith("#") ? v : "#" + v).ToUpperInvariant()); Changed(); }
                Swatch();
            };
            var pick = MakeButton("Pick…", false, true);
            pick.Location = new Point(tb.Right + P(6), py - P(1));
            pick.Click += (s2, e2) =>
            {
                ShowColorPalette(pick, Theme.Parse(tb.Text), hex => tb.Text = hex);
            };
            props.Controls.Add(pick);
            var d = MakeButton("Default", false, true);
            d.Location = new Point(pick.Left + pick.PreferredSize.Width + P(4), py - P(1));
            d.Click += (s2, e2) => tb.Text = "";
            props.Controls.Add(d);
            Swatch();
            py += Math.Max(tb.Height, P(24)) + P(8);
        }

        void ComboRow(string label, string[][] options, Func<string> get, Action<string> set, int width = 0)
        {
            RowLabel(label);
            var cb = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(X1, py), Width = width > 0 ? width : TW, FlatStyle = FlatStyle.Flat, BackColor = cInput, ForeColor = cText, Font = fBody };
            foreach (var o in options) cb.Items.Add(o[1]);
            if (dark) cb.HandleCreated += (s2, e2) => { try { SetWindowTheme(cb.Handle, "DarkMode_CFD", null); } catch { } };   // dark drop-down list
            cb.SelectedIndex = Math.Max(0, Array.FindIndex(options, o => o[0] == get()));
            cb.SelectedIndexChanged += (s2, e2) => { if (cb.SelectedIndex >= 0) { set(options[cb.SelectedIndex][0]); Changed(); } };
            props.Controls.Add(cb);
            py += cb.Height + P(8);
        }

        void CheckRow(string text, Func<bool> get, Action<bool> set)
        {
            var cb = new CheckBox { Text = text, Checked = get(), AutoSize = true, Location = new Point(X1, py), ForeColor = cText, Font = fBody, BackColor = Color.Transparent };
            cb.CheckedChanged += (s2, e2) => { set(cb.Checked); Changed(); };
            props.Controls.Add(cb);
            py += cb.PreferredSize.Height + P(6);
        }

        void Buttons(params (string text, Action act)[] list)
        {
            int x = X1;
            int h = 0;
            foreach (var (text, act) in list)
            {
                var b = MakeButton(text);
                b.Location = new Point(x, py);
                b.Click += (s2, e2) => Later(act);   // runs after the click finishes (the page may be rebuilt)
                props.Controls.Add(b);
                x += b.PreferredSize.Width + P(8);
                h = Math.Max(h, b.PreferredSize.Height);
            }
            py += h + P(8);
        }

        Button MakeButton(string text, bool accent = false, bool small = false)
        {
            var b = new Button
            {
                Text = text, FlatStyle = FlatStyle.Flat, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = small ? new Padding(P(4), 0, P(4), 0) : new Padding(P(8), P(1), P(8), P(1)), Font = small ? fSmall : fBody, Cursor = Cursors.Hand,
                BackColor = accent ? cAccent : cInput, ForeColor = accent ? Color.White : cText, Margin = new Padding(0, 0, P(8), 0)
            };
            b.FlatAppearance.BorderColor = accent ? cAccent : cBorder;
            b.FlatAppearance.MouseOverBackColor = accent ? ControlPaint.Light(cAccent, 0.15f) : cHover;
            b.FlatAppearance.MouseDownBackColor = accent ? ControlPaint.Dark(cAccent, 0.05f) : cHover;
            return b;
        }

        // ---- "Command (like Win+R)" dialog ----
        class CommandDialog : Form
        {
            readonly TextBox cmd, name;
            public string Command => cmd.Text;
            public string ItemName => name.Text;

            public CommandDialog(SettingsForm f)
            {
                Text = "Add a command";
                FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false; ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent; AutoScaleMode = AutoScaleMode.None;
                BackColor = f.cSurface; ForeColor = f.cText; Font = f.fBody;
                ClientSize = new Size(f.P(460), f.P(210));
                int y = f.P(16);
                Controls.Add(new Label { Text = "Anything you'd type in the Run box (Win+R), for example mmsys.cpl, control printers, shell:startup or ms-settings:display", AutoSize = false, Location = new Point(f.P(16), y), Size = new Size(f.P(428), f.P(36)), ForeColor = f.cDim });
                y += f.P(42);
                Controls.Add(new Label { Text = "Command", AutoSize = true, Location = new Point(f.P(16), y + f.P(3)) });
                cmd = new TextBox { Location = new Point(f.P(110), y), Width = f.P(334), BorderStyle = BorderStyle.FixedSingle, BackColor = f.cInput, ForeColor = f.cText };
                Controls.Add(cmd);
                y += f.P(34);
                Controls.Add(new Label { Text = "Name", AutoSize = true, Location = new Point(f.P(16), y + f.P(3)) });
                name = new TextBox { Location = new Point(f.P(110), y), Width = f.P(334), BorderStyle = BorderStyle.FixedSingle, BackColor = f.cInput, ForeColor = f.cText };
                name.HandleCreated += (s2, e2) => { try { SendMessage(name.Handle, 0x1501, (IntPtr)1, "Optional"); } catch { } };
                Controls.Add(name);
                y += f.P(44);
                var ok = f.MakeButton("Add", true); ok.DialogResult = DialogResult.OK; ok.Location = new Point(f.P(300), y);
                var cancel = f.MakeButton("Cancel"); cancel.DialogResult = DialogResult.Cancel; cancel.Location = new Point(f.P(370), y);
                Controls.Add(ok); Controls.Add(cancel);
                AcceptButton = ok; CancelButton = cancel;
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                try { if (BackColor.R < 128) { int on = 1; if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0) Native.DwmSetWindowAttribute(Handle, 19, ref on, 4); } } catch { }
            }
        }
    }
}
