using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Pebble
{
    /// <summary>Icons for anything the shell can name: apps (shell:AppsFolder\...), files, shortcuts. Keeps alpha intact.</summary>
    static class ShellIcons
    {
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IShellItemImageFactory { [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm); }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }
        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER { public int biSize, biWidth, biHeight; public short biPlanes, biBitCount; public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant; }
        [StructLayout(LayoutKind.Sequential)]
        struct DIBSECTION { public BITMAP dsBm; public BITMAPINFOHEADER dsBmih; public uint f0, f1, f2; public IntPtr dshSection; public uint dsOffset; }

        [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int size, out DIBSECTION ds);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

        static readonly Dictionary<string, ImageSource> Cache = new Dictionary<string, ImageSource>();

        public static ImageSource Get(string parsingName, int size = 32)
        {
            string key = parsingName + "|" + size;
            if (Cache.TryGetValue(key, out var hit)) return hit;
            ImageSource result = null;
            IntPtr hbm = IntPtr.Zero;
            try
            {
                Guid iid = typeof(IShellItemImageFactory).GUID;
                SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref iid, out object obj);
                var factory = (IShellItemImageFactory)obj;
                // SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY
                if (factory.GetImage(new SIZE { cx = size, cy = size }, 0x1 | 0x4, out hbm) == 0 && hbm != IntPtr.Zero)
                    result = FromHBitmap(hbm);
                Marshal.ReleaseComObject(factory);
            }
            catch { }
            finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
            Cache[key] = result;
            return result;
        }

        static BitmapSource FromHBitmap(IntPtr hbm)
        {
            if (GetObject(hbm, Marshal.SizeOf<DIBSECTION>(), out DIBSECTION ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
                return null;
            int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
            var buf = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, buf, 0, buf.Length);
            if (ds.dsBmih.biHeight > 0) // bottom-up DIB: flip rows
            {
                var flipped = new byte[buf.Length];
                for (int y = 0; y < h; y++) Buffer.BlockCopy(buf, y * stride, flipped, (h - 1 - y) * stride, stride);
                buf = flipped;
            }
            var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, buf, stride);
            bmp.Freeze();
            return bmp;
        }
    }
}
