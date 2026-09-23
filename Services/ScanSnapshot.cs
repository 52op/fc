using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Serialization;
using FC.Models;

namespace FC.Services
{
    /// <summary>快照中的一条目录记录（路径 + 递归大小 + 文件数）。</summary>
    public class SnapshotEntry
    {
        [XmlAttribute]
        public string Path;

        [XmlAttribute]
        public long Bytes;

        [XmlAttribute]
        public long Files;
    }

    /// <summary>
    /// 一次扫描后的目录占用快照（B6 增量对比用）。
    /// 保存为 %APPDATA%\FC\scan-snapshot.xml；下一轮扫描完成后与本次对比，列出增长/减少最多的目录。
    /// 只存目录聚合值（大小/文件数），不存明细，体积可控。
    /// </summary>
    [XmlRoot("ScanSnapshot")]
    public class ScanSnapshot
    {
        [XmlAttribute]
        public string Target;

        [XmlAttribute]
        public long SavedAtUtcMs;

        [XmlElement("Entry")]
        public List<SnapshotEntry> Entries = new List<SnapshotEntry>();

        public ScanSnapshot()
        {
        }

        /// <summary>从当前扫描树生成快照（遍历全部目录节点）。</summary>
        public static ScanSnapshot Capture(DiskNode root)
        {
            var snap = new ScanSnapshot();
            if (root == null)
            {
                return snap;
            }
            snap.Target = root.FullPath;
            snap.SavedAtUtcMs = DateTime.UtcNow.Ticks;
            var stack = new Stack<DiskNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DiskNode d = stack.Pop();
                try
                {
                    snap.Entries.Add(new SnapshotEntry
                    {
                        Path = d.FullPath,
                        Bytes = d.TotalSize,
                        Files = d.FileCount
                    });
                }
                catch (Exception)
                {
                }
                if (d.Children != null)
                {
                    foreach (var c in d.Children)
                    {
                        if (c.IsFolder)
                        {
                            stack.Push(c);
                        }
                    }
                }
            }
            return snap;
        }

        /// <summary>保存快照（后台线程调用；失败静默，不阻塞扫描流程）。</summary>
        public static void Save(string file, ScanSnapshot snap)
        {
            try
            {
                if (snap == null || string.IsNullOrEmpty(file))
                {
                    return;
                }
                string dir = Path.GetDirectoryName(file);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                var ser = new XmlSerializer(typeof(ScanSnapshot));
                using (var xw = System.Xml.XmlWriter.Create(file, new System.Xml.XmlWriterSettings { Indent = false }))
                {
                    var ns = new XmlSerializerNamespaces();
                    ns.Add(string.Empty, string.Empty);
                    ser.Serialize(xw, snap, ns);
                }
            }
            catch (Exception)
            {
            }
        }

        /// <summary>加载快照（文件缺失/损坏返回 null）。</summary>
        public static ScanSnapshot Load(string file)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file))
                {
                    return null;
                }
                var ser = new XmlSerializer(typeof(ScanSnapshot));
                using (var fs = File.OpenRead(file))
                {
                    return ser.Deserialize(fs) as ScanSnapshot;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}