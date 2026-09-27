using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace CLTaskbar
{
    // Tells us the moment Windows switches virtual desktops, by waiting for Windows to update its
    // "current desktop" value in the registry (read-only: we only ask to be notified, never write).
    // Without this the bar only notices on its next check, which shows as a short delay.
    internal static class DesktopWatcher
    {
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegOpenKeyEx(UIntPtr hKey, string subKey, int options, int samDesired, out IntPtr result);
        [DllImport("advapi32.dll")]
        static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int filter, IntPtr hEvent, bool asynchronous);
        [DllImport("advapi32.dll")]
        static extern int RegCloseKey(IntPtr hKey);

        static readonly UIntPtr HKCU = new UIntPtr(0x80000001u);
        const int KEY_NOTIFY = 0x0010, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
        static Thread thread;

        public static void Start(Action changed)
        {
            if (thread != null) return;
            thread = new Thread(() => Run(changed)) { IsBackground = true, Name = "Desktop watcher" };
            thread.Start();
        }

        static void Run(Action changed)
        {
            int sid = 0;
            try { sid = Process.GetCurrentProcess().SessionId; } catch { }
            // Windows 11 keeps it here; Windows 10 under SessionInfo\<session>
            string[] paths =
            {
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops",
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\" + sid + @"\VirtualDesktops",
            };
            var keys = new IntPtr[paths.Length];
            var events = new AutoResetEvent[paths.Length];
            bool Arm(int i)
            {
                if (keys[i] == IntPtr.Zero && RegOpenKeyEx(HKCU, paths[i], 0, KEY_NOTIFY, out keys[i]) != 0) keys[i] = IntPtr.Zero;
                if (keys[i] == IntPtr.Zero) return false;
                if (events[i] == null) events[i] = new AutoResetEvent(false);
                return RegNotifyChangeKeyValue(keys[i], false, REG_NOTIFY_CHANGE_LAST_SET, events[i].SafeWaitHandle.DangerousGetHandle(), true) == 0;
            }
            void CloseAll()
            {
                for (int i = 0; i < keys.Length; i++) if (keys[i] != IntPtr.Zero) { RegCloseKey(keys[i]); keys[i] = IntPtr.Zero; }
            }
            try
            {
                while (true)
                {
                    // (re)open and arm every key; closing a key cancels its pending notification
                    CloseAll();
                    var armed = new System.Collections.Generic.List<int>();
                    for (int i = 0; i < paths.Length; i++) if (Arm(i)) armed.Add(i);
                    if (armed.Count == 0) { Thread.Sleep(5000); continue; }   // keys not there (yet): try again later
                    var waits = armed.ConvertAll(i => (WaitHandle)events[i]).ToArray();
                    // Wait for changes; every 30 s start over in case Explorer restarted and recreated the keys
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (DateTime.UtcNow < until)
                    {
                        int hit = WaitHandle.WaitAny(waits, 30000);
                        if (hit == WaitHandle.WaitTimeout) break;
                        Arm(armed[hit]);   // a notification fires once: ask again for the next one
                        changed();
                    }
                }
            }
            catch { }
        }
    }
}
