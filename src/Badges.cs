using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace CLTaskbar
{
    // Notification badges (the unread count Teams, Outlook, Mail etc. put on their taskbar icon).
    // Windows keeps every app's badge and its notifications in a small database in your profile. We never
    // touch that file: we copy it into CL-Taskbar's own folder, read the copy with Windows' built-in SQLite
    // (winsqlite3.dll), and delete the copy. Read-only, nothing sent anywhere.
    internal static class Badges
    {
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        static extern int sqlite3_open16(string filename, out IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_close(IntPtr db);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int nBytes, out IntPtr stmt, IntPtr tail);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_step(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_finalize(IntPtr stmt);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr sqlite3_column_text16(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern IntPtr sqlite3_column_blob(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int sqlite3_column_bytes(IntPtr stmt, int col);
        [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.StdCall)]
        static extern long sqlite3_column_int64(IntPtr stmt, int col);

        // app ID (AppUserModelID) -> what to show: "3", "99+", or "•" for a dot
        static volatile Dictionary<string, string> current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // app ID -> when each of its notifications arrived (for "count notifications")
        static volatile Dictionary<string, long[]> toastTimes = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        // app ID -> notifications up to this time count as seen (you switched to the app)
        static readonly Dictionary<string, long> seen = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        public static string For(string aumid)
        {
            if (string.IsNullOrEmpty(aumid)) return null;
            if (current.TryGetValue(aumid, out var s)) return s;
            if (CountNotifications && toastTimes.TryGetValue(aumid, out var times))
            {
                long after;
                lock (seen) after = seen.TryGetValue(aumid, out long t) ? t : 0;
                int n = 0;
                foreach (var x in times) if (x > after) n++;
                if (n > 0) return n > 99 ? "99+" : n.ToString();
            }
            return null;
        }

        // You switched to the app: its waiting notifications count as seen (until a new one arrives)
        public static void MarkSeen(string aumid)
        {
            if (string.IsNullOrEmpty(aumid) || !toastTimes.TryGetValue(aumid, out var times) || times.Length == 0) return;
            long newest = 0;
            foreach (var x in times) if (x > newest) newest = x;
            lock (seen) seen[aumid] = newest;
        }

        // ---- classic Outlook: its real unread count, asked from Outlook itself ----
        public static bool OutlookEnabled = true;
        static volatile int outlookUnread = -1;       // -1 = unknown / Outlook not running
        public static string Outlook() => outlookUnread > 0 ? (outlookUnread > 99 ? "99+" : outlookUnread.ToString()) : null;

        public static bool IsOutlook(string exeOrAumid) =>
            !string.IsNullOrEmpty(exeOrAumid) && (exeOrAumid.EndsWith("\\OUTLOOK.EXE", StringComparison.OrdinalIgnoreCase)
                || exeOrAumid.Equals("OUTLOOK.EXE", StringComparison.OrdinalIgnoreCase)
                || exeOrAumid.StartsWith("Microsoft.Office.OUTLOOK", StringComparison.OrdinalIgnoreCase));

        static void OutlookLoop(Action changed)
        {
            while (true)
            {
                Thread.Sleep(3000);
                int n = -1;
                try
                {
                    if (enabled && OutlookEnabled)
                    {
                        var ps = System.Diagnostics.Process.GetProcessesByName("OUTLOOK");
                        bool running = ps.Length > 0;
                        foreach (var p in ps) p.Dispose();
                        if (running) n = OutlookCount();
                    }
                }
                catch { n = -1; }
                if (n != outlookUnread) { outlookUnread = n; changed(); }
            }
        }

        // Unread email in the Inbox of each account (read-only questions to Outlook's own automation interface)
        static int OutlookCount()
        {
            object app = null;
            var held = new List<object>();
            try
            {
                try { app = Marshal.GetActiveObject("Outlook.Application"); } catch { return -1; }   // not ready yet, or running as administrator
                object Get(object o, string name) { var v = o.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, o, null); if (v != null && Marshal.IsComObject(v)) held.Add(v); return v; }
                object Call(object o, string name, params object[] args) { var v = o.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, o, args); if (v != null && Marshal.IsComObject(v)) held.Add(v); return v; }
                var ns = Get(app, "Session");
                int total = 0; bool any = false;
                try
                {
                    var stores = Get(ns, "Stores");
                    int count = Convert.ToInt32(Get(stores, "Count"));
                    for (int i = 1; i <= count; i++)
                    {
                        try
                        {
                            var store = Call(stores, "Item", i);
                            var inbox = Call(store, "GetDefaultFolder", 6);        // olFolderInbox
                            total += Convert.ToInt32(Get(inbox, "UnReadItemCount"));
                            any = true;
                        }
                        catch { }   // stores without an inbox (shared calendars, archives...)
                    }
                }
                catch { }
                if (!any)
                {
                    var inbox = Call(ns, "GetDefaultFolder", 6);
                    total = Convert.ToInt32(Get(inbox, "UnReadItemCount"));
                }
                return total;
            }
            finally
            {
                for (int i = held.Count - 1; i >= 0; i--) try { Marshal.ReleaseComObject(held[i]); } catch { }
                if (app != null) try { Marshal.ReleaseComObject(app); } catch { }
            }
        }

        public static bool CountNotifications;   // also count notifications waiting in the notification center
        static Thread thread;
        static volatile bool enabled;
        public static string LastError;

        public static void Start(Action changed)
        {
            enabled = true;
            if (thread != null) return;
            thread = new Thread(() => Run(changed)) { IsBackground = true, Name = "Badges", Priority = ThreadPriority.BelowNormal };
            thread.Start();
            var ol = new Thread(() => OutlookLoop(changed)) { IsBackground = true, Name = "Outlook unread", Priority = ThreadPriority.BelowNormal };
            ol.SetApartmentState(ApartmentState.STA);
            ol.Start();
        }

        public static void Stop(Action changed)
        {
            enabled = false;
            if (current.Count > 0 || toastTimes.Count > 0 || outlookUnread > 0)
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                toastTimes = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
                outlookUnread = -1;
                changed();
            }
        }

        static string DbPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications\wpndatabase.db");

        static void Run(Action changed)
        {
            DateTime lastDb = DateTime.MinValue, lastWal = DateTime.MinValue;
            bool lastCount = CountNotifications;
            while (true)
            {
                Thread.Sleep(2000);
                if (!enabled) { lastDb = DateTime.MinValue; continue; }
                try
                {
                    string db = DbPath, wal = db + "-wal";
                    if (!File.Exists(db)) continue;
                    var tDb = File.GetLastWriteTimeUtc(db);
                    var tWal = File.Exists(wal) ? File.GetLastWriteTimeUtc(wal) : DateTime.MinValue;
                    if (tDb == lastDb && tWal == lastWal && lastCount == CountNotifications) continue;   // nothing new
                    lastDb = tDb; lastWal = tWal; lastCount = CountNotifications;
                    var fresh = Read(db, wal);
                    if (fresh == null) continue;
                    current = fresh;
                    changed();
                }
                catch (Exception ex) { LastError = ex.Message; }
            }
        }

        static bool Same(Dictionary<string, string> a, Dictionary<string, string> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a) if (!b.TryGetValue(kv.Key, out var v) || v != kv.Value) return false;
            return true;
        }

        static readonly Regex BadgeValue = new Regex("<badge[^>]*\\bvalue\\s*=\\s*[\"']([^\"']*)[\"']", RegexOptions.IgnoreCase);

        static Dictionary<string, string> Read(string db, string wal)
        {
            // a private copy in our own folder (the live file belongs to Windows)
            string dir = Path.Combine(Config.Dir, "Cache");
            Directory.CreateDirectory(dir);
            string copy = Path.Combine(dir, "notifications-copy.db");
            foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" }) try { if (File.Exists(f)) File.Delete(f); } catch { }
            try
            {
                CopyShared(db, copy);
                if (File.Exists(wal)) CopyShared(wal, copy + "-wal");
                return Query(copy);
            }
            finally
            {
                foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" }) try { if (File.Exists(f)) File.Delete(f); } catch { }
            }
        }

        static void CopyShared(string from, string to)
        {
            using (var src = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var dst = new FileStream(to, FileMode.Create, FileAccess.Write, FileShare.None))
                src.CopyTo(dst);
        }

        static Dictionary<string, string> Query(string file)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var badgeTime = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var toasts = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            if (sqlite3_open16(file, out IntPtr h) != 0) { if (h != IntPtr.Zero) sqlite3_close(h); return null; }
            try
            {
                const string sql = "SELECT h.PrimaryId, n.Type, n.Payload, n.ArrivalTime, n.ExpiryTime FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId WHERE n.Type IN ('badge','toast')";
                if (sqlite3_prepare16_v2(h, sql, -1, out IntPtr st, IntPtr.Zero) != 0) return null;
                try
                {
                    long now = DateTime.UtcNow.ToFileTimeUtc();
                    while (sqlite3_step(st) == 100)   // SQLITE_ROW
                    {
                        string app = Marshal.PtrToStringUni(sqlite3_column_text16(st, 0)) ?? "";
                        string type = Marshal.PtrToStringUni(sqlite3_column_text16(st, 1)) ?? "";
                        long arrived = sqlite3_column_int64(st, 3), expires = sqlite3_column_int64(st, 4);
                        if (app.Length == 0 || (expires > 0 && expires < now)) continue;
                        if (type == "toast") { if (!toasts.TryGetValue(app, out var l)) toasts[app] = l = new List<long>(); l.Add(arrived); continue; }
                        // badge: the newest one for the app wins
                        if (badgeTime.TryGetValue(app, out long t) && t > arrived) continue;
                        badgeTime[app] = arrived;
                        int n = sqlite3_column_bytes(st, 2);
                        IntPtr p = sqlite3_column_blob(st, 2);
                        string xml = "";
                        if (n > 0 && p != IntPtr.Zero) { var bytes = new byte[n]; Marshal.Copy(p, bytes, 0, n); xml = System.Text.Encoding.UTF8.GetString(bytes); }
                        var m = BadgeValue.Match(xml);
                        string v = m.Success ? m.Groups[1].Value.Trim() : "";
                        if (int.TryParse(v, out int num)) { if (num > 0) result[app] = num > 99 ? "99+" : num.ToString(); else result.Remove(app); }
                        else if (v.Length == 0 || v == "none" || v == "available") result.Remove(app);
                        else result[app] = "•";   // a glyph like "newMessage" or "attention": show a dot
                    }
                }
                finally { sqlite3_finalize(st); }
            }
            finally { sqlite3_close(h); }
            var times = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in toasts) times[kv.Key] = kv.Value.ToArray();
            toastTimes = times;
            return result;
        }
    }
}
