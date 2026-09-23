using System;
using System.Threading;
using System.Threading.Tasks;
using FC.Models;

namespace FC.Services
{
    public interface ICopyMigrator
    {
        /// <summary>
        /// 迁移前校验。返回 null 表示可以迁移，否则返回错误描述（如目标在源内部等）。
        /// </summary>
        string ValidateMigration(string source, string destRoot);

        /// <summary>
        /// 迁移目录：
        /// 1) robocopy source -> destRoot\目录名
        /// 2) 校验文件树一致
        /// 3) 删除原目录（若被占用，通过结果 Lockers 返回占用进程）
        /// 4) mklink /J source -> dest
        /// 5) 写入迁移记录
        /// progress：0-100 阶段进度（可选）。
        /// </summary>
        Task<MigrationResult> MigrateAsync(string source, string destRoot, IProgress<string> log, CancellationToken ct, Action<double> progress = null);

        /// <summary>
        /// 还原目录（顺序很重要，必须这样）：
        /// 1) 先删除源位置联接（junction）
        /// 2) 重建空目录
        /// 3) robocopy dest -> source 拷回数据
        /// 4) 校验回拷结果
        /// 5) 清理目标位置副本，记录标记为已还原
        /// progress：0-100 阶段进度（可选）。
        /// </summary>
        Task<MigrationResult> RestoreAsync(MigrationRecord record, IProgress<string> log, CancellationToken ct, Action<double> progress = null);

        /// <summary>
        /// 自动回退：迁移在"删除原目录/建联接"阶段失败（目标副本完整、源目录可能被删掉一部分）时，
        /// 把目标副本数据放回原位置并清理目标副本。成功返回 result.Success=true。
        /// </summary>
        Task<MigrationResult> RollbackAsync(string source, string dest, IProgress<string> log, CancellationToken ct);
    }
}