using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FC.Converters;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 迁移 / 还原全流程编排。
    /// 迁移：robocopy 复制 -> 校验 -> 删原目录 -> mklink /J 建联接 -> 写记录。
    /// 还原：删联接 -> mkdir 重建空目录 -> robocopy 拷回 -> 校验 -> 清理目标副本 -> 记录标已还原。
    /// 任何步骤因文件被占用失败时，经 LockFinder 查出占用进程放进结果 Lockers，由调用方提示用户结束进程后重试。
    /// 删除统一走 FileDeleter（长路径安全、不跟随联接、逐条报告失败路径）。
    /// </summary>
    public class CopyMigrator : ICopyMigrator
    {
        private readonly IVerifier _verifier;
        private readonly IRecordStore _store;
        private readonly int _threads;
        private readonly int _retries;
        private readonly int _retryWaitSec;

        public CopyMigrator(IVerifier verifier, IRecordStore store)
        {
            _verifier = verifier;
            _store = store;
            _threads = ReadConfigInt("RobocopyThreads", 16);
            _retries = ReadConfigInt("RobocopyRetries", 2);
            _retryWaitSec = ReadConfigInt("RobocopyWaitSeconds", 2);
        }

        private static int ReadConfigInt(string key, int defaultValue)
        {
            try
            {
                string s = ConfigurationManager.AppSettings[key];
                int v;
                return int.TryParse(s, out v) ? v : defaultValue;
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        public string ValidateMigration(string source, string destRoot)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destRoot))
            {
                return "源或目标路径为空。";
            }
            if (!Directory.Exists(source))
            {
                return "源目录不存在：" + source;
            }
            if (JunctionUtil.IsReparsePoint(source))
            {
                return "源目录已是联接（junction），不能迁移。如需还原请在“查看迁移记录”里操作。";
            }
            if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destRoot), StringComparison.OrdinalIgnoreCase))
            {
                return "目标不能和源目录相同。";
            }
            if (IsSubPath(destRoot, source))
            {
                return "目标不能位于源目录内部，否则会无限递归。";
            }
            if (IsSubPath(source, destRoot))
            {
                return "源目录不能位于目标内部（不能把数据移进自身的子孙目录）。";
            }

            string destFolder = Path.Combine(destRoot, Path.GetFileName(source));
            if (Directory.Exists(destFolder))
            {
                return "目标位置已存在同名目录，请改名或换目录：" + destFolder;
            }
            return null;
        }

        public static bool IsSubPath(string child, string parent)
        {
            try
            {
                string c = Path.GetFullPath(child).TrimEnd('\\', '/');
                string p = Path.GetFullPath(parent).TrimEnd('\\', '/');
                return c.StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint DELETE = 0x00010000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        // ==================== 迁移前预检 ====================

        /// <summary>
        /// 复制开始前先查占用与"目录可删除性"。
        /// 返回 null 表示可以继续；否则返回带提示的失败结果（复制还没开始，无回退需要）。
        /// </summary>
        private MigrationResult PreflightSource(string source, IProgress<string> log)
        {
            // 1) 占用进程
            var lockers = LockFinder.Find(source);
            if (lockers != null && lockers.Count > 0)
            {
                var r = new MigrationResult();
                r.Lockers = lockers;
                r.Message = "迁移前检测到以下进程正在占用源目录："
                    + Environment.NewLine + LockFinder.Describe(lockers)
                    + Environment.NewLine + "（尚未开始复制，未产生任何变更。）"
                    + Environment.NewLine + "结束这些进程后可重试迁移。";
                Log(log, r.Message);
                return r;
            }

            // 2) 目录"不可删除/不可重命名"探测（cmd/资源管理器把工作目录停在此处、ACL 拒绝都会失败）
            if (!PreflightRenameProbe(source, log))
            {
                string suspects = LockFinder.LookupConsoleSuspects();
                if (!string.IsNullOrEmpty(suspects))
                {
                    var r = new MigrationResult();
                    r.Message = "迁移前预检发现：源目录当前处于“不可移动/不可删除”状态，且检测到命令行窗口进程（"
                        + suspects + "）。"
                        + Environment.NewLine + "常见原因：某个 cmd/Powershell 窗口的工作目录正停在该目录，会锁住它无法删除。"
                        + Environment.NewLine + "请先关闭这些命令行窗口（或在其中执行 cd /d C:\\ 切走），再重试迁移。（尚未开始复制，未产生任何变更。）";
                    Log(log, r.Message);
                    return r;
                }
                // 无可疑命令窗口：可能是 ACL（稍后 icacls 能修），先警告继续
                Log(log, "预检：源目录当前不可移动/删除（可能是 ACL 或占用者）。复制后将自动尝试修复（icacls /reset）并删除。");
            }
            else
            {
                Log(log, "预检通过：源目录可正常移动/删除，无占用进程。");
            }
            return null;
        }

        /// <summary>
        /// 放弃式改名探测：把源目录改名到临时名，再立即改回。改名与删除共享同一类"目录级锁"语义，
        /// cmd 工作目录锁/ACL 拒绝都会让它失败——能提前判断真实的删除可行性，而不影响任何数据。
        /// 探测耗时与子项数量无关（只改目录自身索引项）。
        /// </summary>
        public static bool PreflightRenameProbe(string dir, IProgress<string> log)
        {
            if (!Directory.Exists(dir))
            {
                return true;
            }
            string tmp = dir.TrimEnd('\\') + "__fc_pf_" + Guid.NewGuid().ToString("N");
            try
            {
                Directory.Move(dir, tmp);
            }
            catch (Exception ex)
            {
                Log(log, "预检改名失败（目录被锁）：" + ex.Message);
                return false;
            }

            // 立即改回（最多重试几次，返回失败也尽量恢复）
            bool back = false;
            for (int i = 0; i < 5 && !back; i++)
            {
                try
                {
                    Directory.Move(tmp, dir);
                    back = true;
                }
                catch (Exception)
                {
                    try
                    {
                        Thread.Sleep(200);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            if (!back)
            {
                Log(log, "预检改名探测的“改回”未能完成，请勿手动操作该目录：" + tmp);
            }
            return back;
        }

        /// <summary>以 DELETE 访问打开目录句柄，探测目录是否可被删除（工作目录锁/ACL 拒绝会失败）。</summary>
        public static bool CanDeleteDirectory(string path)
        {
            try
            {
                IntPtr h = CreateFileW(path, DELETE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING,
                    FILE_FLAG_BACKUP_SEMANTICS | FILE_ATTRIBUTE_DIRECTORY, IntPtr.Zero);
                if (h == INVALID_HANDLE_VALUE || h == IntPtr.Zero)
                {
                    return false;
                }
                CloseHandle(h);
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        // ==================== 迁移 ====================

        public async Task<MigrationResult> MigrateAsync(
            string source, string destRoot, IProgress<string> log, CancellationToken ct, Action<double> progress = null)
        {
            var result = new MigrationResult();
            try
            {
                ct.ThrowIfCancellationRequested();
                SetProgress(progress, -1);

                string error = ValidateMigration(source, destRoot);
                if (error != null)
                {
                    throw new MigrationException(error);
                }

string folderName = Path.GetFileName(source);
                string dest = Path.Combine(destRoot, folderName);

                // ---- 步骤 0：迁移前预检（复制前发现占用/不可删除，避免白等数十秒；后台执行以免卡 UI）----
                var preflight = await Task.Run(() => PreflightSource(source, log), ct);
                if (preflight != null)
                {
                    return preflight; // 占用者或命令行窗口锁定 → 直接返回，由 VM 决定结束进程/重试
                }

// 步骤 1：robocopy 复制（含被占用文件的重试；按文件数推进真实进度）
                Log(log, "【步骤 1/5】robocopy 复制：" + source + "  ->  " + dest);
                long expectedFiles = await CountSourceFilesAsync(source, ct);
                if (expectedFiles > 0)
                {
                    Log(log, "预计 " + expectedFiles + " 个文件，复制阶段按实际文件数显示进度。");
                }
                int code = await RunRobocopyAsync(source, dest, log, ct, 0, 30, expectedFiles, (progress != null ? new Progress<double>(progress) : null));
                result.RobocopyExitCode = code;
                if (code >= 8)
                {
                    Log(log, "robocopy 失败，退出码 " + code + "（>=8 表示有复制错误）");
                    if (TryGetLockers(log, new[] { source, dest }, result))
                    {
                        result.Message = "复制失败（退出码 " + code + "）。检测到以下进程可能占用源/目标目录："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "结束这些进程后可重试迁移。";
                        return result;
                    }
                    TryRollbackDest(dest, log);
                    throw new MigrationException("robocopy 复制失败（退出码 " + code + "）。已删除目标副本，源目录保持不动。");
                }
                SetProgress(progress, 30);

                // 步骤 2：校验
                Log(log, "【步骤 2/5】校验文件树一致…");
                var report = await _verifier.VerifyAsync(source, dest, ct);
                if (!report.Ok)
                {
                    Log(log, "校验发现不一致：" + string.Join("；", report.Differences.Take(10).ToArray()));
                    if (TryGetLockers(log, new[] { source, dest }, result))
                    {
                        result.Message = "校验不一致，且检测到占用源/目标目录的进程："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "可能有人正在写这些文件。结束进程后重试迁移。";
                        return result;
                    }
                    TryRollbackDest(dest, log);
                    throw new MigrationException("迁移校验失败（文件缺失或大小不一致）。已删除目标副本，源目录保持不动。");
                }
                result.Files = report.SourceFiles;
                result.Bytes = report.SourceBytes;
                Log(log, "校验通过：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes));
                SetProgress(progress, 45);

                // 步骤 3：删除原目录（被占用则查出占用进程）
                Log(log, "【步骤 3/5】删除原目录：" + source);
                SetProgress(progress, -1);
var failed = new List<string>();
                for (int delAttempt = 0; delAttempt < 3; delAttempt++)
                {
                    failed.Clear();
                    try
                    {
                        await Task.Run(() => FileDeleter.DeleteTree(source, log, ct, failed), ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    if (failed.Count == 0 || delAttempt >= 2)
                    {
                        break;
                    }
                    Log(log, "仍有内容无法删除，稍候重试 " + (delAttempt + 1) + "/2 …");
                    try
                    {
                        await Task.Delay(1500, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
                if (failed.Count > 0)
                {
                    Log(log, "以下内容无法删除（可能被占用）：");
                    foreach (var f in failed.Distinct().Take(20))
                    {
                        Log(log, "  " + f);
                    }
if (TryGetLockers(log, failed.ToArray(), result))
                    {
                        result.Message = "源目录删除未完成，以下进程正在占用："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "数据已完整复制到目标。结束进程后可重试迁移以完成清理并建立联接。";
                        return result;
                    }
                    // 无占用进程：尝试把数据自动放回原目录（源目录可能已被删掉一部分）
                    string rb = "";
                    if (result.Files > 0 && Directory.Exists(dest))
                    {
                        rb = (await RollbackAsync(source, dest, log, ct)).Message;
                        Log(log, rb);
                    }
                    string hints = LockFinder.LookupConsoleSuspects();
                    string hintText = string.IsNullOrEmpty(hints)
                        ? ""
                        : Environment.NewLine + "提示：检测到命令行窗口进程（" + hints + "）——如果一个 cmd/Powershell 窗口的当前目录仍停在被迁移的目录里，就会锁住它无法删除。请先关闭这些窗口（或 `cd /d C:\\` 切走）后重试。";
                    throw new MigrationException("删除原目录失败（未检测到占用进程）：" + source
                        + Environment.NewLine + "未能删除：" + failed.First()
                        + Environment.NewLine + rb
                        + hintText);
                }
                SetProgress(progress, 75);

                // 步骤 4：创建 junction（若源目录被进程瞬间重建，清掉后重试一次）
                Log(log, "【步骤 4/5】创建联接（junction）：" + source + "  ->  " + dest);
                if (!JunctionUtil.Create(source, dest, out string jerr))
                {
                    if (Directory.Exists(source) && !JunctionUtil.IsReparsePoint(source))
                    {
                        Log(log, "源路径又出现普通目录，清除后重试建联接…");
                        var failed2 = new List<string>();
                        FileDeleter.DeleteTree(source, log, ct, failed2);
                        if (failed2.Count == 0 && !Directory.Exists(source) &&
                            JunctionUtil.Create(source, dest, out jerr))
                        {
                            Log(log, "重试创建联接成功。");
                        }
                        else
                        {
                            if (failed2.Count > 0 && TryGetLockers(log, failed2.ToArray(), result))
                            {
                                result.Message = "创建联接前源路径被再次创建且被占用："
                                    + Environment.NewLine + LockFinder.Describe(result.Lockers)
                                    + Environment.NewLine + "结束进程后可重试迁移。";
                                return result;
                            }
                            string rb = "";
                            if (result.Files > 0 && Directory.Exists(dest))
                            {
                                rb = (await RollbackAsync(source, dest, log, ct)).Message;
                                Log(log, rb);
                            }
                            throw new MigrationException("mklink /J 创建联接失败：" + jerr
                                + (string.IsNullOrEmpty(rb) ? "" : Environment.NewLine + rb));
                        }
                    }
                    else
                    {
                        throw new MigrationException("mklink /J 创建联接失败：" + jerr);
                    }
                }
                if (!JunctionUtil.IsReparsePoint(source))
                {
                    Log(log, "警告：建联后未检测到 reparse，请检查目标卷是否支持联接。");
                }
                SetProgress(progress, 90);

                // 步骤 5：写记录
                Log(log, "【步骤 5/5】写入迁移记录…");
                var record = new MigrationRecord
                {
                    SourcePath = source,
                    DestPath = dest,
                    MigratedAt = DateTime.Now,
                    TotalBytes = result.Bytes,
                    FileCount = result.Files,
                    Status = MigrationStatus.Active
                };
                SaveRecordSafely(record, log);

                result.Success = true;
                result.Message = "迁移完成：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes)
                    + "。" + Environment.NewLine + "原位置已替换为联接，数据实际位于：" + dest;
                SetProgress(progress, 100);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "迁移已取消。请手动检查源与目标目录状态。";
                return result;
            }
            catch (MigrationException ex)
            {
                result.Message = ex.Message;
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "迁移异常：" + ex.Message;
                return result;
            }
        }

        // ==================== 还原 ====================

        public async Task<MigrationResult> RestoreAsync(
            MigrationRecord record, IProgress<string> log, CancellationToken ct, Action<double> progress = null)
        {
            var result = new MigrationResult();
            try
            {
                ct.ThrowIfCancellationRequested();
                SetProgress(progress, -1);

                string source = record.SourcePath;
                string dest = record.DestPath;

                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
                {
                    throw new MigrationException("记录中的路径无效，无法还原。");
                }
                if (!Directory.Exists(dest))
                {
                    throw new MigrationException("目标数据目录不存在：" + dest + Environment.NewLine + "请确认目标盘已连接、目录未被手动删除。");
                }

                // 步骤 1：先删联接（用户指定顺序，必须先做）
                Log(log, "【步骤 1/5】删除源位置联接（junction）：" + source);
                if (Directory.Exists(source) || JunctionUtil.IsReparsePoint(source))
                {
                    string err;
                    if (!JunctionUtil.Delete(source, out err))
                    {
                        if (TryGetLockers(log, new[] { source }, result))
                        {
                            result.Message = "删除联接失败，以下进程正在占用："
                                + Environment.NewLine + LockFinder.Describe(result.Lockers)
                                + Environment.NewLine + "结束这些进程后可重试还原。";
                            return result;
                        }
                        throw new MigrationException("删除 junction 失败：" + err);
                    }
                    Log(log, "已删除联接：" + source);
                }
                else
                {
                    Log(log, "源路径不存在或已是普通位置，跳过：" + source);
                }
                SetProgress(progress, 20);

                // 步骤 2：重建空目录
                Log(log, "【步骤 2/5】重建空目录：" + source);
                if (!Directory.Exists(source))
                {
                    Directory.CreateDirectory(source);
                }
                SetProgress(progress, 30);

// 步骤 3：robocopy 拷回
                Log(log, "【步骤 3/5】robocopy 回拷：" + dest + "  ->  " + source);
                long expectedBack = await CountSourceFilesAsync(dest, ct);
                if (expectedBack > 0)
                {
                    Log(log, "预计拷回 " + expectedBack + " 个文件，按实际文件数显示进度。");
                }
                int code = await RunRobocopyAsync(dest, source, log, ct, 20, 60, expectedBack, (progress != null ? new Progress<double>(progress) : null));
                result.RobocopyExitCode = code;
                if (code >= 8)
                {
                    if (TryGetLockers(log, new[] { dest, source }, result))
                    {
                        result.Message = "回拷失败（退出码 " + code + "）。检测到占用源/目标目录的进程："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "结束这些进程后可重试还原。";
                        return result;
                    }
                    throw new MigrationException("robocopy 回拷失败（退出码 " + code + "）。数据仍完整保留在目标目录，源目录为空或部分数据，可重试还原。");
                }
                SetProgress(progress, 60);

                // 步骤 4：校验
                Log(log, "【步骤 4/5】校验回拷结果…");
                var report = await _verifier.VerifyAsync(dest, source, ct);
                if (!report.Ok)
                {
                    if (TryGetLockers(log, new[] { dest, source }, result))
                    {
                        result.Message = "回拷校验不一致，且检测到占用进程："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "结束进程后可重试还原。";
                        return result;
                    }
                    throw new MigrationException("回拷校验不一致，数据未清理。" + Environment.NewLine
                        + "数据仍保留在：" + dest + Environment.NewLine
                        + "差异：" + string.Join("；", report.Differences.Take(5).ToArray()));
                }
                result.Files = report.TargetFiles;
                result.Bytes = report.TargetBytes;
                Log(log, "回拷校验通过：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes));
                SetProgress(progress, 75);

                // 步骤 5：清理目标副本 + 记录状态
                Log(log, "【步骤 5/5】清理目标位置副本：" + dest);
                SetProgress(progress, -1);
                var failed = new List<string>();
                try
                {
                    await Task.Run(() => FileDeleter.DeleteTree(dest, log, ct, failed), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                if (failed.Count > 0)
                {
                    Log(log, "目标副本清理未完成（可能被占用）：");
                    foreach (var f in failed.Distinct().Take(20))
                    {
                        Log(log, "  " + f);
                    }
                    if (TryGetLockers(log, failed.ToArray(), result))
                    {
                        result.Message = "目标副本清理未完成，以下进程正在占用："
                            + Environment.NewLine + LockFinder.Describe(result.Lockers)
                            + Environment.NewLine + "数据已拷回源位置。结束进程后可重试还原以清理目标副本。";
                        return result;
                    }
                    throw new MigrationException("清理目标副本失败（未检测到占用进程，可能是权限/只读）：" + dest
                        + Environment.NewLine + "未能删除：" + failed.First());
                }
                SetProgress(progress, 95);

                record.Status = MigrationStatus.Restored;
                record.RestoredAt = DateTime.Now;
                record.ErrorMessage = null;
                SaveRecordSafely(record, log);

                result.Success = true;
                result.Message = "还原完成：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes)
                    + " 已移回原位置，联接已删除。";
                SetProgress(progress, 100);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "还原已取消。数据仍保留在目标目录，源位置可能为空目录，可重试。";
                return result;
            }
            catch (MigrationException ex)
            {
                result.Message = ex.Message;
                return result;
            }
catch (Exception ex)
            {
                result.Message = "还原异常：" + ex.Message;
                return result;
            }
        }

        // ==================== 自动回退 ====================

        /// <summary>
        /// 迁移在"删除原目录/建联接"阶段失败（目标副本完整、源目录可能被删掉一部分）时调用：
        /// 把目标副本的数据放回原位置，并清理目标副本。成功 result.Success=true。
        /// </summary>
        public async Task<MigrationResult> RollbackAsync(string source, string dest, IProgress<string> log, CancellationToken ct)
        {
            var result = new MigrationResult();
            try
            {
                ct.ThrowIfCancellationRequested();

                if (JunctionUtil.IsReparsePoint(source))
                {
                    result.Success = false;
                    result.Message = "源位置已是联接（junction），无需回退。";
                    return result;
                }
                if (!Directory.Exists(dest))
                {
                    result.Success = false;
                    result.Message = "目标副本不存在（可能已被清理或从未来得及生成）：" + dest;
                    return result;
                }

                Log(log, "【回退】确保源目录存在：" + source);
                if (!Directory.Exists(source))
                {
                    Directory.CreateDirectory(source);
                }

                Log(log, "【回退】robocopy 放回数据：" + dest + "  ->  " + source);
                int code = await RunRobocopyAsync(dest, source, log, ct);
                if (code >= 8)
                {
                    result.Message = "回退复制失败（退出码 " + code + "）。数据仍完整保留在：" + dest + Environment.NewLine
                        + "源目录状态：可能部分/全部已删除。请不要手动删除目标副本，可稍后重试迁移或回退。";
                    return result;
                }

                Log(log, "【回退】校验放回结果…");
                var report = await _verifier.VerifyAsync(dest, source, ct);
                if (!report.Ok)
                {
                    result.Message = "回退校验不一致（数据未清理）。数据仍在：" + dest + Environment.NewLine
                        + "差异：" + string.Join("；", report.Differences.Take(5).ToArray());
                    return result;
                }

                Log(log, "【回退】清理目标副本：" + dest);
                var failed = new List<string>();
                try
                {
                    await Task.Run(() => FileDeleter.DeleteTree(dest, log, ct, failed), ct);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }

                if (failed.Count > 0)
                {
                    result.Success = true; // 数据已放回，仅副本未清干净
                    result.Message = "数据已放回原位置，但目标副本清理未完成（可手动删除）：" + dest + Environment.NewLine
                        + "未能删除：" + failed.First();
                    return result;
                }

                result.Success = true;
                result.Message = "已自动回退：数据已全部放回原位置，目标副本已清理。";
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "回退已取消。数据仍保留在：" + dest;
                return result;
            }
            catch (Exception ex)
            {
                result.Message = "回退异常：" + ex.Message;
                return result;
            }
        }

        // ==================== 内部辅助 ====================

        /// <summary>用 Restart Manager 查占用进程；查到则写入 result.Lockers 并返回 true。</summary>
        private bool TryGetLockers(IProgress<string> log, string[] paths, MigrationResult result)
        {
            try
            {
                var lockers = LockFinder.Find(paths);
                if (lockers != null && lockers.Count > 0)
                {
                    result.Lockers = lockers;
                    return true;
                }
            }
            catch (Exception ex)
            {
                Log(log, "检测占用进程失败：" + ex.Message);
            }
            return false;
        }

private async Task<int> RunRobocopyAsync(
            string src, string dst, IProgress<string> log, CancellationToken ct,
            double stageStart = 0.0, double stageEnd = 30.0,
            long expectedFiles = 0, IProgress<double> progress = null)
        {
            // /E 含子目录与空目录；/COPY:DAT 数据+属性+时间戳；/XJ 不跟随源里的联接；/MT 多线程
            // 去掉 /NFL /NDL，让 robocopy 逐文件输出（不写日志，仅用于计数推进真实进度）
            string args = string.Format(
                "\"{0}\" \"{1}\" /E /COPY:DAT /DCOPY:DAT /R:{2} /W:{3} /MT:{4} /XJ /NP",
                src, dst, _retries, _retryWaitSec, _threads);

            var runner = new CliRunner();
            var res = await runner.RunAsync("robocopy.exe", args,
                new CopyProgressLineSink(log, progress, stageStart, stageEnd, expectedFiles), ct);
            if (res.Cancelled)
            {
                throw new OperationCanceledException(ct);
            }
            return res.ExitCode;
        }

        /// <summary>
        /// robocopy 输出行过滤器：含路径的文件行 → 计数并推进阶段进度（stageStart→stageEnd）；
        /// 其余行（摘要/错误等）照常转发到日志框。日志框因此不会被 2.8 万条文件行刷屏。
        /// </summary>
        private sealed class CopyProgressLineSink : IProgress<string>
        {
            private readonly IProgress<string> _log;
            private readonly IProgress<double> _progress;
            private readonly double _stageStart;
            private readonly double _stageEnd;
            private readonly long _expected;
            private long _processed;
            private double _lastReported = -1;

            public CopyProgressLineSink(
                IProgress<string> log, IProgress<double> progress,
                double stageStart, double stageEnd, long expected)
            {
                _log = log;
                _progress = progress;
                _stageStart = stageStart;
                _stageEnd = stageEnd;
                _expected = expected;
            }

            public void Report(string line)
            {
                if (string.IsNullOrEmpty(line))
                {
                    return;
                }
                // 文件行必然包含反斜杠路径（源文件行等）；视为"已处理一个文件"
                if (line.IndexOf('\\') >= 0)
                {
                    _processed++;
                    if (_expected > 0)
                    {
                        double pct = _stageStart + (_stageEnd - _stageStart) * (double)_processed / _expected;
                        if (pct - _lastReported >= 0.01 || _processed >= _expected)
                        {
                            _lastReported = pct;
                            if (_progress != null)
                            {
                                _progress.Report(pct);
                            }
                        }
                    }
                    return;
                }
                if (_log != null)
                {
                    _log.Report(line);
                }
            }
        }

        /// <summary>预估源文件数（复制阶段真实进度的分母）。失败返回 0。</summary>
        private async Task<long> CountSourceFilesAsync(string source, CancellationToken ct)
        {
            try
            {
                return await Task.Run(() =>
                {
                    long n = 0;
                    foreach (var f in System.IO.Directory.EnumerateFiles(source, "*",
                        System.IO.SearchOption.AllDirectories))
                    {
                        n++;
                    }
                    return n;
                }, ct);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        private static void TryRollbackDest(string dest, IProgress<string> log)
        {
            try
            {
                if (Directory.Exists(dest))
                {
                    Log(log, "回滚：删除不完整的目标副本 " + dest);
                    var failed = new List<string>();
                    FileDeleter.DeleteTree(dest, log, CancellationToken.None, failed);
                    if (failed.Count > 0)
                    {
                        Log(log, "目标副本部分删除失败，请手动清理（或重试迁移）：" + failed[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(log, "回滚目标副本失败，请手动清理：" + dest + "（" + ex.Message + "）");
            }
        }

        private void SaveRecordSafely(MigrationRecord record, IProgress<string> log)
        {
            try
            {
                _store.Upsert(record);
            }
            catch (Exception ex)
            {
                Log(log, "警告：写入迁移记录失败（不影响已完成的迁移）：" + ex.Message);
            }
        }

        private static void SetProgress(Action<double> progress, double value)
        {
            if (progress != null)
            {
                try
                {
                    progress(value);
                }
                catch (Exception)
                {
                }
            }
        }

        private static void Log(IProgress<string> log, string line)
        {
            if (log != null)
            {
                log.Report(line);
            }
        }
    }
}