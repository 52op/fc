using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FC.Services
{
    public class VerificationReport
    {
        public bool Ok { get; set; }

        public long SourceFiles { get; set; }

        public long TargetFiles { get; set; }

        public long SourceBytes { get; set; }

        public long TargetBytes { get; set; }

        /// <summary>差异描述（最多 50 条），前缀"目标缺少"/"大小不一致"/"目标多出"</summary>
        public List<string> Differences { get; set; }

        public VerificationReport()
        {
            Differences = new List<string>();
        }
    }

    public interface IVerifier
    {
        /// <summary>
        /// 比对两棵目录树的文件：相对路径、大小（存在性）。
        /// Ok 为 false 只表示"目标缺少"或"大小不一致"；目标多出的文件按警告记录，不算失败。
        /// </summary>
        Task<VerificationReport> VerifyAsync(string src, string dst, CancellationToken ct);
    }
}
