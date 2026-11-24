using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace Pill.Services;

internal static class IconHelper
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiSmallIcon = 0x000000001;
    private const uint ShgfiAdOverlays = 0x000040000;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes,
        out ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static BitmapSource? GetFileIcon(string path)
    {
        try
        {
            var info = new ShFileInfo();
            IntPtr result = SHGetFileInfo(path, 0, out info,
                (uint)Marshal.SizeOf(typeof(ShFileInfo)),
                ShgfiIcon | ShgfiSmallIcon | ShgfiAdOverlays);

            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            DestroyIcon(info.hIcon);
            return image;
        }
        catch
        {
            return null;
        }
    }
}
