using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CLTaskbar
{
    // All Win32 calls used by the app. Everything here only READS system state
    // or manipulates windows that belong to this app (plus normal "activate / minimize /
    // close" requests on other windows, exactly like the real taskbar sends).
    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public int Width => Right - Left;
            public int Height => Bottom - Top;
            public System.Drawing.Rectangle ToRectangle() => System.Drawing.Rectangle.FromLTRB(Left, Top, Right, Bottom);
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx, cy; public SIZE(int x, int y) { cx = x; cy = y; } }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination;
            public RECT rcSource;
            public byte opacity;
            [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
            [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
        }

        public const int DWM_TNP_RECTDESTINATION = 0x1;
        public const int DWM_TNP_OPACITY = 0x4;
        public const int DWM_TNP_VISIBLE = 0x8;
        public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x10;

        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const int GW_OWNER = 4;

        public const int WS_POPUP = unchecked((int)0x80000000);
        public const int WS_EX_TOOLWINDOW = 0x80;
        public const int WS_EX_APPWINDOW = 0x40000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const int WS_EX_TOPMOST = 0x8;

        public const int SW_RESTORE = 9;
        public const int SW_MINIMIZE = 6;

        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public const int WM_MOUSEACTIVATE = 0x21;
        public const int MA_NOACTIVATE = 3;
        public const int WM_GETICON = 0x7F;
        public const int WM_CLOSE = 0x10;
        public const int WM_SYSCOMMAND = 0x112;
        public const int SC_CLOSE = 0xF060;

        public const int DWMWA_CLOAKED = 14;
        public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        public const int DWM_CLOAKED_SHELL = 0x2;

        public const uint EVENT_SYSTEM_FOREGROUND = 3;
        public const uint WINEVENT_OUTOFCONTEXT = 0;

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
        public delegate void WinEventDelegate(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int idx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
        [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, int msg, IntPtr w, IntPtr l, int flags, int timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr hwnd, int idx);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(IntPtr hwnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string s);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventDelegate cb, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);

        [DllImport("gdi32.dll")] public static extern int GetObject(IntPtr h, int size, out BITMAP bm);
        [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr bmp, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFOHEADER bih, uint usage);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);

        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
        public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        public static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("shell32.dll")]
        public static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, [In] ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PROPVARIANT pv);

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPERTYKEY { public Guid fmtid; public int pid; }

        [StructLayout(LayoutKind.Sequential)]
        public struct PROPVARIANT
        {
            public ushort vt;
            public ushort r1, r2, r3;
            public IntPtr p;
            public IntPtr p2;
        }

        public static string GetText(IntPtr hwnd)
        {
            int len = GetWindowTextLength(hwnd);
            if (len <= 0) return "";
            var sb = new StringBuilder(len + 2);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetClass(IntPtr hwnd)
        {
            var sb = new StringBuilder(256);
            GetClassName(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string GetProcessPath(uint pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
            }
            finally { CloseHandle(h); }
        }

        public static RECT GetMonitorRect(IntPtr hwnd, bool work)
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref mi);
            return work ? mi.rcWork : mi.rcMonitor;
        }

        public static void RoundCorners(IntPtr hwnd)
        {
            try { int pref = 2; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4); } catch { }
        }
    }

    // ---- COM interfaces ----

    // Documented Windows 10/11 API for asking which virtual desktop a window lives on.
    [ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IVirtualDesktopManager
    {
        [PreserveSig] int IsWindowOnCurrentVirtualDesktop(IntPtr hwnd, out int onCurrent);
        [PreserveSig] int GetWindowDesktopId(IntPtr hwnd, out Guid desktopId);
        [PreserveSig] int MoveWindowToDesktop(IntPtr hwnd, [In] ref Guid desktopId);
    }

    [ComImport, Guid("aa509086-5ca9-4c25-8f95-589d3c07b48a")]
    internal class VirtualDesktopManagerClass { }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(Native.SIZE size, int flags, out IntPtr hbmp);
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint i, out Native.PROPERTYKEY key);
        [PreserveSig] int GetValue([In] ref Native.PROPERTYKEY key, out Native.PROPVARIANT pv);
        [PreserveSig] int SetValue([In] ref Native.PROPERTYKEY key, [In] ref Native.PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    // UI Automation (read-only) — used to find where Start / Task View / tray sit on the real taskbar.
    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomation
    {
        void _CompareElements(); void _CompareRuntimeIds(); void _GetRootElement();
        [PreserveSig] int ElementFromHandle(IntPtr hwnd, out IUIAutomationElement element);
        void _ElementFromPoint(); void _GetFocusedElement(); void _GetRootElementBuildCache();
        void _ElementFromHandleBuildCache(); void _ElementFromPointBuildCache(); void _GetFocusedElementBuildCache();
        void _CreateTreeWalker(); void _ControlViewWalker(); void _ContentViewWalker(); void _RawViewWalker();
        void _RawViewCondition(); void _ControlViewCondition(); void _ContentViewCondition(); void _CreateCacheRequest();
        [PreserveSig] int CreateTrueCondition(out IUIAutomationCondition cond);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationCondition { }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElementArray
    {
        [PreserveSig] int get_Length(out int len);
        [PreserveSig] int GetElement(int index, out IUIAutomationElement element);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IUIAutomationElement
    {
        void _SetFocus(); void _GetRuntimeId(); void _FindFirst();
        [PreserveSig] int FindAll(int scope, IUIAutomationCondition cond, out IUIAutomationElementArray found);
        void _FindFirstBuildCache(); void _FindAllBuildCache(); void _BuildUpdatedCache();
        void _GetCurrentPropertyValue(); void _GetCurrentPropertyValueEx(); void _GetCachedPropertyValue(); void _GetCachedPropertyValueEx();
        void _GetCurrentPatternAs(); void _GetCachedPatternAs(); void _GetCurrentPattern(); void _GetCachedPattern();
        void _GetCachedParent(); void _GetCachedChildren();
        void _CurrentProcessId(); void _CurrentControlType(); void _CurrentLocalizedControlType();
        [PreserveSig] int get_CurrentName([MarshalAs(UnmanagedType.BStr)] out string name);
        void _CurrentAcceleratorKey(); void _CurrentAccessKey(); void _CurrentHasKeyboardFocus(); void _CurrentIsKeyboardFocusable(); void _CurrentIsEnabled();
        [PreserveSig] int get_CurrentAutomationId([MarshalAs(UnmanagedType.BStr)] out string id);
        [PreserveSig] int get_CurrentClassName([MarshalAs(UnmanagedType.BStr)] out string cls);
        void _CurrentHelpText(); void _CurrentCulture(); void _CurrentIsControlElement(); void _CurrentIsContentElement(); void _CurrentIsPassword();
        void _CurrentNativeWindowHandle(); void _CurrentItemType(); void _CurrentIsOffscreen(); void _CurrentOrientation(); void _CurrentFrameworkId();
        void _CurrentIsRequiredForForm(); void _CurrentItemStatus();
        [PreserveSig] int get_CurrentBoundingRectangle(out Native.RECT rect);
    }

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")]
    internal class CUIAutomation { }
}
