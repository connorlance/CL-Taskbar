using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CLTaskbar
{
    // Opens launch items (programs, Windows tools, folders, web links) and finds their icons.
    internal static class Launcher
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        static extern int SearchPath(string path, string file, string ext, int len, StringBuilder buf, IntPtr filePart);

        const string SettingsAumid = @"shell:AppsFolder\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
        const string SecurityAumid = @"shell:AppsFolder\Microsoft.Windows.SecHealthUI_cw5n1h2txyewy!SecHealthUI";

        static readonly Dictionary<string, Bitmap> iconCache = new Dictionary<string, Bitmap>();
        static readonly HashSet<string> pendingFavicons = new HashSet<string>();
        public static event Action IconsChanged;
        static SynchronizationContext ui;

        public static void Init() { ui = SynchronizationContext.Current; }

        public static string Expand(string s) => string.IsNullOrEmpty(s) ? "" : Environment.ExpandEnvironmentVariables(s.Trim().Trim('"'));

        // "devmgmt.msc" -> "C:\Windows\System32\devmgmt.msc"
        public static string Resolve(string target)
        {
            string t = Expand(target);
            if (t.Length == 0 || t.Contains(":") || t.StartsWith("\\\\")) return t;
            try
            {
                var sb = new StringBuilder(1024);
                if (SearchPath(null, t, null, sb.Capacity, sb, IntPtr.Zero) > 0) return sb.ToString();
                if (!Path.HasExtension(t) && SearchPath(null, t, ".exe", sb.Capacity, sb, IntPtr.Zero) > 0) return sb.ToString();
            }
            catch { }
            return t;
        }

        // ================= icons =================

        public static void ClearIcons()
        {
            foreach (var b in iconCache.Values) b.Dispose();
            iconCache.Clear();
        }

        public static Bitmap IconFor(LaunchItem it, int px, BrowserOptions menuBrowser = null)
        {
            if (it == null) return null;
            string key = it.Type + "|" + it.IconPath + "|" + it.Target + "|" + px;
            if (it.Type == "web")
            {
                // a link without its own icon may show its browser's icon: keep one per browser, never shared
                var eb = EffectiveBrowser(it, menuBrowser);
                key += "|" + eb.Browser + "|" + eb.CustomPath + "|" + (eb.Private ? "private" : "");
            }
            if (iconCache.TryGetValue(key, out var bmp)) return bmp;
            bmp = LoadIcon(it, px, menuBrowser, out bool cacheable);
            if (bmp == null) bmp = Scale(SystemIcons.Application.ToBitmap(), px);
            if (cacheable) iconCache[key] = bmp;
            return bmp;
        }

        static Bitmap LoadIcon(LaunchItem it, int px, BrowserOptions menuBrowser, out bool cacheable)
        {
            cacheable = true;
            if (!string.IsNullOrWhiteSpace(it.IconPath))
            {
                var custom = FromIconFile(Expand(it.IconPath), px);
                if (custom != null) return custom;
            }
            if (it.Type == "folder") return Icons.FromShell(Environment.GetFolderPath(Environment.SpecialFolder.Windows), px)
                                        ?? Icons.FromShell("shell:Personal", px);
            if (it.Type == "web")
            {
                // a link that opens a private (incognito) window shows that browser's private icon
                var eb = EffectiveBrowser(it, menuBrowser);
                if (eb.Private)
                {
                    var pi = PrivateIcons.For(ResolveBrowser(eb).Exe, px);
                    if (pi != null) return pi;
                }
                string host = HostOf(it.Target);
                if (host != null)
                {
                    string file = FaviconPath(host);
                    if (File.Exists(file)) { var f = FromIconFile(file, px); if (f != null) return f; }
                    else { FetchFavicon(host); cacheable = false; }
                }
                var b = ResolveBrowser(EffectiveBrowser(it, menuBrowser));
                return b.Exe != null ? Icons.FromShell(b.Exe, px) : null;
            }
            return IconForTarget(it.Target, px);
        }

        public static Bitmap IconForTarget(string target, int px)
        {
            string t = Expand(target);
            if (t.Length == 0) return null;
            if (t.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase))
                return Icons.FromShell(SettingsAumid, px)
                    ?? Icons.FromShell(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"ImmersiveControlPanel\SystemSettings.exe"), px);
            if (t.StartsWith("windowsdefender:", StringComparison.OrdinalIgnoreCase))
                return Icons.FromShell(SecurityAumid, px);
            if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return null;
            if (t.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase))
            {
                // Store apps: use the app's own program icon when there is one (the tile icon can have a plate behind it)
                var exe = AppResolve.PackageExe(t.Substring(17));
                if (exe != null) { var b = Icons.FromShell(exe, px); if (b != null) return b; }
            }
            return Icons.FromShell(Resolve(t), px) ?? Icons.FromShell(t, px);
        }

        // Same as FromIconFile, but remembered until settings change
        public static Bitmap FromIconFileCached(string path, int px)
        {
            string key = "file|" + path + "|" + px;
            if (iconCache.TryGetValue(key, out var b)) return b;
            b = FromIconFile(path, px);
            if (b != null) iconCache[key] = b;
            return b;
        }

        public static Bitmap FromIconFile(string path, int px)
        {
            if (path.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                // A picture written into the address itself ("data:image/png;base64,...", what Google Images often copies)
                string local = WebImagePath(path);
                if (!File.Exists(local) && !SaveDataUri(path, local)) return null;
                path = local;
            }
            else if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // A picture on the web: downloaded once and kept in the Cache folder
                string local = WebImagePath(path);
                if (!File.Exists(local)) { FetchWebImage(path, local); return null; }
                path = local;
            }
            try
            {
                if (!File.Exists(path)) return Icons.FromShell(path, px);
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".bmp" || ext == ".gif" || ext == ".webp" || ext == ".tif" || ext == ".tiff" || path.Contains(@"\Cache\images\"))
                {
                    try
                    {
                        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        using (var img = Image.FromStream(fs)) return Scale(new Bitmap(img), px);
                    }
                    catch
                    {
                        // formats GDI+ can't read (WebP and others): let Windows decode it
                        var w = Wic.Load(path);
                        if (w != null) return Scale(w, px);
                        try { using (var ico = new Icon(path, px, px)) return Scale(ico.ToBitmap(), px); } catch { }
                        return null;
                    }
                }
                if (ext == ".ico")
                    using (var ico = new Icon(path, px, px)) return Scale(ico.ToBitmap(), px);
                return Icons.FromShell(path, px);
            }
            catch { return null; }
        }

        static Bitmap Scale(Bitmap src, int px)
        {
            if (src.Width == px && src.Height == px) return src;
            var b = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(src, new Rectangle(0, 0, px, px));
            }
            src.Dispose();
            return b;
        }

        public static string HostOf(string url)
        {
            try
            {
                string u = url.Trim();
                if (!u.Contains("://")) u = "https://" + u;
                return new Uri(u).Host.ToLowerInvariant();
            }
            catch { return null; }
        }

        static string WebImagePath(string url)
        {
            string ext = ".png";
            try { var e = Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant(); if (e == ".ico" || e == ".jpg" || e == ".jpeg" || e == ".gif" || e == ".bmp" || e == ".png") ext = e; } catch { }
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var h = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-", "").Substring(0, 16);
                return Path.Combine(Config.Dir, "Cache", "images", h + ext);
            }
        }

        // What happened with a web picture, for the settings window to show
        static readonly Dictionary<string, string> webStatus = new Dictionary<string, string>();

        static bool SaveDataUri(string uri, string file)
        {
            try
            {
                int comma = uri.IndexOf(',');
                if (comma < 0 || uri.Substring(0, comma).IndexOf("base64", StringComparison.OrdinalIgnoreCase) < 0) { lock (webStatus) webStatus[uri] = "That picture address isn't in a format I can read."; return false; }
                var data = Convert.FromBase64String(uri.Substring(comma + 1).Trim());
                Directory.CreateDirectory(Path.GetDirectoryName(file));
                File.WriteAllBytes(file, data);
                return true;
            }
            catch (Exception ex) { lock (webStatus) webStatus[uri] = "Couldn't read that picture: " + ex.Message; return false; }
        }

        static void FetchWebImage(string url, string file)
        {
            lock (pendingFavicons) { if (!pendingFavicons.Add(url)) return; }
            lock (webStatus) webStatus[url] = "Downloading…";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string status = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120 Safari/537.36";
                        wc.Headers[HttpRequestHeader.Accept] = "image/avif,image/webp,image/png,image/*,*/*;q=0.8";
                        var data = wc.DownloadData(url);
                        // a web page instead of a picture (some "image addresses" are really pages)
                        string head = Encoding.ASCII.GetString(data, 0, Math.Min(64, data.Length)).TrimStart().ToLowerInvariant();
                        if (head.StartsWith("<!doctype") || head.StartsWith("<html") || head.StartsWith("<?xml") && head.Contains("svg") || head.StartsWith("<svg"))
                            status = head.Contains("svg") ? "That's an SVG drawing; please use a PNG, JPG, GIF, BMP, ICO or WebP picture." : "That address is a web page, not a picture. Right-click the picture itself > Copy image address.";
                        else File.WriteAllBytes(file, data);
                    }
                }
                catch (Exception ex) { status = "Couldn't download it: " + ex.Message; }
                lock (webStatus) webStatus[url] = status ?? "Loaded";
                if (status != null) lock (pendingFavicons) pendingFavicons.Remove(url);   // let it try again later
                ui?.Post(__ => { ClearIcons(); IconsChanged?.Invoke(); }, null);
            });
        }

        // A short note about a custom icon (shown under the Icon box in settings), or null when all is well
        public static string IconStatus(string iconPath)
        {
            string p = Expand(iconPath);
            if (p.Length == 0) return null;
            bool web = p.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
            bool data = p.StartsWith("data:", StringComparison.OrdinalIgnoreCase);
            if (web || data)
            {
                string local = WebImagePath(p);
                lock (webStatus) if (webStatus.TryGetValue(p, out var st) && st != "Loaded") return st;
                if (!File.Exists(local)) { if (data) SaveDataUri(p, local); else FetchWebImage(p, local); return data ? null : "Downloading…"; }
                return FromIconFile(p, 16) != null ? "✓ Picture loaded" : "Windows can't open this picture. Try a PNG, JPG, GIF, BMP, ICO or WebP.";
            }
            if (!File.Exists(p) && !p.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)) return "Can't find that file.";
            return FromIconFile(p, 16) != null ? null : "Windows can't open this picture.";
        }

        static string FaviconPath(string host) => Path.Combine(Config.Dir, "Cache", "icons", host + ".png");

        // Downloads a site's icon once and keeps it next to the app.
        static void FetchFavicon(string host)
        {
            lock (pendingFavicons) { if (!pendingFavicons.Add(host)) return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    string file = FaviconPath(host);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    using (var wc = new WebClient())
                    {
                        wc.Headers[HttpRequestHeader.UserAgent] = "Mozilla/5.0 CL-Taskbar";
                        Bitmap got = null;
                        // The site's own icon first; DuckDuckGo's icon service as a fallback
                        foreach (var url in new[] { "https://" + host + "/favicon.ico", "https://icons.duckduckgo.com/ip3/" + host + ".ico" })
                        {
                            try
                            {
                                var data = wc.DownloadData(url);
                                using (var ms = new MemoryStream(data))
                                {
                                    try { using (var ico = new Icon(ms, 64, 64)) got = ico.ToBitmap(); }
                                    catch { ms.Position = 0; using (var img = Image.FromStream(ms)) got = new Bitmap(img); }
                                }
                                if (got != null && got.Width >= 8) break;
                            }
                            catch { got = null; }
                        }
                        if (got == null) return;
                        using (got) got.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    ui?.Post(__ =>
                    {
                        foreach (var k in iconCache.Keys.Where(k => k.StartsWith("web|")).ToList()) { iconCache[k].Dispose(); iconCache.Remove(k); }
                        IconsChanged?.Invoke();
                    }, null);
                }
                catch { }
            });
        }

        // ================= launching programs =================

        public static void Run(LaunchItem it, IWin32Window owner = null)
        {
            if (it == null || it.Type == "separator" || it.Type == "folder") return;
            string target = Expand(it.Target), args = Expand(it.Arguments);
            try
            {
                var psi = new ProcessStartInfo { UseShellExecute = true };
                string ext = Path.GetExtension(target).ToLowerInvariant();
                if (it.RunAsAdmin && ext == ".msc") { psi.FileName = "mmc.exe"; psi.Arguments = Quote(Resolve(target)) + " " + args; }
                else if (it.RunAsAdmin && ext == ".cpl") { psi.FileName = "control.exe"; psi.Arguments = Quote(Resolve(target)) + " " + args; }
                else { psi.FileName = target; psi.Arguments = args; }
                if (it.RunAsAdmin) psi.Verb = "runas";
                string dir = Expand(it.StartIn);
                if (dir.Length > 0 && Directory.Exists(dir)) psi.WorkingDirectory = dir;
                string name = string.IsNullOrEmpty(it.Name) ? target : it.Name;
                // Start it on a separate thread so the taskbar never waits for the app to launch
                var th = new Thread(() =>
                {
                    try { Process.Start(psi); }
                    catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { } // cancelled UAC prompt
                    catch (Exception ex)
                    {
                        ui?.Post(__ => MessageBox.Show("Couldn't open \"" + name + "\".\n\n" + ex.Message, "CL-Taskbar", MessageBoxButtons.OK, MessageBoxIcon.Warning), null);
                    }
                }) { IsBackground = true };
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, "Couldn't open \"" + (string.IsNullOrEmpty(it.Name) ? target : it.Name) + "\".\n\n" + ex.Message,
                    "CL-Taskbar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        static string Quote(string s) => s.Contains(" ") ? "\"" + s + "\"" : s;

        // ================= browsers =================

        public class BrowserInfo
        {
            public string Exe;       // full path
            public string Kind;      // chromium | firefox | other
            public string Name;
        }

        public static readonly string[][] BrowserChoices =
        {
            new[] { "default", "Default browser" },
            new[] { "chrome", "Google Chrome" },
            new[] { "edge", "Microsoft Edge" },
            new[] { "firefox", "Firefox" },
            new[] { "brave", "Brave" },
            new[] { "opera", "Opera" },
            new[] { "vivaldi", "Vivaldi" },
            new[] { "custom", "Other (choose the .exe)" },
        };

        public static readonly string[][] OpenModes =
        {
            new[] { "tabHere", "New tab on this desktop (else new window)" },
            new[] { "tabHereElseSwitch", "New tab here, else go to a desktop with one" },
            new[] { "tabRecent", "New tab in the window I used last" },
            new[] { "newWindow", "Always a new window" },
        };

        public static BrowserInfo ResolveBrowser(BrowserOptions o)
        {
            o = o ?? new BrowserOptions();
            string exe = null;
            switch (o.Browser)
            {
                case "chrome": exe = AppPath("chrome.exe", @"Google\Chrome\Application\chrome.exe"); break;
                case "edge": exe = AppPath("msedge.exe", @"Microsoft\Edge\Application\msedge.exe"); break;
                case "firefox": exe = AppPath("firefox.exe", @"Mozilla Firefox\firefox.exe"); break;
                case "brave": exe = AppPath("brave.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe"); break;
                case "opera": exe = AppPath("opera.exe", null) ?? AppPath("launcher.exe", null); break;
                case "vivaldi": exe = AppPath("vivaldi.exe", @"Vivaldi\Application\vivaldi.exe"); break;
                case "custom": exe = Expand(o.CustomPath); break;
                default: exe = DefaultBrowserExe(); break;
            }
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) exe = o.Browser == "default" ? null : DefaultBrowserExe();
            string n = exe == null ? "" : Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
            string kind = n == "firefox" || n == "librewolf" || n == "waterfox" || n == "floorp" ? "firefox"
                        : n == "chrome" || n == "msedge" || n == "brave" || n == "vivaldi" || n == "opera" || n == "chromium" || n == "launcher" ? "chromium"
                        : "other";
            return new BrowserInfo { Exe = exe, Kind = kind, Name = n };
        }

        static string AppPath(string exe, string programFilesRel)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                try
                {
                    using (var k = hive.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                        if (k?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { }
            }
            if (programFilesRel != null)
            {
                foreach (var root in new[] {
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData) })
                {
                    string p = Path.Combine(root, programFilesRel);
                    if (File.Exists(p)) return p;
                }
            }
            return null;
        }

        static string DefaultBrowserExe()
        {
            try
            {
                string progId = null;
                foreach (var proto in new[] { "https", "http" })
                {
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + proto + @"\UserChoice"))
                        progId = k?.GetValue("ProgId") as string;
                    if (progId != null) break;
                }
                if (progId == null) return null;
                using (var k = Registry.ClassesRoot.OpenSubKey(progId + @"\shell\open\command"))
                {
                    string cmd = k?.GetValue(null) as string;
                    if (string.IsNullOrEmpty(cmd)) return null;
                    cmd = cmd.Trim();
                    string exe = cmd.StartsWith("\"") ? cmd.Substring(1, cmd.IndexOf('"', 1) - 1) : cmd.Split(' ')[0];
                    return File.Exists(exe) ? exe : null;
                }
            }
            catch { return null; }
        }

        // ================= web links =================

        public interface IWindowHost
        {
            List<AppWindow> AllWindows();
            Guid CurrentDesktop { get; }
            long LastActivated(IntPtr hwnd);
            void ActivateWindow(AppWindow w);
        }

        // A link's own choices, on top of its menu's (or group's) where it follows them
        public static BrowserOptions EffectiveBrowser(LaunchItem it, BrowserOptions menuDefaults)
        {
            var o = ((it.UseMenuBrowser && menuDefaults != null) ? menuDefaults : it.Browser ?? new BrowserOptions()).Clone();
            if (it.UseMenuBrowser)
            {
                if (!string.IsNullOrEmpty(it.OpenModeOverride)) o.OpenMode = it.OpenModeOverride;
                if (it.PrivateOverride == "private") o.Private = true;
                else if (it.PrivateOverride == "regular") o.Private = false;
            }
            return o;
        }

        public static void OpenWeb(LaunchItem it, BrowserOptions menuDefaults, IWindowHost host)
        {
            var opts = EffectiveBrowser(it, menuDefaults);
            string url = it.Target.Trim();
            if (url.Length == 0) return;
            if (!url.Contains("://") && !url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;

            var b = ResolveBrowser(opts);
            if (b.Exe == null) { Start(url, null); return; }

            if (opts.Private) { Start(b.Exe, PrivateArgs(b) + " " + Q(url)); return; }
            if (opts.OpenMode == "newWindow") { Start(b.Exe, NewWindowArgs(b) + Q(url)); return; }

            var wins = BrowserWindows(b, host);
            Guid cur = host.CurrentDesktop;
            Func<AppWindow, bool> here = w => w.DesktopId == Guid.Empty || w.DesktopId == cur;
            IEnumerable<AppWindow> Order(IEnumerable<AppWindow> src) =>
                src.OrderBy(w => opts.PreferVisible && w.Minimized ? 1 : 0).ThenByDescending(w => host.LastActivated(w.Hwnd));

            AppWindow pick = null;
            if (opts.OpenMode == "tabRecent") pick = Order(wins).FirstOrDefault();
            else
            {
                pick = Order(wins.Where(here)).FirstOrDefault();
                if (pick == null && opts.OpenMode == "tabHereElseSwitch") pick = Order(wins).FirstOrDefault();
            }

            if (pick == null) { Start(b.Exe, NewWindowArgs(b) + Q(url)); return; }

            // Bring the chosen window forward first; browsers open a new tab in their most recently active window.
            bool otherDesktop = !here(pick);
            host.ActivateWindow(pick);
            var t = new System.Windows.Forms.Timer { Interval = otherDesktop ? 650 : 180 };
            t.Tick += (s, e) =>
            {
                t.Dispose();
                Start(b.Exe, (b.Kind == "firefox" ? "-new-tab " : "") + Q(url));
            };
            t.Start();
        }

        static List<AppWindow> BrowserWindows(BrowserInfo b, IWindowHost host)
        {
            string exeName = Path.GetFileName(b.Exe);
            return host.AllWindows().Where(w =>
                w.ExePath != null && Path.GetFileName(w.ExePath).Equals(exeName, StringComparison.OrdinalIgnoreCase)
                && (w.Aumid == null || w.Aumid.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) < 0)   // not an installed web app
                && !(w.Title ?? "").StartsWith("DevTools", StringComparison.OrdinalIgnoreCase)
                && IsMainBrowserClass(b.Kind, Native.GetClass(w.Hwnd))).ToList();
        }

        static bool IsMainBrowserClass(string kind, string cls) =>
            kind == "chromium" ? cls == "Chrome_WidgetWin_1" : kind == "firefox" ? cls == "MozillaWindowClass" : true;

        static string NewWindowArgs(BrowserInfo b) => b.Kind == "chromium" ? "--new-window " : b.Kind == "firefox" ? "-new-window " : "";

        static string PrivateArgs(BrowserInfo b)
        {
            if (b.Kind == "firefox") return "-private-window";
            if (b.Name == "msedge") return "--inprivate";
            if (b.Name == "opera" || b.Name == "launcher") return "--private";
            return b.Kind == "chromium" ? "--incognito" : "";
        }

        static string Q(string url) => "\"" + url.Replace("\"", "%22") + "\"";

        static void Start(string file, string args)
        {
            var th = new Thread(() =>
            {
                try { Process.Start(new ProcessStartInfo(file) { Arguments = args ?? "", UseShellExecute = true }); }
                catch (Exception ex) { ui?.Post(__ => MessageBox.Show("Couldn't open the link.\n\n" + ex.Message, "CL-Taskbar", MessageBoxButtons.OK, MessageBoxIcon.Warning), null); }
            }) { IsBackground = true };
            th.SetApartmentState(ApartmentState.STA);
            th.Start();
        }
    }
}
