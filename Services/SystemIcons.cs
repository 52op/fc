using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 资源管理器风格图标（SHGetFileInfo 提取，按文件夹/文件扩展名缓存）。
    /// 文件夹取通用文件夹小图标；文件按扩展名取对应类型图标，配合 SHGFI_USEFILEATTRIBUTES 无需真实文件存在。
    /// </summary>
    public static class SystemIcons
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath, uint dwFileAttributes,
            ref SHFILEINFO psfi, uint cbSizeFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private const uint SHGFI_ICON = 0x000000100;
        private const uint SHGFI_SMALLICON = 0x000000001;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x000000010;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x000000080;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x000000010;

        private static readonly ConcurrentDictionary<string, ImageSource> Cache =
            new ConcurrentDictionary<string, ImageSource>(StringComparer.OrdinalIgnoreCase);

        public static ImageSource GetIcon(DiskNode node)
        {
            if (node == null)
            {
                return null;
            }

            string key;
            string probe;
            if (!node.IsFolder)
            {
                string ext = Path.GetExtension(node.FullPath);
                if (string.IsNullOrEmpty(ext))
                {
                    ext = ".unknown";
                }
                key = "file:" + ext;
                probe = "x" + ext;
                return Cache.GetOrAdd(key, k => Load(probe, false));
            }

            key = "dir";
            return Cache.GetOrAdd(key, k => Load(".folder", true));
        }

        private static ImageSource Load(string probeName, bool isDirectory)
        {
            try
            {
                var sfi = new SHFILEINFO();
                IntPtr r = SHGetFileInfo(
                    probeName,
                    isDirectory ? FILE_ATTRIBUTE_DIRECTORY : FILE_ATTRIBUTE_NORMAL,
                    ref sfi,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);

                if (r == IntPtr.Zero || sfi.hIcon == IntPtr.Zero)
                {
                    return null;
                }

                try
                {
                    ImageSource src = Imaging.CreateBitmapSourceFromHIcon(
                        sfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(16, 16));
                    var fz = src as Freezable;
                    if (fz != null && fz.CanFreeze)
                    {
                        fz.Freeze();
                    }
                    return src;
                }
                finally
                {
                    DestroyIcon(sfi.hIcon);
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}