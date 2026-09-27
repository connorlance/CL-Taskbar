using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace CLTaskbar
{
    // Shows Explorer's own right-click menu for a file or app (what you get with Shift+right-click),
    // and the Properties window.
    internal static class ShellMenu
    {
        [ComImport, Guid("000214e4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr cmd, uint type, IntPtr reserved, IntPtr name, uint cch);
        }

        [ComImport, Guid("000214f4-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu2
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr cmd, uint type, IntPtr reserved, IntPtr name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
        }

        [ComImport, Guid("bcfce0a0-ec17-11d0-8d10-00a0c90f2719"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IContextMenu3
        {
            [PreserveSig] int QueryContextMenu(IntPtr hmenu, uint index, uint idFirst, uint idLast, uint flags);
            [PreserveSig] int InvokeCommand(ref CMINVOKECOMMANDINFOEX info);
            [PreserveSig] int GetCommandString(UIntPtr cmd, uint type, IntPtr reserved, IntPtr name, uint cch);
            [PreserveSig] int HandleMenuMsg(uint msg, IntPtr w, IntPtr l);
            [PreserveSig] int HandleMenuMsg2(uint msg, IntPtr w, IntPtr l, out IntPtr result);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct CMINVOKECOMMANDINFOEX
        {
            public int cbSize;
            public uint fMask;
            public IntPtr hwnd;
            public IntPtr lpVerb;
            [MarshalAs(UnmanagedType.LPStr)] public string lpParameters;
            [MarshalAs(UnmanagedType.LPStr)] public string lpDirectory;
            public int nShow;
            public uint dwHotKey;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.LPStr)] public string lpTitle;
            public IntPtr lpVerbW;
            public string lpParametersW;
            public string lpDirectoryW;
            public string lpTitleW;
            public Native.POINT ptInvoke;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHELLEXECUTEINFO
        {
            public int cbSize; public uint fMask; public IntPtr hwnd;
            public string lpVerb, lpFile, lpParameters, lpDirectory;
            public int nShow; public IntPtr hInstApp, lpIDList; public string lpClass;
            public IntPtr hkeyClass; public uint dwHotKey; public IntPtr hIcon, hProcess;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern bool ShellExecuteEx(ref SHELLEXECUTEINFO info);
        [DllImport("user32.dll")] static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll")] static extern bool DestroyMenu(IntPtr h);
        [DllImport("user32.dll")] static extern int TrackPopupMenuEx(IntPtr h, uint flags, int x, int y, IntPtr hwnd, IntPtr tpm);

        static IContextMenu2 active2;
        static IContextMenu3 active3;

        // The bar forwards menu-drawing messages here while the menu is open (needed for "Send to", "Open with"...)
        public static bool HandleMessage(ref Message m)
        {
            if (active2 == null && active3 == null) return false;
            int msg = m.Msg;
            if (msg != 0x117 && msg != 0x2B && msg != 0x2C && msg != 0x120) return false;   // INITMENUPOPUP, DRAWITEM, MEASUREITEM, MENUCHAR
            try
            {
                if (active3 != null && active3.HandleMenuMsg2((uint)msg, m.WParam, m.LParam, out IntPtr r) == 0) { m.Result = r; return true; }
                if (active2 != null && active2.HandleMenuMsg((uint)msg, m.WParam, m.LParam) == 0) { m.Result = IntPtr.Zero; return true; }
            }
            catch { }
            return false;
        }

        // extended: include the Shift+right-click extras. aboveCentered: centered above x,y (the taskbar) instead of at the mouse
        public static bool Show(string path, IntPtr owner, int x, int y, bool extended = true, bool aboveCentered = true)
        {
            var item = AppResolve.Item(path);
            if (item == null) return false;
            IntPtr pcm = IntPtr.Zero, menu = IntPtr.Zero;
            try
            {
                var bhid = new Guid("3981e225-f559-11d3-8e3a-00c04f6837d5");   // BHID_SFUIObject
                var iid = new Guid("000214e4-0000-0000-c000-000000000046");
                if (item.BindToHandler(IntPtr.Zero, ref bhid, ref iid, out pcm) != 0 || pcm == IntPtr.Zero) return false;
                var cm = (IContextMenu)Marshal.GetObjectForIUnknown(pcm);
                active2 = cm as IContextMenu2;
                active3 = cm as IContextMenu3;
                menu = CreatePopupMenu();
                // CMF_NORMAL | CMF_EXTENDEDVERBS (the Shift+right-click extras)
                if (cm.QueryContextMenu(menu, 0, 1, 0x7FFF, extended ? 0x100u : 0u) < 0) return false;
                Native.SetForegroundWindow(owner);
                // TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_CENTERALIGN (centered above the icon)
                int cmd = TrackPopupMenuEx(menu, 0x100 | 0x2 | (aboveCentered ? 0x20u | 0x4u : 0u), x, y, owner, IntPtr.Zero);
                if (cmd > 0)
                {
                    var info = new CMINVOKECOMMANDINFOEX
                    {
                        cbSize = Marshal.SizeOf(typeof(CMINVOKECOMMANDINFOEX)),
                        fMask = 0x4000 | 0x20000000,          // UNICODE | PTINVOKE
                        hwnd = owner,
                        lpVerb = (IntPtr)(cmd - 1),
                        lpVerbW = (IntPtr)(cmd - 1),
                        nShow = 1,
                        ptInvoke = new Native.POINT { X = x, Y = y }
                    };
                    cm.InvokeCommand(ref info);
                }
                return true;
            }
            catch { return false; }
            finally
            {
                active2 = null; active3 = null;
                if (menu != IntPtr.Zero) DestroyMenu(menu);
                if (pcm != IntPtr.Zero) Marshal.Release(pcm);
                Marshal.ReleaseComObject(item);
            }
        }

        public static void Properties(string path, IntPtr owner)
        {
            var info = new SHELLEXECUTEINFO
            {
                cbSize = Marshal.SizeOf(typeof(SHELLEXECUTEINFO)),
                fMask = 0x0C,               // SEE_MASK_INVOKEIDLIST
                hwnd = owner, lpVerb = "properties", lpFile = path, nShow = 1
            };
            try { ShellExecuteEx(ref info); } catch { }
        }
    }
}
