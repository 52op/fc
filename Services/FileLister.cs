using System;
using System.Collections.Generic;
using System.IO;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 按需读取一个目录的"直接文件"明细（展开目录行时才调用，FastDirectory 快速枚举）。
    /// 返回的文件项按大小降序。
    /// </summary>
    public static class FileLister
    {
        public static List<FileEntry> ReadEntries(string dir)
        {
            var files = new List<FileEntry>();
            try
            {
                var dirs = new List<FastDirectory.DirInfoData>();
                var filesData = new List<FastDirectory.FileInfoData>();
                FastDirectory.List(dir, dirs, filesData);

                int cluster = VolumeInfo.GetClusterSize(dir);
                foreach (var f in filesData)
                {
                    files.Add(new FileEntry
                    {
                        FullPath = f.FullPath,
                        Name = f.Name,
                        Size = f.Length,
                        Allocated = VolumeInfo.RoundUpToCluster(f.Length, cluster),
                        LastWriteTime = f.LastWriteTime,
                        SpecialNote = SpecialFiles.GetNote(f.Name),
                        IsHiddenOrSystem = SpecialFiles.IsHiddenOrSystem(f.FullPath)
                    });
                }
            }
            catch (Exception)
            {
                // 无权限等：返回已读到的部分
            }

            files.Sort((a, b) =>
            {
                int bySize = b.Size.CompareTo(a.Size);
                if (bySize != 0)
                {
                    return bySize;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            return files;
        }
    }
}