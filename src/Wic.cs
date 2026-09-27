using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CLTaskbar
{
    // Decodes pictures with Windows Imaging Component, so formats like WebP work too
    // (whatever image formats Windows itself can open).
    internal static class Wic
    {
        static readonly Guid CLSID_Factory = new Guid("cacaf262-9370-4615-a13b-9f5539da4c0a");
        static readonly Guid BGRA = new Guid("6fddc324-4e03-4bfe-b185-3d77768dc90f");

        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        delegate int CreateDecoderFromFilenameFn(IntPtr self, string file, IntPtr vendor, uint access, int options, out IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateFormatConverterFn(IntPtr self, out IntPtr conv);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetFrameFn(IntPtr self, uint index, out IntPtr frame);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int GetSizeFn(IntPtr self, out uint w, out uint h);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CopyPixelsFn(IntPtr self, IntPtr rect, uint stride, uint size, [Out] byte[] buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int ConverterInitFn(IntPtr self, IntPtr source, [In] ref Guid format, int dither, IntPtr palette, double alpha, int paletteType);

        static T Slot<T>(IntPtr obj, int slot) where T : class =>
            (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size), typeof(T));

        public static Bitmap Load(string file)
        {
            object factoryObj = null;
            IntPtr factory = IntPtr.Zero, decoder = IntPtr.Zero, frame = IntPtr.Zero, conv = IntPtr.Zero;
            try
            {
                factoryObj = Activator.CreateInstance(Type.GetTypeFromCLSID(CLSID_Factory));
                IntPtr unk = Marshal.GetIUnknownForObject(factoryObj);
                var iid = new Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70");   // IWICImagingFactory
                int hr = Marshal.QueryInterface(unk, ref iid, out factory);
                Marshal.Release(unk);
                if (hr != 0) return null;
                if (Slot<CreateDecoderFromFilenameFn>(factory, 3)(factory, file, IntPtr.Zero, 0x80000000, 0, out decoder) != 0) return null;
                if (Slot<GetFrameFn>(decoder, 13)(decoder, 0, out frame) != 0) return null;
                if (Slot<CreateFormatConverterFn>(factory, 10)(factory, out conv) != 0) return null;
                var fmt = BGRA;
                if (Slot<ConverterInitFn>(conv, 8)(conv, frame, ref fmt, 0, IntPtr.Zero, 0.0, 0) != 0) return null;
                if (Slot<GetSizeFn>(conv, 3)(conv, out uint w, out uint h) != 0 || w == 0 || h == 0 || w > 4096 || h > 4096) return null;
                uint stride = w * 4;
                var buf = new byte[stride * h];
                if (Slot<CopyPixelsFn>(conv, 7)(conv, IntPtr.Zero, stride, (uint)buf.Length, buf) != 0) return null;
                var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                for (int y = 0; y < h; y++) Marshal.Copy(buf, (int)(y * stride), data.Scan0 + y * data.Stride, (int)stride);
                bmp.UnlockBits(data);
                return bmp;
            }
            catch { return null; }
            finally
            {
                foreach (var p in new[] { conv, frame, decoder, factory }) if (p != IntPtr.Zero) Marshal.Release(p);
                if (factoryObj != null) Marshal.ReleaseComObject(factoryObj);
            }
        }
    }
}
