using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;
using System.Xml.Serialization;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// "快速复用"缓存：把整棵扫描树镜像序列化到 %APPDATA%\FC\scan-cache.xml，按扫描目标分目录存放。
    /// 快速模式重扫时按目录 mtime 校验：父枚举里拿到的子目录 mtime 与缓存一致 → 整棵子树直接复用（不再枚举），
    /// 不一致 → 只深入变化分支。内容级修改（不改目录 mtime）可能使缓存偏旧，用"强制完整扫描"（取消勾选快速复用）兜底。
    /// </summary>
    public class ScanCache
    {
        [XmlRoot("ScanCache")]
        public class CacheFile
        {
            [XmlElement("Target")]
            public List<CachedTarget> Targets { get; set; }

            public CacheFile()
            {
                Targets = new List<CachedTarget>();
            }
        }

        public class CachedTarget
        {
            [XmlAttribute]
            public string Path { get; set; }

            [XmlElement]
            public CachedDir Root { get; set; }
        }

        public class CachedDir
        {
            [XmlAttribute]
            public string Path { get; set; }

            [XmlAttribute]
            public string Name { get; set; }

            [XmlAttribute]
            public long LastWriteUtc { get; set; }

            [XmlAttribute]
            public long TotalSize { get; set; }

            [XmlAttribute]
            public long TotalAllocated { get; set; }

            [XmlAttribute]
            public long DirectSize { get; set; }

            [XmlAttribute]
            public long DirectAllocated { get; set; }

            [XmlAttribute]
            public long FileCount { get; set; }

            [XmlAttribute]
            public long DirectFileCount { get; set; }

            [XmlAttribute]
            public bool IsReparsePoint { get; set; }

            [XmlAttribute]
            public bool IsSkipped { get; set; }

            [XmlAttribute]
            public string Error { get; set; }

            [XmlElement("Dir")]
            public List<CachedDir> Children { get; set; }

            public CachedDir()
            {
                Children = new List<CachedDir>();
            }
        }

        private readonly string _filePath;
        private CacheFile _all;

        public ScanCache(string filePath)
        {
            _filePath = filePath;
        }

        public static string DefaultPath()
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC");
            return System.IO.Path.Combine(dir, "scan-cache.xml");
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    _all = new CacheFile();
                    return;
                }
                var ser = new XmlSerializer(typeof(CacheFile));
                using (var fs = File.OpenRead(_filePath))
                {
                    _all = (CacheFile)ser.Deserialize(fs);
                }
                if (_all == null || _all.Targets == null)
                {
                    _all = new CacheFile();
                }
            }
            catch (Exception)
            {
                _all = new CacheFile();
            }
        }

        /// <summary>取某扫描目标的缓存树（无则 null）。线程：扫描前读取，之后只读。</summary>
        public CachedDir GetTargetRoot(string target)
        {
            if (_all == null)
            {
                return null;
            }
            foreach (var t in _all.Targets)
            {
                if (string.Equals(t.Path, target, StringComparison.OrdinalIgnoreCase))
                {
                    return t.Root;
                }
            }
            return null;
        }

        /// <summary>保存扫描结果（覆盖该目标条目）。</summary>
        public void Save(string target, DiskNode root)
        {
            try
            {
                if (_all == null)
                {
                    Load();
                }
                _all.Targets.RemoveAll(t => string.Equals(t.Path, target, StringComparison.OrdinalIgnoreCase));
                _all.Targets.Add(new CachedTarget { Path = target, Root = ToCache(root) });

                string dir = System.IO.Path.GetDirectoryName(_filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                string tmp = _filePath + ".tmp";
                var ser = new XmlSerializer(typeof(CacheFile));
                var settings = new XmlWriterSettings { Indent = false, Encoding = new UTF8Encoding(false) };
                var ns = new XmlSerializerNamespaces();
                ns.Add(string.Empty, string.Empty);
                using (var xw = XmlWriter.Create(tmp, settings))
                {
                    ser.Serialize(xw, _all, ns);
                    xw.Flush();
                }
                File.Copy(tmp, _filePath, true);
                File.Delete(tmp);
            }
            catch (Exception)
            {
                // 缓存写失败不影响主流程
            }
        }

        private static CachedDir ToCache(DiskNode n)
        {
            var c = new CachedDir
            {
                Path = n.FullPath,
                Name = n.Name,
                LastWriteUtc = n.LastWriteTime.HasValue ? n.LastWriteTime.Value.ToFileTimeUtc() : 0,
                TotalSize = n.TotalSize,
                TotalAllocated = n.TotalAllocated,
                DirectSize = n.DirectSize,
                DirectAllocated = n.DirectAllocated,
                FileCount = n.FileCount,
                DirectFileCount = n.DirectFileCount,
                IsReparsePoint = n.IsReparsePoint,
                IsSkipped = n.IsSkipped,
                Error = n.Error
            };
            foreach (var child in n.Children)
            {
                if (child.IsFolder)
                {
                    c.Children.Add(ToCache(child));
                }
            }
            return c;
        }

        /// <summary>镜像转回磁盘节点（子树标记为"已完成"，快照即用）。</summary>
        public static DiskNode FromCache(CachedDir c)
        {
            var n = new DiskNode
            {
                FullPath = c.Path,
                Name = c.Name,
                IsFolder = true,
                TotalSize = c.TotalSize,
                TotalAllocated = c.TotalAllocated,
                DirectSize = c.DirectSize,
                DirectAllocated = c.DirectAllocated,
                FileCount = c.FileCount,
                DirectFileCount = c.DirectFileCount,
                IsReparsePoint = c.IsReparsePoint,
                IsSkipped = c.IsSkipped,
                Error = c.Error,
                LastWriteTime = c.LastWriteUtc > 0 ? DateTime.FromFileTimeUtc(c.LastWriteUtc).ToLocalTime() : (DateTime?)null,
                PendingCompleted = 1,
                PendingFilesDone = 1
            };
            foreach (var child in c.Children)
            {
                n.Children.Add(FromCache(child));
            }
            n.CompletedChildrenCount = n.Children.Count;
            return n;
        }
    }
}