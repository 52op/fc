using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace FC.Services
{
    /// <summary>
    /// 递归删除目录树。对"目录被进程占用"有专项战术：
    /// - 快路径 Directory.Delete(true)（单次系统调用，绝大多数瞬时完成）；
    /// - 失败 → 慢路径（FindFirstFileEx 条目级枚举 + icacls /reset + 逐条删 + 只删重解析点链接）；
    /// - 仍失败 → 重命名战术：把目录改名到旁路（rename 只需父级权限，可绕开"目录自身被锁"的删除路径），再删改名后的目录；
    /// - 每轮间隔 3s 自动重试（最多 5 轮），抗占用者短暂/瞬时锁定；
    /// - 60 秒慢路径上限；失败原因与剩余路径写进日志，并收集到 failedPaths（供锁定检测与回退）。
    /// </summary>
    public static class FileDeleter
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetFileInformationByHandle(
            IntPtr hFile, int FileInformationClass,
            ref FILE_DISPOSITION_INFO lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        private struct FILE_DISPOSITION_INFO
        {
            public byte DeleteFile;
        }

        private const uint DELETE = 0x00010000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint FILE_SHARE_DELETE = 0x4;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_ATTRIBUTE_NORMAL = 0x80;
        private const int FileDispositionInfo = 1;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        private const long SlowTimeoutMs = 60000;
        private const long ProgressEvery = 300;
        private const int MaxDeleteAttempts = 5;
        private const int RetryDelayMs = 3000;

        /// <summary>删除目录树。失败条目追加到 failedPaths。取消时抛 OperationCanceledException。</summary>
        public static void DeleteTree(string root, IProgress<string> log, CancellationToken ct, List<string> failedPaths)
        {
            if (root == null || failedPaths == null)
            {
                return;
            }
            root = root.TrimEnd('\\');
            string moved = null;

            for (int attempt = 0; attempt < MaxDeleteAttempts; attempt++)
            {
                ct.ThrowIfCancellationRequested();

                if (attempt > 0)
                {
                    RunLog(log, "目录仍被占用，等待 3 秒后重试 " + attempt + "/" + (MaxDeleteAttempts - 1) + " …");
                    try
                    {
                        Task.Delay(RetryDelayMs, ct).GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }

                // 原地快路径
                if (Directory.Exists(root))
                {
                    TryDeleteInPlace(root, log, ct, failedPaths);
                }

                // 原地慢路径未能删净 → 重命名战术（绕开目录自身被锁的删除路径）
                if (Directory.Exists(root))
                {
                    string tmp = root + "__fc_del_" + Guid.NewGuid().ToString("N");
                    try
                    {
                        Directory.Move(root, tmp);
                        moved = tmp;
                        RunLog(log, "已把目录重命名为：" + tmp + "（继续清理原内容）");
                    }
                    catch (Exception ex)
                    {
                        RunLog(log, "重命名也未成功（占用者锁定目录本身）：" + ex.Message);
                    }
                }

                // 清理改名后的目录
                if (!string.IsNullOrEmpty(moved) && Directory.Exists(moved))
                {
                    TryDeleteInPlace(moved, log, ct, failedPaths);
                }

                // 原始路径与改名路径都清了 → 完成
                if (!Directory.Exists(root) &&
                    (string.IsNullOrEmpty(moved) || !Directory.Exists(moved)))
                {
                    return;
                }
            }

            // 最终仍有残留 → 收集
            foreach (var p in new[] { root, moved })
            {
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p) && !failedPaths.Contains(p))
                {
                    failedPaths.Add(p);
                }
            }
        }

        /// <summary>
        /// 清空目录的内容但保留目录本身（用于清理缓存/临时目录）。复用删除树全部战术：
        /// 先删直接子项（文件逐个、目录整棵），若有残留目录再整体走 DeleteTree（可能重命名绕锁）。
        /// </summary>
        public static void EmptyDirectoryContents(
            string dir, IProgress<string> log, CancellationToken ct, List<string> failedPaths)
        {
            if (string.IsNullOrEmpty(dir) || failedPaths == null)
            {
                return;
            }
            dir = dir.TrimEnd('\\');
            if (!Directory.Exists(dir))
            {
                return;
            }

            // 第一遍：清直接子项（保留根目录）。子目录一律"单遍尽力"——快删失败转慢删逐条，
            // 但绝不走 DeleteTree 的 5 轮重试（清理场景对 3GB 大目录死磕会卡到你怀疑人生）。
            RemoveDirectChildren(dir, log, ct, failedPaths);

            // 第二遍：仍有直接子项 → 多为进程占用刚释放或正占用。短暂等待后重试一轮，
            // 仍失败就交给 failedPaths 上报（占用者释放后可再点一次清理）。
            if (HasAnyChild(dir))
            {
                RunLog(log, "部分内容仍被占用，稍候重试一轮…");
                try
                {
                    Task.Delay(1500, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                RemoveDirectChildren(dir, log, ct, failedPaths);
            }
        }

        /// <summary>删除单文件（清只读/系统位后删除，复用失败收集）。</summary>
        public static void DeleteFile(string path, List<string> failedPaths)
        {
            if (string.IsNullOrEmpty(path) || failedPaths == null)
            {
                return;
            }
            if (Directory.Exists(path))
            {
                failedPaths.Add(path);
                return;
            }
            TryDeleteFile(path, failedPaths);
        }

        private static void RemoveDirectChildren(
            string dir, IProgress<string> log, CancellationToken ct, List<string> failedPaths)
        {
            var dirs = new List<FastDirectory.DirInfoData>();
            var files = new List<FastDirectory.FileInfoData>();
            try
            {
                FastDirectory.List(dir, dirs, files);
            }
            catch (Exception ex)
            {
                failedPaths.Add(dir);
                RunLog(log, "枚举失败：" + dir + "（" + ex.GetType().Name + "：" + ex.Message + "）");
                return;
            }

            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                TryDeleteFile(f.FullPath, failedPaths);
            }
            foreach (var d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                // junction 子项只删链接本身
                if (d.IsReparse)
                {
                    TryRemoveLink(d.FullPath, failedPaths);
                }
                else
                {
                    // 单遍尽力删除整棵子树：快删 → 慢删逐条（不重试、不重复 icacls）
                    TryDeleteDirTreeOnce(d.FullPath, log, ct, failedPaths);
                }
            }
        }

        /// <summary>
        /// 单遍尽力删除一棵子树（清理场景专用，无 5 轮重试）：
        /// 快路径 Directory.Delete(true) → 失败转慢路径（逐条删内容再删自身）。
        /// 被占用/无权限的残留项进 failedPaths 上报，不阻塞整体清理。
        /// </summary>
        private static void TryDeleteDirTreeOnce(
            string path, IProgress<string> log, CancellationToken ct, List<string> failedPaths)
        {
            try
            {
                Directory.Delete(path, true);
                return;
            }
            catch (Exception)
            {
            }
            try
            {
                string lp = "\\\\?\\" + path;
                if (Directory.Exists(lp))
                {
                    Directory.Delete(lp, true);
                    if (!Directory.Exists(path))
                    {
                        return;
                    }
                }
            }
            catch (Exception)
            {
            }

            // 慢路径：逐条删内容（FindFirstFileEx），本身带文件数进度日志与 60s/目录上限
            var sw = Stopwatch.StartNew();
            long deleted = 0;
            DeleteChildren(path, log, ct, failedPaths, sw, ref deleted);
            if (Directory.Exists(path))
            {
                TryDeleteDir(path, failedPaths, log);
            }
        }

        private static bool HasAnyChild(string dir)
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(dir).Any();
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>单次尽全力的删除尝试：快路径 → 慢路径。</summary>
        private static void TryDeleteInPlace(
            string target, IProgress<string> log, CancellationToken ct, List<string> failedPaths)
        {
            // 快路径
            try
            {
                Directory.Delete(target, true);
                return;
            }
            catch (Exception ex)
            {
                RunLog(log, "快路径删除失败（" + ex.GetType().Name + "）：" + ex.Message);
            }
            try
            {
                string lp = "\\\\?\\" + target;
                if (Directory.Exists(lp))
                {
                    Directory.Delete(lp, true);
                    if (!Directory.Exists(target))
                    {
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                RunLog(log, "快路径（长路径）删除失败：" + ex.Message);
            }

            // 慢路径：整树 icacls 一次 + FindFirstFileEx 逐条删
            RunLog(log, "整树重置 ACL（icacls /reset /T /C）…");
            RunIcacls(target, true);

            var sw = Stopwatch.StartNew();
            long deleted = 0;
            DeleteChildren(target, log, ct, failedPaths, sw, ref deleted);

            foreach (var f in failedPaths.Distinct())
            {
                RunLog(log, "仍无法删除：" + f);
            }
            RunLog(log, "删除阶段处理 " + deleted + " 项，耗时 " + (sw.ElapsedMilliseconds / 1000) + "s；剩余失败项见上。");

            if (Directory.Exists(target))
            {
                TryDeleteDir(target, failedPaths, log);
            }
        }

        private static void DeleteChildren(
            string dir, IProgress<string> log, CancellationToken ct,
            List<string> failedPaths, Stopwatch sw, ref long deleted)
        {
            var dirs = new List<FastDirectory.DirInfoData>();
            var files = new List<FastDirectory.FileInfoData>();
            try
            {
                // FindFirstFileEx：坏 junction 也按条目返回，不会让整棵树枚举失败
                FastDirectory.List(dir, dirs, files);
            }
            catch (Exception ex)
            {
                failedPaths.Add(dir);
                RunLog(log, "枚举失败：" + dir + "（" + ex.GetType().Name + "：" + ex.Message + "）");
                return;
            }

            foreach (var f in files)
            {
                if (TimeoutOrCancel(dir, log, ct, failedPaths, sw))
                {
                    return;
                }
                TryDeleteFile(f.FullPath, failedPaths);
                deleted++;
                if (deleted % ProgressEvery == 0)
                {
                    RunLog(log, "…已删除 " + deleted + " 项");
                }
            }

            foreach (var d in dirs)
            {
                if (TimeoutOrCancel(dir, log, ct, failedPaths, sw))
                {
                    return;
                }
                if (d.IsReparse)
                {
                    // 只删链接本身，绝不深入（坏联接也照删）
                    TryRemoveLink(d.FullPath, failedPaths);
                    continue;
                }
                DeleteChildren(d.FullPath, log, ct, failedPaths, sw, ref deleted);
                if (Directory.Exists(d.FullPath))
                {
                    TryDeleteDir(d.FullPath, failedPaths, log);
                }
            }
        }

        private static bool TimeoutOrCancel(
            string dir, IProgress<string> log, CancellationToken ct,
            List<string> failedPaths, Stopwatch sw)
        {
            if (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
            }
            if (sw.ElapsedMilliseconds > SlowTimeoutMs)
            {
                if (!failedPaths.Contains(dir))
                {
                    failedPaths.Add(dir);
                }
                RunLog(log, "删除超时（60s），剩余项交给锁定检测/回退处理。");
                return true;
            }
            return false;
        }

        /// <summary>删除重解析点链接本身（junction/symlink；不跟随目标）。</summary>
        private static void TryRemoveLink(string path, List<string> failedPaths)
        {
            try
            {
                if (Directory.Exists(path) || JunctionUtil.IsReparsePoint(path))
                {
                    Directory.Delete(path, false);
                    return;
                }
            }
            catch (Exception)
            {
            }
            try
            {
                string lp = "\\\\?\\" + path;
                if (Directory.Exists(lp))
                {
                    Directory.Delete(lp, false);
                    return;
                }
            }
            catch (Exception)
            {
            }
            if (Directory.Exists(path) || JunctionUtil.IsReparsePoint(path))
            {
                failedPaths.Add(path);
            }
        }

        private static void TryDeleteFile(string path, List<string> failedPaths)
        {
            if (TryDeleteFileNormal(path))
            {
                return;
            }
            if (TryDeleteFilePending(path))
            {
                return;
            }
            if (File.Exists(path))
            {
                failedPaths.Add(path);
            }
        }

        /// <summary>普通 + 长路径前缀删除。</summary>
        private static bool TryDeleteFileNormal(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.SetAttributes(path, FileAttributes.Normal);
                    File.Delete(path);
                    return !File.Exists(path);
                }
            }
            catch (Exception)
            {
            }
            try
            {
                string lp = "\\\\?\\" + path;
                if (File.Exists(lp))
                {
                    File.SetAttributes(lp, FileAttributes.Normal);
                    File.Delete(lp);
                    return !File.Exists(path);
                }
            }
            catch (Exception)
            {
            }
            return !File.Exists(path);
        }

        /// <summary>"标记删除"句柄方案：删除共享模式下被打开的文件也尝试清除。</summary>
        private static bool TryDeleteFilePending(string path)
        {
            if (!File.Exists(path))
            {
                return true;
            }
            try
            {
                string lp = "\\\\?\\" + path;
                IntPtr h = CreateFile(lp, DELETE,
                    FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                    IntPtr.Zero, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
                if (h == INVALID_HANDLE_VALUE || h == IntPtr.Zero)
                {
                    return !File.Exists(path);
                }
                try
                {
                    var info = new FILE_DISPOSITION_INFO { DeleteFile = 1 };
                    bool ok = SetFileInformationByHandle(h, FileDispositionInfo, ref info,
                        (uint)Marshal.SizeOf(typeof(FILE_DISPOSITION_INFO)));
                    return ok || !File.Exists(path);
                }
                finally
                {
                    CloseHandle(h);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool TryDeleteDir(string path, List<string> failedPaths, IProgress<string> log)
        {
            if (JunctionUtil.IsReparsePoint(path))
            {
                TryRemoveLink(path, failedPaths);
                return !Directory.Exists(path);
            }
            try
            {
                Directory.Delete(path, false);
                return !Directory.Exists(path);
            }
            catch (Exception)
            {
            }
            try
            {
                string lp = "\\\\?\\" + path;
                if (Directory.Exists(lp))
                {
                    Directory.Delete(lp, false);
                    return !Directory.Exists(path);
                }
            }
            catch (Exception)
            {
            }
            if (Directory.Exists(path))
            {
                failedPaths.Add(path);
            }
            return false;
        }

        private static void RunIcacls(string target, bool resetTree)
        {
            try
            {
                string args = "\"" + target + "\" /reset" + (resetTree ? " /T /C" : " /C");
                var psi = new ProcessStartInfo("icacls.exe", args)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var p = Process.Start(psi))
                {
                    p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd();
                    if (!p.WaitForExit(20000))
                    {
                        try
                        {
                            p.Kill();
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        private static void RunLog(IProgress<string> log, string line)
        {
            if (log != null)
            {
                log.Report(line);
            }
        }
    }
}