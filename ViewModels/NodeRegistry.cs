using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace FC.ViewModels
{
    /// <summary>
    /// 已创建的树节点 VM 登记表（按路径）。扫描流式期间，某目录枚举/聚合完成时，
    /// 用它找到对应 VM 做 ResyncFromData，让已展开的行实时长出占位/文件/大小。
    /// 扫描开始时 Clear；VM 构造时 Register。
    /// </summary>
    public static class NodeRegistry
    {
        private static readonly ConcurrentDictionary<string, TreeNodeViewModel> Map =
            new ConcurrentDictionary<string, TreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);

        public static void Register(TreeNodeViewModel vm)
        {
            if (vm == null)
            {
                return;
            }
            string path = vm.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            Map[path] = vm;
        }

        public static TreeNodeViewModel Find(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }
            TreeNodeViewModel vm;
            return Map.TryGetValue(path, out vm) ? vm : null;
        }

        /// <summary>当前已创建的全部 VM 快照（计时器刷新行进度用）。</summary>
        public static List<TreeNodeViewModel> GetAll()
        {
            return new List<TreeNodeViewModel>(Map.Values);
        }

        public static void Clear()
        {
            Map.Clear();
        }
    }
}