using System;
using System.Collections.Generic;

namespace FC.Models
{
    /// <summary>
    /// 目录树中的一个节点。只代表"目录"，文件大小聚合到父节点的 DirectSize。
    /// </summary>
    public class DiskNode
    {
        /// <summary>完整路径</summary>
        public string FullPath { get; set; }

        /// <summary>显示名（最后一个路径段）</summary>
        public string Name { get; set; }

        /// <summary>递归总大小（本目录直接文件 + 所有子孙目录）</summary>
        public long TotalSize { get; set; }

        /// <summary>仅本目录直接文件占用的字节数</summary>
        public long DirectSize { get; set; }

        /// <summary>递归聚合的"已分配空间"（按簇大小估算）</summary>
        public long TotalAllocated { get; set; }

        /// <summary>仅本目录直接文件的分配字节（估算）</summary>
        public long DirectAllocated { get; set; }

        /// <summary>递归文件总数</summary>
        public long FileCount { get; set; }

        /// <summary>仅本目录直接文件数量（扫描时计数；DirectFiles 懒加载前的占位判断用）</summary>
        public long DirectFileCount { get; set; }

        /// <summary>已完成聚合的子目录数（扫描期瞬态，驱动每行进度条；节点完成后=Children.Count）</summary>
        public int CompletedChildrenCount { get; set; }

        /// <summary>目录最后修改时间（扫描时采集）</summary>
        public DateTime? LastWriteTime { get; set; }

        /// <summary>是否为目录（本工具树中恒为 true，保留以备用）</summary>
        public bool IsFolder { get; set; }

        /// <summary>是否为联接（junction）/重解析点。为 true 时不递归，避免环路</summary>
        public bool IsReparsePoint { get; set; }

        /// <summary>扫描该目录时发生的错误（无权限/路径过长等），null 表示正常</summary>
        public string Error { get; set; }

        /// <summary>子目录（按大小降序）</summary>
        public List<DiskNode> Children { get; set; }

        /// <summary>本目录直接文件（紧凑结构，按大小降序）。文件行在展开时才物化。</summary>
        public List<FileEntry> DirectFiles { get; set; }

        public DiskNode()
        {
            Children = new List<DiskNode>();
            DirectFiles = new List<FileEntry>();
        }

        // ==================== 扫描瞬态字段（不参与缓存序列化；缓存使用镜像类） ====================

        /// <summary>扫描：父节点（驱动自底向上聚合）</summary>
        public DiskNode ParentNode;

        /// <summary>扫描：待完成的目录子节点数（入队数）</summary>
        public int PendingRemaining;

        /// <summary>扫描：已完成的目录子节点数</summary>
        public int PendingDone;

        /// <summary>扫描：0/1 自身是否已枚举</summary>
        public int PendingFilesDone;

        /// <summary>扫描：0/1 是否已完成聚合</summary>
        public int PendingCompleted;

        /// <summary>扫描：是否命中排除列表（跳过不枚举，计入"已排除"）</summary>
        public bool IsSkipped;

        /// <summary>是否为隐藏/系统文件（文件行显示标记）</summary>
        public bool IsHiddenOrSystem;

        /// <summary>特殊文件作用说明（如 pagefile.sys = 虚拟内存页面文件）</summary>
        public string SpecialNote { get; set; }
    }
}
