using System;
using System.Collections.Generic;
using System.IO;
using FC.Models;

namespace FC.Services
{
    /// <summary>两个快照间一个目录的变化。</summary>
    public class SnapshotDiffRow
    {
        public string Path;
        public long OldBytes;
        public long NewBytes;
        public long DeltaBytes;
        public bool IsNew;
        public bool IsRemoved;
    }

    /// <summary>快照对比（B6）：定位"又长胖了"的目录。</summary>
    public static class SnapshotDiff
    {
        /// <summary>
        /// 比较新旧快照，返回变化量最大的目录（按 |delta| 降序，最多 limit 条）。
        /// 只对"两边都存在"或"新增/删除"的目录出结果；同路径按大小差比较。
        /// </summary>
        public static List<SnapshotDiffRow> Compute(ScanSnapshot oldSnap, ScanSnapshot newSnap, int limit)
        {
            var result = new List<SnapshotDiffRow>();
            if (oldSnap == null || newSnap == null)
            {
                return result;
            }

            var oldMap = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in oldSnap.Entries)
            {
                if (!string.IsNullOrEmpty(e.Path))
                {
                    oldMap[e.Path] = e;
                }
            }
            var newMap = new Dictionary<string, SnapshotEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var e in newSnap.Entries)
            {
                if (!string.IsNullOrEmpty(e.Path))
                {
                    newMap[e.Path] = e;
                }
            }

            var keys = new HashSet<string>(oldMap.Keys, StringComparer.OrdinalIgnoreCase);
            foreach (var k in newMap.Keys)
            {
                keys.Add(k);
            }

            foreach (var k in keys)
            {
                SnapshotEntry o, n;
                bool hasOld = oldMap.TryGetValue(k, out o);
                bool hasNew = newMap.TryGetValue(k, out n);
                long ob = hasOld ? o.Bytes : 0;
                long nb = hasNew ? n.Bytes : 0;
                long delta = nb - ob;
                if (delta == 0 && hasOld && hasNew)
                {
                    continue;
                }
                result.Add(new SnapshotDiffRow
                {
                    Path = k,
                    OldBytes = ob,
                    NewBytes = nb,
                    DeltaBytes = delta,
                    IsNew = !hasOld && hasNew,
                    IsRemoved = hasOld && !hasNew
                });
            }

            result.Sort((a, b) => Math.Abs(b.DeltaBytes).CompareTo(Math.Abs(a.DeltaBytes)));
            if (limit > 0 && result.Count > limit)
            {
                result.RemoveRange(limit, result.Count - limit);
            }
            return result;
        }

        /// <summary>快照文件路径（%APPDATA%\FC\scan-snapshot.xml）。</summary>
        public static string SnapshotFilePath(string target)
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC");
            string safe = SafeName(target);
            return Path.Combine(dir, "snapshot-" + safe + ".xml");
        }

        private static string SafeName(string target)
        {
            if (string.IsNullOrEmpty(target))
            {
                return "default";
            }
            var sb = new System.Text.StringBuilder();
            foreach (char c in target)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            string s = sb.ToString().Trim('_');
            return s.Length > 40 ? s.Substring(0, 40) : s;
        }
    }
}