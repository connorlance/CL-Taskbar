using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CLTaskbar
{
    // The modern "Select folder" window (the same one File Explorer's open/save windows use),
    // instead of the old tree-only folder browser. Falls back to the old one if it isn't available.
    internal static class FolderPicker
    {
        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint count, IntPtr specs);
            void SetFileTypeIndex(uint index);
            void GetFileTypeIndex(out uint index);
            void Advise(IntPtr sink, out uint cookie);
            void Unadvise(uint cookie);
            void SetOptions(uint fos);
            void GetOptions(out uint fos);
            void SetDefaultFolder(AppResolve.IShellItem item);
            void SetFolder(AppResolve.IShellItem item);
            void GetFolder(out AppResolve.IShellItem item);
            void GetCurrentSelection(out AppResolve.IShellItem item);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string name);
            void GetFileName(out IntPtr name);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string text);
            void GetResult(out AppResolve.IShellItem item);
        }

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")] class FileOpenDialog { }

        const uint FOS_PICKFOLDERS = 0x20, FOS_FORCEFILESYSTEM = 0x40, FOS_PATHMUSTEXIST = 0x800;
        const int SIGDN_FILESYSPATH = unchecked((int)0x80058000);

        // Returns the chosen folder, or null if cancelled
        public static string Pick(IWin32Window owner, string title, string start = null)
        {
            IFileDialog dlg = null;
            try
            {
                dlg = (IFileDialog)new FileOpenDialog();
                dlg.GetOptions(out uint fos);
                dlg.SetOptions(fos | FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST);
                dlg.SetTitle(title);
                if (!string.IsNullOrEmpty(start))
                {
                    var item = AppResolve.Item(Launcher.Expand(start));
                    if (item != null) dlg.SetFolder(item);
                }
                int hr = dlg.Show(owner?.Handle ?? IntPtr.Zero);
                if (hr != 0) return null;   // cancelled
                dlg.GetResult(out var result);
                if (result == null || result.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr p) != 0 || p == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(p); }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            catch
            {
                // older or unusual systems: the classic folder browser
                using (var fb = new FolderBrowserDialog { Description = title, ShowNewFolderButton = true })
                {
                    if (!string.IsNullOrEmpty(start)) fb.SelectedPath = Launcher.Expand(start);
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
                }
            }
            finally
            {
                if (dlg != null) Marshal.ReleaseComObject(dlg);
            }
        }
    }
}
