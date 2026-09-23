using FC.Models;

namespace FC.Models
{
    /// <summary>扫描阶段：Structure=构建中（渐进上报），Done=完成（RootNode 携带整棵树）</summary>
    public enum ScanPhase
    {
        Structure,
        Sizing,
        Done
    }

    /// <summary>扫描进度消息，经 IProgress&lt;ScanProgress&gt; 推送到 UI 线程</summary>
    public class ScanProgress
    {
        public ScanPhase Phase { get; set; }

        /// <summary>完成时携带整棵树根节点</summary>
        public DiskNode RootNode { get; set; }

        /// <summary>一个"顶层目录"子树完成时携带该节点（UI 用它刷新该行大小）</summary>
        public DiskNode NewTopNode { get; set; }

        /// <summary>扫描目标自身枚举完成时携带根节点（UI 用它一次挂整棵目录树）</summary>
        public DiskNode RootReady { get; set; }

        /// <summary>某个目录枚举完成/聚合完成时携带该节点（UI 刷新对应行，展开占位/大小）</summary>
        public DiskNode EnumeratedNode { get; set; }

        /// <summary>已完成（含子树聚合）的目录数</summary>
        public long CompletedFolders { get; set; }

        /// <summary>目录总数（仅完成时有效）</summary>
        public long TotalFolders { get; set; }

        /// <summary>已完成的子树累计大小</summary>
        public long CompletedBytes { get; set; }

        public long CompletedFiles { get; set; }

        /// <summary>已排除（无权限/无法访问/junction）节点数</summary>
        public long ExcludedCount { get; set; }

        /// <summary>快速复用：直接复用缓存的目录数</summary>
        public long ReusedCount { get; set; }

        /// <summary>命中排除列表而跳过的目录数</summary>
        public long SkippedCount { get; set; }

        public string CurrentPath { get; set; }

        public bool IsDone { get; set; }

        /// <summary>阶段A（目录结构）就绪，RootNode 此时有效，UI 一次挂整棵树</summary>
        public bool StructureReady { get; set; }
    }
}
