using System.Collections.Generic;
using FC.Models;

namespace FC.Services
{
    /// <summary>一次扫描的最终结果</summary>
    public class ScanResult
    {
        /// <summary>扫描目标对应的根节点（含完整子树；Children 即第一层目录）</summary>
        public DiskNode RootNode { get; set; }

        /// <summary>扫描目标的第一层子目录（每个已含完整子树与聚合大小）</summary>
        public List<DiskNode> Children { get; set; }

        /// <summary>已排除节点数（无权限 / 无法访问 / junction / 跳过列表）</summary>
        public long ExcludedCount { get; set; }

        /// <summary>快速复用：直接复用缓存的目录数</summary>
        public long ReusedCount { get; set; }

        public long TotalFiles { get; set; }

        public long TotalBytes { get; set; }

        public long TotalFolders { get; set; }
    }
}
