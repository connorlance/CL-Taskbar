using System;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace CLTaskbar
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); } catch { } // per-monitor v2
            using (var mutex = new Mutex(true, "CL-Taskbar_SingleInstance_7f3c", out bool first))
            {
                if (!first) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => Log(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log(e.ExceptionObject as Exception);
                MigrateStartupShortcut();
                Application.Run(new BarForm());
            }
        }

        // If you had "start when I sign in" on under the old name, point it at CL-Taskbar instead
        static void MigrateStartupShortcut()
        {
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
                string old = Path.Combine(dir, "TaskbarGroups.lnk");
                if (!File.Exists(old)) return;
                // only touch it if it's ours (points into the folder CL-Taskbar runs from)
                string target = AppIdentity.LinkTarget(old);
                if (string.IsNullOrEmpty(target) ||
                    !string.Equals(Path.GetDirectoryName(target)?.TrimEnd('\\'), AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return;
                File.Delete(old);
                SettingsForm.CreateShortcut(Path.Combine(dir, "CL-Taskbar.lnk"), Application.ExecutablePath);
            }
            catch { }
        }

        internal static void Log(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "CL-Taskbar-errors.log"),
                    DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
            }
            catch { }
        }
    }
}
