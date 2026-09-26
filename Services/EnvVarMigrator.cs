using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FC.Converters;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 环境变量方式的数据目录迁移器。
    /// 与 junction 迁移（CopyMigrator）的区别：
    /// - 迁移目标 = 用户选的新目录（不再拼出子目录），数据整体搬过去；
    /// - 迁移成功后写入环境变量指到新位置，源位置可选择建 junction 兜底（某些软件仍读旧路径）或彻底删除；
    /// - 源目录删除若失败（占用），不回滚，生成"重启后删除脚本"并提示用户。
    /// 复用：LockFinder（占用检测/杀进程）、CliRunner+robocopy（复制）、IVerifier（校验）、FileDeleter（删除）。
    /// </summary>
    public class EnvVarMigrator
    {
        private readonly IVerifier _verifier;
        private readonly IRecordStore _store;
        private readonly int _threads;
        private readonly int _retries;
        private readonly int _retryWaitSec;

        public EnvVarMigrator(IVerifier verifier, IRecordStore store)
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
                int v;
                return int.TryParse(System.Configuration.ConfigurationManager.AppSettings[key], out v) ? v : defaultValue;
            }
            catch (Exception)
            {
                return defaultValue;
            }
        }

        /// <summary>
        /// 迁移前校验。返回 null 表示可以继续，否则返回错误描述。
        /// </summary>
        public string Validate(string source, string dest)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(dest))
            {
                return "源或目标路径为空。";
            }
            if (!System.IO.Directory.Exists(source))
            {
                return "源目录不存在：" + source;
            }
            if (JunctionUtil.IsReparsePoint(source))
            {
                return "源目录已是联接（junction），不支持环境变量迁移。";
            }
            if (string.Equals(PathUtil.TrimSlash(System.IO.Path.GetFullPath(source)),
                PathUtil.TrimSlash(System.IO.Path.GetFullPath(dest)), StringComparison.OrdinalIgnoreCase))
            {
                return "目标不能和源目录相同。";
            }
            if (CopyMigrator.IsSubPath(dest, source))
            {
                return "目标不能位于源目录内部，否则会无限递归。";
            }
            if (CopyMigrator.IsSubPath(source, dest))
            {
                return "源目录不能位于目标内部。";
            }
            if (System.IO.Directory.Exists(dest))
            {
                bool empty = !System.IO.Directory.EnumerateFileSystemEntries(dest).Any();
                if (!empty)
                {
                    return "目标目录已存在且不为空：" + dest
                        + "\n请选择空目录或不存在的目录。";
                }
            }
            return null;
        }

        /// <summary>
        /// 环境变量迁移主流程。返回成功后：
        /// - result.Success=true：数据已搬到 dest，环境变量已写入，源目录已删除（或已生成重启删除脚本，见 result.Message）。
        /// </summary>
        public async Task<MigrationResult> MigrateAsync(
            EnvVarRule rule, string source, string dest,
            EnvVarManager.Scope scope, bool createJunctionFallback,
            IProgress<string> log, CancellationToken ct, Action<double> progress = null)
        {
            var result = new MigrationResult();
            try
            {
                ct.ThrowIfCancellationRequested();
                SetProgress(progress, -1);

                string error = Validate(source, dest);
                if (error != null)
                {
                    throw new MigrationException(error);
                }
                if (string.IsNullOrEmpty(rule?.EnvVarName))
                {
                    throw new MigrationException("该目录的规则没有环境变量定义（" + (rule?.SoftwareName ?? "?") + "）。");
                }

                // ---- 步骤 0：杀规则标记的进程 + 占用检测 ----
                Log(log, "【步骤 1/6】检查并结束占用进程…");
                bool killed = await TryKillProcessesAsync(rule, source, log, ct);
                if (!killed)
                {
                    result.Message = "存在占用进程但未全部结束，已取消。";
                    return result;
                }

                // ---- 步骤 1：robocopy 复制（若源非空）----
                bool sourceHasData;
                try
                {
                    sourceHasData = System.IO.Directory.EnumerateFileSystemEntries(source).Any();
                }
                catch (Exception)
                {
                    sourceHasData = true;
                }

                if (sourceHasData)
                {
                    Log(log, "【步骤 2/6】robocopy 复制：" + source + "  ->  " + dest);
                    long expectedFiles = await Task.Run(() => CountSourceFiles(source), ct);
                    if (expectedFiles > 0)
                    {
                        Log(log, "预计 " + expectedFiles + " 个文件，复制阶段按实际文件数显示进度。");
                    }
                    int code = await RunRobocopyAsync(source, dest, log, ct, 0, 40, expectedFiles, progress);
                    result.RobocopyExitCode = code;
                    if (code >= 8)
                    {
                        if (TryGetLockers(log, new[] { source, dest }, result) && result.Lockers.Count > 0)
                        {
                            result.Message = "复制失败（退出码 " + code + "），检测到占用进程："
                                + Environment.NewLine + LockFinder.Describe(result.Lockers)
                                + Environment.NewLine + "结束这些进程后可重试。";
                            return result;
                        }
                        TryRollbackDest(dest, log);
                        throw new MigrationException("robocopy 复制失败（退出码 " + code + "）。已清理目标副本，源目录未改动。");
                    }
                }
                SetProgress(progress, 40);

                // ---- 步骤 2：校验 ----
                if (sourceHasData)
                {
                    Log(log, "【步骤 3/6】校验文件树一致…");
                    var report = await _verifier.VerifyAsync(source, dest, ct);
                    if (!report.Ok)
                    {
                        Log(log, "校验不一致：" + string.Join("；", report.Differences.Take(10).ToArray()));
                        TryRollbackDest(dest, log);
                        throw new MigrationException("迁移校验失败（文件缺失或大小不一致）。已清理目标副本，源目录未改动。");
                    }
                    result.Files = report.SourceFiles;
                    result.Bytes = report.SourceBytes;
                    Log(log, "校验通过：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes));
                }
                else
                {
                    result.Files = 0;
                    result.Bytes = 0;
                    Log(log, "源目录为空，跳过复制与校验。");
                }
                SetProgress(progress, 55);

                // ---- 步骤 3：写入环境变量 ----
                Log(log, "【步骤 4/6】写入环境变量 " + rule.EnvVarName + "（" +
                    (scope == EnvVarManager.Scope.Machine ? "系统级" : "用户级") + "）=" + dest);
                string envValue = dest;
                string existingValue = EnvVarManager.Get(rule.EnvVarName, scope);
                if (!string.IsNullOrEmpty(existingValue))
                {
                    Log(log, "原值（将备份）：" + existingValue);
                }

                if (rule.ValueMode == EnvValueMode.OptsFlag)
                {
                    // Maven 类：变量值是 JVM 参数段
                    string flag = "-Dmaven.repo.local=" + "\"" + dest + "\"";
                    if (!string.IsNullOrEmpty(existingValue)
                        && existingValue.IndexOf("maven.repo.local", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        // 保留用户原有 MAVEN_OPTS 但替换本地仓库参数（简单策略：追加一条）
                        envValue = existingValue + " " + flag;
                    }
                    else if (!string.IsNullOrEmpty(existingValue))
                    {
                        envValue = existingValue + " " + flag;
                    }
                    else
                    {
                        envValue = flag;
                    }
                }

                if (!EnvVarManager.Set(rule.EnvVarName, envValue, scope, out string setErr))
                {
                    if (EnvVarManager.NeedsAdmin(scope))
                    {
                        Log(log, "写入系统级环境变量失败（可能需要管理员权限）：" + setErr);
                        TryRollbackDest(dest, log);
                        throw new MigrationException("写入环境变量 " + rule.EnvVarName + " 失败（" + setErr + "）。\n"
                            + "系统级环境变量需要管理员权限：请以管理员身份运行 FC 后重试，或改用“用户级”作用域。");
                    }
                    TryRollbackDest(dest, log);
                    throw new MigrationException("写入环境变量 " + rule.EnvVarName + " 失败（" + setErr + "）。已清理目标副本。");
                }
                Log(log, "环境变量已写入。");
                SetProgress(progress, 70);

                // ---- 步骤 4：删除源目录（失败不回滚，生成重启删除脚本）----
                Log(log, "【步骤 5/6】删除源目录：" + source);
                bool sourceDeleted = await TryDeleteSourceAsync(rule, source, log, ct);
                if (!sourceDeleted)
                {
                    Log(log, "源目录删除未完成（可能被占用）。生成“重启后自动删除”脚本…");
                    string scriptPath = WriteRestartDeleteScript(rule, source, dest, log);
                    if (scriptPath != null)
                    {
                        result.Message = "环境变量已设置、数据已迁移到 " + dest
                            + "。" + Environment.NewLine
                            + "源目录仍有残留文件无法删除（可能被进程占用）。" + Environment.NewLine
                            + "已生成重启后自动删除脚本：" + Environment.NewLine + scriptPath
                            + Environment.NewLine + "下次重启时脚本会自动清理残留。";
                    }
                    else
                    {
                        result.Message = "环境变量已设置、数据已迁移到 " + dest + "。" + Environment.NewLine
                            + "但源目录删除失败，且无法生成删除脚本，请手动清理：" + source;
                    }
                    result.Success = true;
                    SetProgress(progress, 100);
                    return result;
                }
                SetProgress(progress, 90);

                // ---- 步骤 5（可选兜底）：建 junction，旧路径继续可用 ----
                if (createJunctionFallback && !System.IO.Directory.Exists(source))
                {
                    Log(log, "【步骤 6/6】可选兜底：在源位置创建 junction 指向新目录…");
                    if (JunctionUtil.Create(source, dest, out string jerr))
                    {
                        Log(log, "已在源位置创建 junction（软件若仍读旧路径也能正常工作）。");
                    }
                    else
                    {
                        Log(log, "创建 junction 失败（不影响环境变量迁移结果）：" + jerr);
                    }
                }

                // ---- 步骤 6：写记录 ----
                SaveRecord(rule, source, dest, scope, existingValue, envValue, createJunctionFallback,
                    result.Bytes, result.Files, status: (createJunctionFallback && System.IO.Directory.Exists(source) && !JunctionUtil.IsReparsePoint(source)) ? MigrationStatus.Error : MigrationStatus.Active, log);

                result.Success = true;
                result.Message = "迁移完成：" + result.Files + " 个文件 / " + SizeText.Format(result.Bytes)
                    + "。" + Environment.NewLine
                    + "环境变量 " + rule.EnvVarName + " 已指向：" + dest + Environment.NewLine
                    + (createJunctionFallback ? "源位置已创建 junction 兜底链接。" : "源目录已删除。");
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

        /// <summary>
        /// 还原 env 迁移：恢复环境变量旧值（或删除），把数据搬回源位置，清理目标，删除 junction。
        /// 返回 result.Success=true 表示还原完成。
        /// </summary>
        public async Task<MigrationResult> RestoreAsync(
            MigrationRecord record, string envVarName,
            EnvVarManager.Scope scope, string oldValue,
            IProgress<string> log, CancellationToken ct)
        {
            var result = new MigrationResult();
            try
            {
                ct.ThrowIfCancellationRequested();
                string source = record.SourcePath;
                string dest = record.DestPath;

                Log(log, "【步骤 1/5】检查占用进程…");
                if (!await TryKillProcessesAsyncForRestore(source, dest, log, ct))
                {
                    result.Message = "存在占用进程但未全部结束，已取消。";
                    return result;
                }

                // 1) 还原环境变量
                Log(log, "【步骤 2/5】还原环境变量 " + envVarName + " …");
                bool envOk;
                if (!string.IsNullOrEmpty(oldValue))
                {
                    envOk = EnvVarManager.Set(envVarName, oldValue, scope, out string err);
                    if (!envOk) Log(log, "还原值失败：" + (err ?? ""));
                }
                else
                {
                    envOk = EnvVarManager.Remove(envVarName, scope, out string err);
                    if (!envOk) Log(log, "删除变量失败：" + (err ?? ""));
                }
                if (!envOk)
                {
                    result.Message = "环境变量还原失败。数据未移动。";
                    return result;
                }
                Log(log, "环境变量已还原。");

                // 2) 删掉源位置的 junction（若存在）
                if (JunctionUtil.IsReparsePoint(source))
                {
                    Log(log, "【步骤 3/5】删除源位置 junction…");
                    if (!JunctionUtil.Delete(source, out string jerr))
                    {
                        result.Message = "删除源位置 junction 失败：" + jerr;
                        return result;
                    }
                }
                else if (System.IO.Directory.Exists(source))
                {
                    Log(log, "源位置存在普通目录/残留，先清理：");
                    var failed = new List<string>();
                    FileDeleter.DeleteTree(source, log, ct, failed);
                    if (failed.Count > 0)
                    {
                        result.Message = "清理源位置残留失败：" + failed.First();
                        return result;
                    }
                }

                // 3) 数据搬回
                Log(log, "【步骤 4/5】复制数据回源位置…");
                if (System.IO.Directory.Exists(dest))
                {
                    long expectedBack = await Task.Run(() => CountSourceFiles(dest), ct);
                    int code = await RunRobocopyAsync(dest, source, log, ct, 0, 80, expectedBack, null);
                    if (code >= 8)
                    {
                        if (TryGetLockers(log, new[] { source, dest }, result) && result.Lockers.Count > 0)
                        {
                            result.Message = "复制回源失败（退出码 " + code + "），检测到占用：" + Environment.NewLine
                                + LockFinder.Describe(result.Lockers);
                            return result;
                        }
                        throw new MigrationException("还原复制失败（退出码 " + code + "）。");
                    }
                    Log(log, "校验…");
                    var report = await _verifier.VerifyAsync(dest, source, ct);
                    if (!report.Ok)
                    {
                        throw new MigrationException("还原校验失败（文件缺失或大小不一致）。");
                    }
                    result.Files = report.SourceFiles;
                    result.Bytes = report.SourceBytes;
                }

                // 4) 清理目标副本 + 记录标记已还原
                Log(log, "【步骤 5/5】清理目标副本：" + dest);
                var failed2 = new List<string>();
                FileDeleter.DeleteTree(dest, log, ct, failed2);
                if (failed2.Count > 0)
                {
                    Log(log, "目标副本部分删除失败，请手动清理：" + failed2[0]);
                }

                record.Status = MigrationStatus.Restored;
                record.RestoredAt = DateTime.Now;
                try { _store.Upsert(record); } catch (Exception ex) { Log(log, "写记录失败：" + ex.Message); }

                result.Success = true;
                result.Message = "还原完成：" + result.Files + " 个文件已回到 " + source;
                SetProgress(null, 100);
                return result;
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
                result.Message = "还原已取消。请手动检查。";
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

        // ==================== 内部 ====================

        /// <summary>杀规则指定的相关进程 + 实际占用源目录的进程。返回是否全部成功。</summary>
        private async Task<bool> TryKillProcessesAsync(EnvVarRule rule, string source, IProgress<string> log, CancellationToken ct)
        {
            var known = new List<LockerInfo>();

            // 规则里的进程提示
            foreach (var name in rule.GetKillHints())
            {
                try
                {
                    var procs = System.Diagnostics.Process.GetProcessesByName(name);
                    foreach (var p in procs)
                    {
                        if (p.Id == System.Diagnostics.Process.GetCurrentProcess().Id)
                        {
                            continue;
                        }
                        known.Add(new LockerInfo { ProcessId = p.Id, ProcessName = SafeName(p) });
                    }
                    foreach (var p in procs) { try { p.Dispose(); } catch (Exception) { } }
                }
                catch (Exception)
                {
                }
            }

            // 实际占用源目录的进程
            try
            {
                var live = LockFinder.Find(source);
                if (live != null)
                {
                    foreach (var l in live)
                    {
                        if (!known.Any(k => k.ProcessId == l.ProcessId))
                        {
                            known.Add(l);
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            if (known.Count == 0)
            {
                Log(log, "无需结束占用进程。");
                return true;
            }

            Log(log, "检测到需结束的进程：" + LockFinder.Describe(known));
            var errors = await Task.Run(() => LockFinder.Kill(known), ct);
            if (errors.Count > 0)
            {
                Log(log, "部分进程结束失败：" + string.Join("；", errors));
                return false;
            }
            Log(log, "相关进程已结束。");
            // 给进程退出留一点时间
            try { await Task.Delay(600, ct); } catch (OperationCanceledException) { throw; }
            return true;
        }

        private async Task<bool> TryKillProcessesAsyncForRestore(string source, string dest, IProgress<string> log, CancellationToken ct)
        {
            var known = new List<LockerInfo>();
            var live = LockFinder.Find(source) ?? new List<LockerInfo>();
            var live2 = LockFinder.Find(dest) ?? new List<LockerInfo>();
            foreach (var l in live.Concat(live2))
            {
                if (!known.Any(k => k.ProcessId == l.ProcessId))
                {
                    known.Add(l);
                }
            }
            if (known.Count == 0)
            {
                Log(log, "无需结束占用进程。");
                return true;
            }
            Log(log, "检测到占用进程：" + LockFinder.Describe(known));
            var errors = await Task.Run(() => LockFinder.Kill(known), ct);
            if (errors.Count > 0)
            {
                Log(log, "部分进程结束失败：" + string.Join("；", errors));
                return false;
            }
            try { await Task.Delay(600, ct); } catch (OperationCanceledException) { throw; }
            return true;
        }

        /// <summary>删除源目录。返回是否完全删除。失败时不抛异常，由调用方决定生成重启删除脚本。</summary>
        private async Task<bool> TryDeleteSourceAsync(EnvVarRule rule, string source, IProgress<string> log, CancellationToken ct)
        {
            if (!System.IO.Directory.Exists(source) && !System.IO.File.Exists(source))
            {
                return true;
            }
            // junction 不能删（不该出现），普通目录删
            if (JunctionUtil.IsReparsePoint(source))
            {
                // 此前是 junction（不该有），跳过
                Log(log, "源位置是 junction，跳过删除。");
                return true;
            }
            var failed = new List<string>();
            try
            {
                await Task.Run(() => FileDeleter.DeleteTree(source, log, ct, failed), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            if (failed.Count > 0)
            {
                Log(log, "以下内容无法删除（可能被占用）：");
                foreach (var f in failed.Distinct().Take(20))
                {
                    Log(log, "  " + f);
                }
                return false;
            }
            Log(log, "源目录已删除。");
            return true;
        }

        /// <summary>生成"重启后自动删除"脚本（RunOnce 注册表 + bat）。返回脚本路径，失败返回 null。</summary>
        private string WriteRestartDeleteScript(EnvVarRule rule, string source, string dest, IProgress<string> log)
        {
            try
            {
                if (rule.DisableRestartDeleteScript)
                {
                    return null;
                }
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FC", "pending-delete");
                System.IO.Directory.CreateDirectory(dir);
                string bat = System.IO.Path.Combine(dir, "delete_" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".cmd");
                string script = "@echo off\r\n"
                    + "rem FC 自动删除残留目录（程序已迁移数据，此目录为残留）\r\n"
                    + "rem 源: " + source + "\r\n"
                    + "rem 目标: " + dest + "\r\n"
                    + "if not exist \"" + source + "\" exit /b 0\r\n"
                    + "taskkill /F /IM explorer.exe >nul 2>&1\r\n"
                    + "rmdir /s /q \"" + source + "\" >nul 2>&1\r\n"
                    + "if exist \"" + source + "\" (start explorer.exe & rmdir /s /q \"" + source + "\") >nul 2>&1\r\n"
                    + "start explorer.exe\r\n"
                    + "reg delete \"HKCU\\Software\\Microsoft\\Windows\\CurrentVersion\\RunOnce\" /v FC_pending_delete_" + DateTime.Now.ToString("HHmmss") + " /f >nul 2>&1\r\n"
                    + "del \"%~f0\"\r\n";
                System.IO.File.WriteAllText(bat, script, System.Text.Encoding.Default);

                // RunOnce 注册表项：登录时执行 bat（避免卡用户）
                try
                {
                    using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\RunOnce"))
                    {
                        if (key != null)
                        {
                            key.SetValue("FC_pending_delete_1", "\"" + bat + "\"");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log(log, "Register RunOnce 失败（脚本已生成但不会自动运行）：" + ex.Message);
                }
                return bat;
            }
            catch (Exception ex)
            {
                Log(log, "生成重启删除脚本失败：" + ex.Message);
                return null;
            }
        }

        private void SaveRecord(EnvVarRule rule, string source, string dest,
            EnvVarManager.Scope scope, string oldValue, string newValue, bool junctionFallback,
            long bytes, long files, MigrationStatus status, IProgress<string> log)
        {
            try
            {
                var record = new MigrationRecord
                {
                    SourcePath = source,
                    DestPath = dest,
                    MigratedAt = DateTime.Now,
                    TotalBytes = bytes,
                    FileCount = files,
                    Status = status,
                    MigrationKind = MigrationKind.EnvVar,
                    EnvVarName = rule.EnvVarName,
                    EnvVarScope = scope == EnvVarManager.Scope.Machine ? "Machine" : "User",
                    EnvVarOldValue = oldValue,
                    EnvVarNewValue = newValue,
                    JunctionFallback = junctionFallback,
                    SoftwareName = rule.SoftwareName
                };
                _store.Upsert(record);
            }
            catch (Exception ex)
            {
                Log(log, "警告：写入迁移记录失败（不影响迁移结果）：" + ex.Message);
            }
        }

        private async Task<int> RunRobocopyAsync(
            string src, string dst, IProgress<string> log, CancellationToken ct,
            double stageStart = 0.0, double stageEnd = 30.0,
            long expectedFiles = 0, Action<double> progress = null)
        {
            string args = string.Format(
                "\"{0}\" \"{1}\" /E /COPY:DAT /DCOPY:DAT /R:{2} /W:{3} /MT:{4} /XJ /NP",
                src, dst, _retries, _retryWaitSec, _threads);
            var runner = new CliRunner();
            var res = await runner.RunAsync("robocopy.exe", args,
                new EnvCopySink(log, progress, stageStart, stageEnd, expectedFiles), ct);
            if (res.Cancelled)
            {
                throw new OperationCanceledException(ct);
            }
            return res.ExitCode;
        }

        /// <summary>预估源文件数（复制阶段真实进度的分母）。失败返回 0。</summary>
        private static long CountSourceFiles(string dir)
        {
            try
            {
                long n = 0;
                foreach (var f in System.IO.Directory.EnumerateFiles(dir, "*", System.IO.SearchOption.AllDirectories))
                {
                    n++;
                }
                return n;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// robocopy 输出行过滤器：含路径的文件行 → 不写日志，仅计数并按文件数推进阶段进度（stageStart→stageEnd）；
        /// 其余行（摘要/错误等）照常转发到日志框。避免十几万文件行刷爆日志导致 UI 卡死、内存暴涨。
        /// </summary>
        private sealed class EnvCopySink : IProgress<string>
        {
            private readonly IProgress<string> _log;
            private readonly Action<double> _progress;
            private readonly double _stageStart;
            private readonly double _stageEnd;
            private readonly long _expected;
            private long _processed;
            private double _lastReported = -1;

            public EnvCopySink(IProgress<string> log, Action<double> progress,
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
                // 文件行必然含反斜杠路径 → 视为"已处理一个文件"，只计数，不写日志
                if (line.IndexOf('\\') >= 0)
                {
                    _processed++;
                    if (_expected > 0 && _progress != null)
                    {
                        double pct = _stageStart + (_stageEnd - _stageStart) * (double)_processed / _expected;
                        if (pct - _lastReported >= 0.005 || _processed >= _expected)
                        {
                            _lastReported = pct;
                            try { _progress(pct); } catch (Exception) { }
                        }
                    }
                    return;
                }
                // 摘要/错误行才写日志
                if (_log != null)
                {
                    try { _log.Report(line); } catch (Exception) { }
                }
            }
        }

        private bool TryGetLockers(IProgress<string> log, string[] paths, MigrationResult result)
        {
            try
            {
                var fs = new List<LockerInfo>();
                foreach (var p in paths)
                {
                    var ls = LockFinder.Find(p);
                    if (ls != null)
                    {
                        fs.AddRange(ls);
                    }
                }
                var distinct = fs.GroupBy(x => x.ProcessId).Select(g => g.First()).ToList();
                result.Lockers = distinct;
                return distinct.Count > 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static void TryRollbackDest(string dest, IProgress<string> log)
        {
            try
            {
                if (System.IO.Directory.Exists(dest))
                {
                    Log(log, "回滚：删除不完整的目标副本 " + dest);
                    var failed = new List<string>();
                    FileDeleter.DeleteTree(dest, log, CancellationToken.None, failed);
                    if (failed.Count > 0)
                    {
                        Log(log, "目标副本部分删除失败，请手动清理：" + failed[0]);
                    }
                }
            }
            catch (Exception ex)
            {
                Log(log, "回滚目标副本失败:" + ex.Message);
            }
        }

        private static string SafeName(System.Diagnostics.Process p)
        {
            try { return p.ProcessName; } catch (Exception) { return "?"; }
        }

        private static void Log(IProgress<string> log, string line)
        {
            try
            {
                if (log != null)
                {
                    log.Report(line);
                }
            }
            catch (Exception)
            {
            }
        }

        private static void SetProgress(Action<double> p, double v)
        {
            try
            {
                if (p != null)
                {
                    p(v);
                }
            }
            catch (Exception)
            {
            }
        }
    }
}