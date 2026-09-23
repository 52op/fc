using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace FC.Services
{
    /// <summary>
    /// 卷信息：总容量/可用空间（GetDiskFreeSpaceEx）+ 簇大小（GetDiskFreeSpace）。
    /// 簇大小用于"已分配空间"估算（实际分配 = ceil(文件长/簇)×簇）。
    /// </summary>
    public static class VolumeInfo
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(
            string lpDirectoryName,
            out ulong lpFreeBytesAvailableToCaller,
            out ulong lpTotalNumberOfBytes,
            out ulong lpTotalNumberOfFreeBytes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpace(
            string lpRootPathName,
            out uint lpSectorsPerCluster,
            out uint lpBytesPerSector,
            out uint lpNumberOfFreeClusters,
            out uint lpTotalNumberOfClusters);

        private static readonly ConcurrentDictionary<string, int> ClusterCache =
            new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        public static string GetVolumeRoot(string path)
        {
            try
            {
                return Path.GetPathRoot(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>某路径所在卷的 可用/总容量（字节）。失败返回 false。</summary>
        public static bool GetVolumeStats(string path, out long freeBytes, out long totalBytes)
        {
            freeBytes = 0;
            totalBytes = 0;
            string root = GetVolumeRoot(path);
            if (root == null)
            {
                return false;
            }

            ulong avail, total, free2;
            if (!GetDiskFreeSpaceEx(root, out avail, out total, out free2))
            {
                return false;
            }
            freeBytes = (long)free2;
            totalBytes = (long)total;
            return true;
        }

        /// <summary>某路径所在卷的簇大小（按卷根缓存）。默认 4096。</summary>
        public static int GetClusterSize(string path)
        {
            string root = GetVolumeRoot(path);
            if (root == null)
            {
                return 4096;
            }
            return ClusterCache.GetOrAdd(root, r =>
            {
                uint spc, bps, nfc, ntc;
                if (!GetDiskFreeSpace(r, out spc, out bps, out nfc, out ntc))
                {
                    return 4096;
                }
                long cluster = (long)spc * bps;
                if (cluster <= 0 || cluster > int.MaxValue)
                {
                    return 4096;
                }
                return (int)cluster;
            });
        }

        /// <summary>按簇向上取整（估算文件实际占用）</summary>
        public static long RoundUpToCluster(long length, int cluster)
        {
            if (length <= 0)
            {
                return 0;
            }
            long c = cluster;
            return ((length + c - 1) / c) * c;
        }
    }
}
