using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CLTaskbar
{
    // ---------------------------------------------------------------------------------------------
    // Settings model. Saved as TaskbarGroups.settings.json next to the .exe — nothing goes into Windows.
    // ---------------------------------------------------------------------------------------------

    public class BrowserOptions
    {
        public string Browser { get; set; } = "default";   // default | chrome | edge | firefox | brave | opera | vivaldi | custom
        public string CustomPath { get; set; } = "";
        public string OpenMode { get; set; } = "tabHere";   // newWindow | tabHere | tabHereElseSwitch | tabRecent
        public bool PreferVisible { get; set; } = true;     // prefer windows that aren't minimized
        public bool Private { get; set; }

        public BrowserOptions Clone() => (BrowserOptions)MemberwiseClone();
    }

    public class LaunchItem
    {
        public string Type { get; set; } = "program";       // program | web | folder | separator
        public string Name { get; set; } = "";
        public string Target { get; set; } = "";            // program path / command / shell: / ms-settings: ... or URL for web
        public string Arguments { get; set; } = "";
        public string StartIn { get; set; } = "";
        public bool RunAsAdmin { get; set; }
        public string IconPath { get; set; } = "";          // optional custom icon (.ico, .exe, .dll, .png)
        public bool UseMenuBrowser { get; set; } = true;    // web links: use the menu's (or group's) browser
        public string OpenModeOverride { get; set; } = "";  // ...but open it here instead ("" = same as the menu)
        public string PrivateOverride { get; set; } = "";   // ...but as "regular" or "private" ("" = same as the menu)
        public BrowserOptions Browser { get; set; } = new BrowserOptions();
        public List<LaunchItem> Children { get; set; } = new List<LaunchItem>();  // for folders (sub-menus)

        // Look (in a popup menu; BackColor also tints a pinned button on the bar)
        public bool ShowText { get; set; } = true;
        public string TextColor { get; set; } = "";
        public string BackColor { get; set; } = "";
        public int Height { get; set; }                     // 0 = the menu's default
        public int IconSize { get; set; }                   // 0 = the menu's default; set large for a picture
        public bool Bold { get; set; }

        public LaunchItem Clone()
        {
            var c = (LaunchItem)MemberwiseClone();
            c.Browser = Browser?.Clone() ?? new BrowserOptions();
            c.Children = Children?.Select(x => x.Clone()).ToList() ?? new List<LaunchItem>();
            return c;
        }
    }

    public class BarSection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Type { get; set; } = "pins";          // pins (group of icons) | menu | item (one button) | windows | gap
        public string Name { get; set; } = "";

        // pins + menu
        public List<LaunchItem> Items { get; set; } = new List<LaunchItem>();

        // menu
        public string Label { get; set; } = "";             // text shown on the bar next to the icon
        public bool ShowLabel { get; set; }
        public bool ShowIcon { get; set; } = true;
        public string IconPath { get; set; } = "";
        public BrowserOptions Browser { get; set; } = new BrowserOptions();

        // windows
        public bool GroupByDesktop { get; set; } = true;
        public string FlatScope { get; set; } = "current";  // current | all (when not grouping by desktop)
        public string DesktopLabels { get; set; } = "number"; // number | name | none
        public bool ShowEmptyDesktops { get; set; } = true;
        public int DesktopGap { get; set; } = 18;
        public bool CombineWindows { get; set; } = true;

        // gap
        public int Width { get; set; } = 16;
        public bool Divider { get; set; }

        // groups of icons: "row" (normal taskbar buttons) or "grid" (small icons, like the notification area)
        public string Style { get; set; } = "row";
        public int GridRows { get; set; } = 2;
        public int GridColumns { get; set; } = 4;              // popup grid: icons per row (when GridShape is "grid")
        public string GridShape { get; set; } = "grid";        // popup grid: grid | row (all side by side) | column (all stacked)
        public int GridIconSize { get; set; }                 // 0 = fit the rows

        // Look on the bar
        public string BackColor { get; set; } = "";         // background behind this section's buttons
        public string TextColor { get; set; } = "";         // menu button text / desktop label text
        public string LabelBackColor { get; set; } = "";    // desktops: behind the number/name only

        // Look of the popup menu (menu buttons)
        public int MenuWidth { get; set; }                  // 0 = fit the items
        public int MenuItemHeight { get; set; }             // 0 = automatic
        public int MenuIconSize { get; set; }               // 0 = 16
        public float MenuFontSize { get; set; }             // 0 = Windows default (9)
        public string MenuBackColor { get; set; } = "";
        public string MenuTextColor { get; set; } = "";
        public string MenuHoverColor { get; set; } = "";
    }

    // Custom look for an app that isn't pinned (kept only once you change something)
    public class AppStyle
    {
        public string Key { get; set; } = "";               // which app (its app ID or program path)
        public string AppName { get; set; } = "";           // what the app was called when you customized it
        public string ExePath { get; set; } = "";
        public string Name { get; set; } = "";              // your name for it (tooltips and menus)
        public string IconPath { get; set; } = "";
        public string BackColor { get; set; } = "";
        public bool RunAsAdmin { get; set; }                // new windows of it open as administrator
        public string Arguments { get; set; } = "";         // ...with these arguments
        public string StartIn { get; set; } = "";           // ...in this folder
        [ScriptIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Name) && string.IsNullOrWhiteSpace(IconPath) && string.IsNullOrWhiteSpace(BackColor) && !RunAsAdmin
                                              && string.IsNullOrWhiteSpace(Arguments) && string.IsNullOrWhiteSpace(StartIn);
        [ScriptIgnore] public bool ChangesLaunch => RunAsAdmin || !string.IsNullOrWhiteSpace(Arguments) || !string.IsNullOrWhiteSpace(StartIn);
    }

    // The order you dragged open apps into, for one desktop (new apps go at the end)
    public class WindowOrder
    {
        public string Desk { get; set; } = "";              // desktop id, or "flat"
        public List<string> Keys { get; set; } = new List<string>();
    }

    // Custom label / picture / colors for one virtual desktop's group on the bar
    public class DesktopStyle
    {
        public string Id { get; set; } = "";
        public string Label { get; set; } = "";             // replaces the number/name
        public string ImagePath { get; set; } = "";
        public string BackColor { get; set; } = "";
        public string TextColor { get; set; } = "";
        public string LabelBackColor { get; set; } = "";    // behind the number/name only
        public List<LaunchItem> Pins { get; set; } = new List<LaunchItem>();   // apps pinned to this desktop
        [ScriptIgnore] public bool IsEmpty => string.IsNullOrWhiteSpace(Label) && string.IsNullOrWhiteSpace(ImagePath) && string.IsNullOrWhiteSpace(BackColor) && string.IsNullOrWhiteSpace(TextColor) && string.IsNullOrWhiteSpace(LabelBackColor) && (Pins == null || Pins.Count == 0);
    }

    public class Config
    {
        public int Version { get; set; } = 3;

        // Appearance
        public string Color { get; set; } = "sample";      // sample | theme | #RRGGBB
        public int IconSize { get; set; } = 0;             // 0 = match Windows
        public int ButtonWidth { get; set; } = 0;          // 0 = match Windows
        public int SectionGap { get; set; } = 18;          // space between sections on the bar
        public bool ShowDividers { get; set; } = true;
        public bool HighlightCurrentDesktop { get; set; } = true;
        public bool ShowPreviews { get; set; } = true;
        public int PreviewDelayMs { get; set; } = 350;
        public int LeftOffset { get; set; }
        public int RightOffset { get; set; }

        // Behavior
        public bool HideWhenFullscreen { get; set; } = true;
        public bool ClickCyclesWindows { get; set; } = true;   // clicking an app with several windows brings the next one forward
        public string IndicatorColor { get; set; } = "";         // the line under apps that are open (empty = automatic)
        public string ActiveIndicatorColor { get; set; } = "";   // the line under the app you're using (empty = automatic)
        public string HighlightColor { get; set; } = "";       // color behind the desktop you're on (empty = automatic)
        public List<string> HiddenMenuItems { get; set; } = new List<string>();   // (older versions) right-click options turned off
        public Dictionary<string, bool> MenuChoices { get; set; } = new Dictionary<string, bool>();   // right-click options you turned on or off
        public List<string> MenuOrder { get; set; } = new List<string>();
        public bool MenuEditorEnabledOnly { get; set; }
        public bool DesktopPinsFirst { get; set; }
        public bool WindowsAppMenus { get; set; }
        public bool ShowBadges { get; set; } = true;          // unread counts on apps like Teams and Outlook
        public bool CountNotificationBadges { get; set; }     // ...also count notifications waiting in the notification center
        public bool OutlookUnread { get; set; } = true;       // classic Outlook: unread email in your inboxes
        public string BadgeColor { get; set; } = "";          // empty = red
        public bool NarrowWindowList { get; set; } = true;    // shorten long window titles in the right-click menu
        public int MenuMaxWidth { get; set; }                 // ...so the menu is at most this wide (0 = Windows' own width)             // right-clicking an app shows only Windows' own menu            // apps pinned to a desktop jump to the front of its group
        public string SettingsTheme { get; set; } = "dark";   // the settings window: dark (default) | light | auto (match Windows)
        public bool SettingsThemeDefaulted { get; set; }        // older settings files said "auto" only because that was the default                                                 // settings: list only the options that are on                             // your order of right-click options
        public bool ShowTrayIcon { get; set; } = true;

        public List<BarSection> Layout { get; set; } = new List<BarSection>();
        public List<AppStyle> Apps { get; set; } = new List<AppStyle>();
        public List<DesktopStyle> Desktops { get; set; } = new List<DesktopStyle>();
        public List<WindowOrder> Orders { get; set; } = new List<WindowOrder>();   // your drag order of open apps, per desktop

        public WindowOrder OrderFor(string desk, bool create)
        {
            var o = Orders.FirstOrDefault(x => x.Desk == desk);
            if (o == null && create) { o = new WindowOrder { Desk = desk }; Orders.Add(o); }
            return o;
        }

        public AppStyle FindApp(string key) => Apps.FirstOrDefault(a => string.Equals(a.Key, key, StringComparison.OrdinalIgnoreCase));
        public DesktopStyle FindDesktop(Guid id) => Desktops.FirstOrDefault(d => string.Equals(d.Id, id.ToString("N"), StringComparison.OrdinalIgnoreCase));
        public DesktopStyle DesktopFor(Guid id)
        {
            var d = FindDesktop(id);
            if (d == null) { d = new DesktopStyle { Id = id.ToString("N") }; Desktops.Add(d); }
            return d;
        }

        // Drop entries that no longer customize anything
        public void Prune()
        {
            Apps.RemoveAll(a => a.IsEmpty);
            Desktops.RemoveAll(d => d.IsEmpty);
        }

        // ----- sizing helpers -----
        public int EffectiveIconSize(bool small) => IconSize > 0 ? IconSize : (small ? 16 : 24);
        public int EffectiveButtonWidth(bool small) => ButtonWidth > 0 ? ButtonWidth : (Theme.IsWin10 ? (small ? 40 : 48) : 44);

        // ----- storage -----
        public static string Dir => AppDomain.CurrentDomain.BaseDirectory;
        public static string FilePath => Path.Combine(Dir, "CL-Taskbar.settings.json");
        static string LegacyIni => Path.Combine(Dir, "TaskbarGroups.ini");

        public static Config Load()
        {
            Config c = null;
            try
            {
                // Coming from the older name (TaskbarGroups): bring your settings along
                string old = Path.Combine(Dir, "TaskbarGroups.settings.json");
                if (!File.Exists(FilePath) && File.Exists(old)) File.Copy(old, FilePath);
                if (File.Exists(FilePath))
                {
                    var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                    c = js.Deserialize<Config>(File.ReadAllText(FilePath));
                }
            }
            catch (Exception ex)
            {
                // Keep a copy of a broken file instead of silently overwriting it
                try { File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                Program.Log(ex);
                c = null;
            }
            if (c == null)
            {
                c = new Config();
                c.ImportLegacyIni(out bool showPinned, out var windows);
                c.Layout = DefaultLayout(showPinned, windows);
                c.Save();
            }
            c.Normalize();
            return c;
        }

        public void Normalize()
        {
            if (Layout == null) Layout = new List<BarSection>();
            if (Apps == null) Apps = new List<AppStyle>();
            if (Desktops == null) Desktops = new List<DesktopStyle>();
            if (HiddenMenuItems == null) HiddenMenuItems = new List<string>();
            if (MenuChoices == null) MenuChoices = new Dictionary<string, bool>();
            if (MenuOrder == null) MenuOrder = new List<string>();
            // dark became the default for the settings window: switch over once, then respect whatever is chosen
            if (!SettingsThemeDefaulted) { if (string.IsNullOrEmpty(SettingsTheme) || SettingsTheme == "auto") SettingsTheme = "dark"; SettingsThemeDefaulted = true; }
            foreach (var h in HiddenMenuItems) MenuChoices[h] = false;   // carry over choices from the older list
            HiddenMenuItems.Clear();
            if (Orders == null) Orders = new List<WindowOrder>();
            foreach (var o in Orders) if (o.Keys == null) o.Keys = new List<string>();
            foreach (var d in Desktops) if (d.Pins == null) d.Pins = new List<LaunchItem>();
            foreach (var s in Layout)
            {
                if (s.Items == null) s.Items = new List<LaunchItem>();
                if (s.Browser == null) s.Browser = new BrowserOptions();
                if (string.IsNullOrEmpty(s.Id)) s.Id = Guid.NewGuid().ToString("N");
                foreach (var it in Walk(s.Items)) { if (it.Children == null) it.Children = new List<LaunchItem>(); if (it.Browser == null) it.Browser = new BrowserOptions(); }
            }
        }

        public static IEnumerable<LaunchItem> Walk(IEnumerable<LaunchItem> items)
        {
            foreach (var i in items)
            {
                yield return i;
                if (i.Children != null) foreach (var c in Walk(i.Children)) yield return c;
            }
        }

        public void Save()
        {
            try
            {
                Prune();
                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                string json = Pretty(js.Serialize(this));
                string tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch (Exception ex) { Program.Log(ex); }
        }

        // ----- defaults -----
        public static List<BarSection> DefaultLayout(bool includePins, BarSection windows)
        {
            var list = new List<BarSection>();
            if (includePins)
            {
                var pins = new BarSection { Type = "pins", Name = "Pinned apps" };
                pins.Items = ImportWindowsPins();
                list.Add(pins);
            }
            list.Add(windows ?? new BarSection { Type = "windows", Name = "Open windows" });
            return list;
        }

        // Copies your Windows taskbar pins (the .lnk files) into our own folder, so our list is independent.
        public static List<LaunchItem> ImportWindowsPins()
        {
            var items = new List<LaunchItem>();
            try
            {
                string pinDir = Path.Combine(Dir, "Pins");
                Directory.CreateDirectory(pinDir);
                foreach (var p in WindowTracker.ReadPinned())
                {
                    if (p.Aumid != null) { items.Add(new LaunchItem { Type = "program", Name = p.Name, Target = @"shell:AppsFolder\" + p.Aumid }); continue; }
                    string dest = Path.Combine(pinDir, Path.GetFileName(p.LnkPath));
                    try { File.Copy(p.LnkPath, dest, true); } catch { dest = p.LnkPath; }
                    items.Add(new LaunchItem { Type = "program", Name = p.Name, Target = dest });
                }
            }
            catch (Exception ex) { Program.Log(ex); }
            return items;
        }

        void ImportLegacyIni(out bool showPinned, out BarSection windows)
        {
            showPinned = true;
            windows = new BarSection { Type = "windows", Name = "Open windows" };
            try
            {
                if (!File.Exists(LegacyIni)) return;
                foreach (var raw in File.ReadAllLines(LegacyIni))
                {
                    var line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (line.StartsWith(";") || eq < 0) continue;
                    string k = line.Substring(0, eq).Trim().ToLowerInvariant(), v = line.Substring(eq + 1).Trim();
                    bool b = v.Equals("true", StringComparison.OrdinalIgnoreCase);
                    int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);
                    switch (k)
                    {
                        case "groupgap": windows.DesktopGap = n; break;
                        case "combinewindows": windows.CombineWindows = b; break;
                        case "showpinned": showPinned = b; break;
                        case "showemptydesktops": windows.ShowEmptyDesktops = b; break;
                        case "desktoplabels": windows.DesktopLabels = v.ToLowerInvariant(); break;
                        case "highlightcurrentdesktop": HighlightCurrentDesktop = b; break;
                        case "showdividers": ShowDividers = b; break;
                        case "color": Color = v; break;
                        case "leftoffset": LeftOffset = n; break;
                        case "rightoffset": RightOffset = n; break;
                        case "hidewhenfullscreen": HideWhenFullscreen = b; break;
                        case "showtrayicon": ShowTrayIcon = b; break;
                        case "showpreviews": ShowPreviews = b; break;
                        case "previewdelayms": PreviewDelayMs = n; break;
                    }
                }
                File.Move(LegacyIni, LegacyIni + ".old");
            }
            catch { }
        }

        // Minimal JSON pretty-printer so the file is readable
        static string Pretty(string json)
        {
            var sb = new StringBuilder();
            int indent = 0; bool inStr = false, esc = false;
            for (int i = 0; i < json.Length; i++)
            {
                char c = json[i];
                if (inStr)
                {
                    sb.Append(c);
                    if (esc) esc = false;
                    else if (c == '\\') esc = true;
                    else if (c == '"') inStr = false;
                    continue;
                }
                switch (c)
                {
                    case '"': inStr = true; sb.Append(c); break;
                    case '{': case '[':
                        sb.Append(c);
                        if (i + 1 < json.Length && (json[i + 1] == '}' || json[i + 1] == ']')) { sb.Append(json[++i]); break; }
                        sb.AppendLine(); sb.Append(' ', ++indent * 2); break;
                    case '}': case ']':
                        sb.AppendLine(); sb.Append(' ', Math.Max(0, --indent) * 2); sb.Append(c); break;
                    case ',':
                        sb.Append(c); sb.AppendLine(); sb.Append(' ', indent * 2); break;
                    case ':':
                        sb.Append(": "); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }
    }

    // Ready-made shortcuts to Windows tools, Settings pages and folders.
    public static class Presets
    {
        public class Preset
        {
            public string Category, Name, Target, Args;
            public LaunchItem Clone() => new LaunchItem { Type = "program", Name = Name, Target = Target, Arguments = Args ?? "" };
        }

        public static readonly List<Preset> All = new List<Preset>();

        static void Add(string cat, string name, string target, string args = "") =>
            All.Add(new Preset { Category = cat, Name = name, Target = target, Args = args });

        static Presets()
        {
            const string T = "System tools", C = "Classic Control Panel", M = "Management consoles", S = "Settings pages", F = "Folders";
            Add(T, "Control Panel", "control.exe");
            Add(T, "Task Manager", "taskmgr.exe");
            Add(T, "Registry Editor", "regedit.exe");
            Add(T, "System Configuration (msconfig)", "msconfig.exe");
            Add(T, "System Information", "msinfo32.exe");
            Add(T, "DirectX Diagnostic Tool", "dxdiag.exe");
            Add(T, "Resource Monitor", "resmon.exe");
            Add(T, "Disk Cleanup", "cleanmgr.exe");
            Add(T, "Optimize Drives", "dfrgui.exe");
            Add(T, "System Restore", "rstrui.exe");
            Add(T, "Windows Features", "optionalfeatures.exe");
            Add(T, "Volume Mixer (classic)", "sndvol.exe");
            Add(T, "Character Map", "charmap.exe");
            Add(T, "On-Screen Keyboard", "osk.exe");
            Add(T, "Remote Desktop Connection", "mstsc.exe");
            Add(T, "About Windows (winver)", "winver.exe");
            Add(T, "Command Prompt", "cmd.exe");
            Add(T, "PowerShell", "powershell.exe");
            Add(T, "Windows Terminal", "wt.exe");
            Add(T, "Run dialog", "explorer.exe", "shell:::{2559a1f3-21d7-11d4-bdaf-00c04f60b9f0}");
            Add(T, "All Settings (God Mode)", "shell:::{ED7BA470-8E54-465E-825C-99712043E01C}");

            Add(C, "Sound: Playback devices", "rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,0");
            Add(C, "Sound: Recording devices", "rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,1");
            Add(C, "Sound: Sound scheme", "rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,2");
            Add(C, "Sound: Communications", "rundll32.exe", "shell32.dll,Control_RunDLL mmsys.cpl,,3");
            Add(C, "Network Connections (adapters)", "ncpa.cpl");
            Add(C, "Network and Sharing Center", "control.exe", "/name Microsoft.NetworkAndSharingCenter");
            Add(C, "Internet Options", "inetcpl.cpl");
            Add(C, "Programs and Features", "appwiz.cpl");
            Add(C, "Default Programs", "control.exe", "/name Microsoft.DefaultPrograms");
            Add(C, "Power Options", "powercfg.cpl");
            Add(C, "Mouse Properties", "main.cpl");
            Add(C, "Keyboard Properties", "control.exe", "keyboard");
            Add(C, "Game Controllers", "joy.cpl");
            Add(C, "Region (formats)", "intl.cpl");
            Add(C, "Date and Time", "timedate.cpl");
            Add(C, "Display (classic)", "desk.cpl");
            Add(C, "Color Management", "colorcpl.exe");
            Add(C, "ClearType Text Tuner", "cttune.exe");
            Add(C, "Fonts", "control.exe", "fonts");
            Add(C, "Devices and Printers", "control.exe", "printers");
            Add(C, "Bluetooth Devices (classic)", "control.exe", "bthprops.cpl");
            Add(C, "System Properties", "sysdm.cpl");
            Add(C, "System Properties: Advanced", "SystemPropertiesAdvanced.exe");
            Add(C, "Performance Options (virtual memory)", "SystemPropertiesPerformance.exe");
            Add(C, "System Protection", "SystemPropertiesProtection.exe");
            Add(C, "Remote Settings", "SystemPropertiesRemote.exe");
            Add(C, "Environment Variables", "rundll32.exe", "sysdm.cpl,EditEnvironmentVariables");
            Add(C, "User Accounts (netplwiz)", "netplwiz.exe");
            Add(C, "Credential Manager", "control.exe", "/name Microsoft.CredentialManager");
            Add(C, "Windows Firewall", "firewall.cpl");
            Add(C, "Security and Maintenance", "wscui.cpl");
            Add(C, "Folder Options", "control.exe", "folders");
            Add(C, "Indexing Options", "control.exe", "srchadmin.dll");
            Add(C, "AutoPlay", "control.exe", "/name Microsoft.AutoPlay");
            Add(C, "Ease of Access Center", "control.exe", "access.cpl");
            Add(C, "Speech Recognition", "control.exe", "/name Microsoft.SpeechRecognition");
            Add(C, "File History", "control.exe", "/name Microsoft.FileHistory");
            Add(C, "Backup and Restore", "sdclt.exe");
            Add(C, "Recovery (classic)", "control.exe", "/name Microsoft.Recovery");
            Add(C, "BitLocker", "control.exe", "/name Microsoft.BitLockerDriveEncryption");
            Add(C, "Troubleshooting", "control.exe", "/name Microsoft.Troubleshooting");
            Add(C, "Windows Tools / Administrative Tools", "control.exe", "admintools");
            Add(C, "Device Installation Settings", "rundll32.exe", "newdev.dll,DeviceInternetSettingUi");

            Add(M, "Device Manager", "devmgmt.msc");
            Add(M, "Disk Management", "diskmgmt.msc");
            Add(M, "Computer Management", "compmgmt.msc");
            Add(M, "Services", "services.msc");
            Add(M, "Event Viewer", "eventvwr.msc");
            Add(M, "Task Scheduler", "taskschd.msc");
            Add(M, "Performance Monitor", "perfmon.msc");
            Add(M, "Windows Firewall (advanced)", "wf.msc");
            Add(M, "Certificates (my account)", "certmgr.msc");
            Add(M, "Shared Folders", "fsmgmt.msc");
            Add(M, "Print Management", "printmanagement.msc");
            Add(M, "Local Users and Groups", "lusrmgr.msc");
            Add(M, "Local Security Policy", "secpol.msc");
            Add(M, "Group Policy Editor", "gpedit.msc");

            Add(S, "Settings", "ms-settings:");
            Add(S, "Display", "ms-settings:display");
            Add(S, "Sound", "ms-settings:sound");
            Add(S, "Sound devices", "ms-settings:sound-devices");
            Add(S, "App volume and device preferences", "ms-settings:apps-volume");
            Add(S, "Notifications", "ms-settings:notifications");
            Add(S, "Power & sleep", "ms-settings:powersleep");
            Add(S, "Storage", "ms-settings:storagesense");
            Add(S, "Multitasking", "ms-settings:multitasking");
            Add(S, "Clipboard", "ms-settings:clipboard");
            Add(S, "About this PC", "ms-settings:about");
            Add(S, "Bluetooth & devices", "ms-settings:bluetooth");
            Add(S, "Printers & scanners", "ms-settings:printers");
            Add(S, "Mouse", "ms-settings:mousetouchpad");
            Add(S, "Wi-Fi", "ms-settings:network-wifi");
            Add(S, "Network status", "ms-settings:network-status");
            Add(S, "VPN", "ms-settings:network-vpn");
            Add(S, "Proxy", "ms-settings:network-proxy");
            Add(S, "Personalization", "ms-settings:personalization");
            Add(S, "Background", "ms-settings:personalization-background");
            Add(S, "Colors", "ms-settings:colors");
            Add(S, "Taskbar", "ms-settings:taskbar");
            Add(S, "Night light", "ms-settings:nightlight");
            Add(S, "Apps & features", "ms-settings:appsfeatures");
            Add(S, "Default apps", "ms-settings:defaultapps");
            Add(S, "Startup apps", "ms-settings:startupapps");
            Add(S, "Your account", "ms-settings:yourinfo");
            Add(S, "Sign-in options", "ms-settings:signinoptions");
            Add(S, "Date & time", "ms-settings:dateandtime");
            Add(S, "Language & region", "ms-settings:regionlanguage");
            Add(S, "Privacy", "ms-settings:privacy");
            Add(S, "Windows Update", "ms-settings:windowsupdate");
            Add(S, "Recovery", "ms-settings:recovery");
            Add(S, "Windows Security", "windowsdefender:");

            Add(F, "This PC", "shell:MyComputerFolder");
            Add(F, "Downloads", "shell:Downloads");
            Add(F, "Documents", "shell:Personal");
            Add(F, "Desktop", "shell:Desktop");
            Add(F, "Pictures", "shell:My Pictures");
            Add(F, "Recycle Bin", "shell:RecycleBinFolder");
            Add(F, "Startup folder", "shell:startup");
            Add(F, "AppData (Roaming)", "%APPDATA%");
            Add(F, "Temp files", "%TEMP%");
            Add(F, "Program Files", "shell:ProgramFiles");
            Add(F, "Network", "shell:NetworkPlacesFolder");
        }
    }
}
