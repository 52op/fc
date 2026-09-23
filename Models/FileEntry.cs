using System;

namespace FC.Models
{
    /// <summary>
    /// 目录"直接文件"的紧凑记录（结构体，扫描阶段使用，避免为 25 万文件逐个建对象引发 GC 压力）。
    /// 文件行在 UI 展开对应目录时才物化为 TreeNodeViewModel。
    /// </summary>
    public struct FileEntry
    {
        public string FullPath;
        public string Name;
        public long Size;
        public long Allocated;
        public DateTime LastWriteTime;

        /// <summary>是否为隐藏/系统文件（显示时加标记）</summary>
        public bool IsHiddenOrSystem;

        /// <summary>特殊文件作用说明（如 pagefile.sys → 虚拟内存页面文件）</summary>
        public string SpecialNote;
    }
}
