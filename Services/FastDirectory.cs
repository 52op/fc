using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace FC.Services
{
    /// <summary>
    /// 快速目录枚举：FindFirstFileEx + FIND_FIRST_EX_LARGE_FETCH（NTFS 批量抓取目录项），
    /// 每次 FindFirst 即可拿到名字/属性/大小/时间，避免 .NET EnumerateFileSystemInfos 的逐项包装对象开销，
    /// 并显著降低系统调用轮次（实时防护环境下收益巨大）。
    /// </summary>
    public static class FastDirectory
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WIN32_FIND_DATA
        {
            public uint dwFileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
            public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
            public uint nFileSizeHigh;
            public uint nFileSizeLow;
            public uint dwReserved0;
            public uint dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
            public string cAlternateFileName;
        }

        private enum FINDEX_INFO_LEVELS
        {
            FindExInfoStandard = 0,
            FindExInfoBasic = 1,
            FindExInfoMaxInfoLevel = 2
        }

        private enum FINDEX_SEARCH_OPS : uint
        {
            FindExSearchNameMatch = 0,
            FindExSearchLimitToDirectories = 1,
            FindExSearchLimitToDevices = 2
        }

        private const uint FIND_FIRST_EX_LARGE_FETCH = 0x0000002;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private const uint FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        private const uint FILE_ATTRIBUTE_HIDDEN = 0x2;
        private const uint FILE_ATTRIBUTE_SYSTEM = 0x4;
        private const uint INVALID_FILE_ATTRIBUTES = 0xFFFFFFFF;
        private const int ERROR_ACCESS_DENIED = 5;
        private const int ERROR_NO_MORE_FILES = 18;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindFirstFileEx(
            string lpFileName, FINDEX_INFO_LEVELS fInfoLevelId,
            out WIN32_FIND_DATA lpFindFileData, FINDEX_SEARCH_OPS fSearchOp,
            IntPtr lpSearchFilter, uint dwAdditionalFlags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindNextFile(IntPtr hFindFile, out WIN32_FIND_DATA lpFindFileData);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool FindClose(IntPtr hFindFile);

        /// <summary>目录项（目录用）</summary>
        public struct DirInfoData
        {
            public string FullPath;
            public string Name;
            public bool IsReparse;
            public DateTime LastWriteTime;
        }

        /// <summary>文件项（文件名 + 逻辑长度 + 修改时间 + 隐藏/系统标记）</summary>
        public struct FileInfoData
        {
            public string FullPath;
            public string Name;
            public long Length;
            public DateTime LastWriteTime;

            /// <summary>是否为隐藏/系统文件（由枚举到的属性一次判定，免二次属性查询）</summary>
            public bool IsHiddenOrSystem;
        }

        /// <summary>
        /// 枚举 dir 的直接子项。成功返回 0；权限等错误抛出对应异常（带 Win32 码）。
        /// </summary>
        public static void List(string dir, List<DirInfoData> dirs, List<FileInfoData> files)
        {
            string pattern = dir + "\\*";
            WIN32_FIND_DATA fd;
            IntPtr h = FindFirstFileEx(
                pattern, FINDEX_INFO_LEVELS.FindExInfoBasic,
                out fd, FINDEX_SEARCH_OPS.FindExSearchNameMatch,
                IntPtr.Zero, FIND_FIRST_EX_LARGE_FETCH);

            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                ThrowForLastError(dir);
            }

            try
            {
                do
                {
                    if (fd.cFileName == "." || fd.cFileName == "..")
                    {
                        continue;
                    }

                    string full = dir + "\\" + fd.cFileName;
                    bool isDir = (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    bool isReparse = (fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0;
                    DateTime lastWrite = FileTimeToDateTime(fd.ftLastWriteTime);

                    if (isDir)
                    {
                        dirs.Add(new DirInfoData
                        {
                            FullPath = full,
                            Name = fd.cFileName,
                            IsReparse = isReparse,
                            LastWriteTime = lastWrite
                        });
                    }
                    else
                    {
                        long size = ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                        files.Add(new FileInfoData
                        {
                            FullPath = full,
                            Name = fd.cFileName,
                            Length = size,
                            LastWriteTime = lastWrite,
                            IsHiddenOrSystem = (fd.dwFileAttributes & (FILE_ATTRIBUTE_HIDDEN | FILE_ATTRIBUTE_SYSTEM)) != 0
                        });
                    }
                }
                while (FindNextFile(h, out fd));
            }
            finally
            {
                FindClose(h);
            }

            int err = Marshal.GetLastWin32Error();
            if (err != ERROR_NO_MORE_FILES)
            {
                // FindNextFile 因其他错误结束
            }
        }

        private static DateTime FileTimeToDateTime(System.Runtime.InteropServices.ComTypes.FILETIME ft)
        {
            long ticks = ((long)(uint)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
            try
            {
                return DateTime.FromFileTimeUtc(ticks);
            }
            catch (Exception)
            {
                return DateTime.MinValue;
            }
        }

        private static void ThrowForLastError(string dir)
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_ACCESS_DENIED)
            {
                throw new UnauthorizedAccessException("对路径访问被拒绝：" + dir);
            }
            throw new IOException("枚举目录失败 (Win32 " + err + ")：" + dir);
        }
    }
}