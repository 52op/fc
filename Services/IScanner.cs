using System;
using System.Threading;
using System.Threading.Tasks;
using FC.Models;

namespace FC.Services
{
    public interface IScanner
    {
        /// <summary>
        /// 深度扫描 path。结构/大小并行计算，顶层目录完成经 progress 渐进上报（NewTopNode）。
        /// </summary>
        Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress> progress, CancellationToken ct);
    }
}
