using System;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    // Moves ANOTHER app's window to a different virtual desktop.
    // Windows has no public API for this, so this uses the shell's internal interface (the same one
    // tools like PowerToys and VirtualDesktopAccessor use). Its identity changes between Windows versions;
    // we only ever ask for versions we know, and every call is checked. If Windows changes it, the move
    // simply fails with a message — nothing else happens.
    internal static class DesktopMover
    {
        [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IServiceProvider10
        {
            [PreserveSig] int QueryService([In] ref Guid service, [In] ref Guid riid, out IntPtr ppv);
        }

        static readonly Guid CLSID_ImmersiveShell = new Guid("C2F03A33-21F5-47FA-B4BB-156362A2F239");
        static readonly Guid SID_VDMInternal = new Guid("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
        static readonly Guid IID_ViewCollection = new Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
        static readonly Guid IID_IUnknown = new Guid("00000000-0000-0000-C000-000000000046");

        // Known versions of the internal desktop manager. For all of them, slot 4 is MoveViewToDesktop
        // and slot 7 is GetDesktops (Windows 11 21H2 adds a monitor parameter to GetDesktops).
        static readonly (Guid iid, bool monitorParam)[] Known =
        {
            (new Guid("53F5CA0B-158F-4124-900C-057158060B27"), false), // Windows 11 24H2+
            (new Guid("4970BA3D-FD4E-4647-BEA3-D89076EF4B9C"), false), // Windows 11 22H2/23H2 (later builds)
            (new Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"), false), // Windows 11 22H2 (early builds)
            (new Guid("B2F925B9-5A0F-4D2E-9F4D-2B1507593C10"), true),  // Windows 11 21H2
            (new Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"), false), // Windows 10
        };

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetViewForHwndFn(IntPtr self, IntPtr hwnd, out IntPtr view);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MoveViewFn(IntPtr self, IntPtr view, IntPtr desktop);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesktopsFn(IntPtr self, out IntPtr array);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetDesktopsMonFn(IntPtr self, IntPtr monitor, out IntPtr array);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ArrayCountFn(IntPtr self, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ArrayGetAtFn(IntPtr self, uint index, [In] ref Guid riid, out IntPtr obj);

        static T Slot<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            IntPtr fn = Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(fn, typeof(T));
        }

        static void Release(IntPtr p) { if (p != IntPtr.Zero) Marshal.Release(p); }

        // desktopIndex: position in Task View (0-based). expectedCount: how many desktops we think exist.
        public static bool Move(IntPtr hwnd, int desktopIndex, int expectedCount, out string error)
        {
            error = null;
            IntPtr coll = IntPtr.Zero, view = IntPtr.Zero, mgr = IntPtr.Zero, arr = IntPtr.Zero, desk = IntPtr.Zero;
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_ImmersiveShell));
                var sp = (IServiceProvider10)shell;

                var iidColl = IID_ViewCollection;
                if (sp.QueryService(ref iidColl, ref iidColl, out coll) != 0 || coll == IntPtr.Zero) { error = "Windows didn't allow it."; return false; }
                if (Slot<GetViewForHwndFn>(coll, 6)(coll, hwnd, out view) != 0 || view == IntPtr.Zero) { error = "That window can't be moved."; return false; }

                bool monitorParam = false;
                var sid = SID_VDMInternal;
                foreach (var k in Known)
                {
                    var iid = k.iid;
                    if (sp.QueryService(ref sid, ref iid, out mgr) == 0 && mgr != IntPtr.Zero) { monitorParam = k.monitorParam; break; }
                    mgr = IntPtr.Zero;
                }
                if (mgr == IntPtr.Zero) { error = "This version of Windows isn't supported for moving windows between desktops yet."; return false; }

                int hr = monitorParam ? Slot<GetDesktopsMonFn>(mgr, 7)(mgr, IntPtr.Zero, out arr) : Slot<GetDesktopsFn>(mgr, 7)(mgr, out arr);
                if (hr != 0 || arr == IntPtr.Zero) { error = "Couldn't read the desktop list."; return false; }

                // IObjectArray is a documented interface: GetCount = slot 3, GetAt = slot 4
                if (Slot<ArrayCountFn>(arr, 3)(arr, out uint count) != 0) { error = "Couldn't read the desktop list."; return false; }
                if (count != expectedCount || desktopIndex < 0 || desktopIndex >= count) { error = "The desktop list changed. Try again."; return false; }
                var unk = IID_IUnknown;
                if (Slot<ArrayGetAtFn>(arr, 4)(arr, (uint)desktopIndex, ref unk, out desk) != 0 || desk == IntPtr.Zero) { error = "Couldn't find that desktop."; return false; }

                hr = Slot<MoveViewFn>(mgr, 4)(mgr, view, desk);
                if (hr != 0) { error = "Windows didn't allow moving that window."; return false; }
                return true;
            }
            catch (Exception ex)
            {
                error = "Couldn't move the window (" + ex.Message + ").";
                return false;
            }
            finally
            {
                Release(desk); Release(arr); Release(mgr); Release(view); Release(coll);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }
    }
}
