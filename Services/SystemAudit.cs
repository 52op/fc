using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace FC.Services
{
    /// <summary>
    /// 系统级空间审计（A2 休眠/页文件、C8 组件存储 WinSxS、C7 卷影提示的数据来源）。
    /// 只做读取与命令复制/打开设置，不直接修改系统配置。
    /// </summary>
    public static class SystemAudit
    {
        private static readonly string Windir =
            Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static readonly string SystemDrive = Path.GetPathRoot(Windir);

        /// <summary>系统盘根目录中的特殊大文件（pagefile/hiberfil/swapfile）大小。不存在的跳过。</summary>
        public static List<SystemFileInfo> ListSystemFiles()
        {
            var result = new List<SystemFileInfo>();
            var names = new[] { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp" };
            foreach (var n in names)
            {
                string p = Path.Combine(SystemDrive, n);
                try
                {
                    var fi = new FileInfo(p);
                    if (fi.Exists)
                    {
                        result.Add(new SystemFileInfo
                        {
                            Name = n,
                            Path = p,
                            Bytes = fi.Length
                        });
                    }
                }
                catch (Exception)
                {
                }
            }
            return result;
        }

        /// <summary>组件存储目录 WinSxS 大小（可能数百 MB~数 GB；受权限影响读取不全属正常）。</summary>
        public static long GetWinSxSSize()
        {
            try
            {
                string sx = Path.Combine(Windir, "WinSxS");
                return Directory.Exists(sx) ? SizeDirectory(sx) : 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>打开"虚拟内存"设置（系统属性 → 高级 → 性能 → 高级 → 虚拟内存）。
        /// 用 SystemPropertiesPerformance.exe 直接落到性能选项对话框，比 rundll32 参数更稳。</summary>
        public static void OpenVirtualMemorySettings()
        {
            TryStart("SystemPropertiesPerformance.exe", null, true);
        }

        /// <summary>打开磁盘清理（cleanmgr，可选盘符参数）。</summary>
        public static void OpenDiskCleanup(string drive = null)
        {
            TryStart("cleanmgr.exe", string.IsNullOrEmpty(drive) ? null : "/d " + drive.TrimEnd(':', '\\'), true);
        }

        /// <summary>DISM 组件（WinSxS）清理命令文本（需管理员，在提升后的 CMD 中执行）。</summary>
        public static string DismCommandText()
        {
            return "dism /Online /Cleanup-Image /StartComponentCleanup";
        }

        /// <summary>关闭休眠命令文本（需管理员；会同时删除 hiberfil.sys，释放与其等大的空间）。</summary>
        public static string PowercfgOffHibernateCommand()
        {
            return "powercfg /hibernate off";
        }

        /// <summary>
        /// 以管理员身份执行命令（触发 UAC）。提权后的 cmd 子进程把输出与退出码重定向到临时文件，
        /// 本方法轮询等待至完成（最长 timeoutMs，默认 20 分钟，覆盖 DISM 场景）。
        /// 用户取消 UAC 时不会产生输出文件 → Cancelled=true。
        /// </summary>
        public static CommandResult RunCommandElevated(string file, string args, long timeoutMs = 20 * 60 * 1000)
        {
            var result = new CommandResult();
            string outFile = null;
            try
            {
                outFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "fc_cmd_" + Guid.NewGuid().ToString("N") + ".txt");
                string cmd = string.Format(
                    "/c (\"{0}\" {1}) > \"{2}\" 2>&1 & echo EXIT_CODE:%errorlevel% >> \"{2}\"",
                    file, args, outFile);
                var psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = cmd,
                    Verb = "runas",
                    UseShellExecute = true,
                    CreateNoWindow = true
                };
                using (var p = Process.Start(psi))
                {
                    // shell 模式 Start 立即返回；UAC 子进程交由系统
                }

                if (!WaitForExitLine(outFile, timeoutMs))
                {
                    result.Cancelled = true;
                    return result;
                }

                string text = File.ReadAllText(outFile);
                int p0 = text.LastIndexOf("EXIT_CODE:", StringComparison.OrdinalIgnoreCase);
                if (p0 >= 0)
                {
                    string tail = text.Substring(p0 + 10).Trim();
                    var parts = tail.Split(' ');
                    string code = parts.Length > 0 ? parts[0].Trim() : "";
                    result.Success = code == "0";
                    result.Output = text.Substring(0, p0).Trim();
                }
                else
                {
                    result.Output = text.Trim();
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Output = result.Output.Length > 0 ? result.Output : ex.Message;
            }
            finally
            {
                if (outFile != null)
                {
                    try { File.Delete(outFile); } catch (Exception) { }
                }
            }
            return result;
        }

        private static bool WaitForExitLine(string file, long timeoutMs)
        {
            int start = Environment.TickCount;
            long limit = Math.Max(0, timeoutMs);
            while (Environment.TickCount - start < limit)
            {
                try
                {
                    if (File.Exists(file) && File.ReadAllText(file).IndexOf("EXIT_CODE:", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }
                catch (Exception)
                {
                }
                try
                {
                    Thread.Sleep(1000);
                }
                catch (Exception)
                {
                }
            }
            return false;
        }

        private static void TryStart(string file, string args, bool useShell)
        {
            try
            {
                var psi = new ProcessStartInfo(file, args)
                {
                    UseShellExecute = useShell
                };
                Process.Start(psi);
            }
            catch (Exception)
            {
            }
        }

        /// <summary>递归统计目录大小（不跟随重解析点，30s 上限）。</summary>
        private static long SizeDirectory(string dir)
        {
            long total = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0 && sw.ElapsedMilliseconds < 30000)
            {
                string cur = stack.Pop();
                try
                {
                    var dirs = new List<FastDirectory.DirInfoData>();
                    var files = new List<FastDirectory.FileInfoData>();
                    FastDirectory.List(cur, dirs, files);
                    foreach (var f in files)
                    {
                        total += f.Length;
                    }
                    foreach (var d in dirs)
                    {
                        if (!d.IsReparse)
                        {
                            stack.Push(d.FullPath);
                        }
                    }
                }
                catch (Exception)
                {
                }
            }
            return total;
        }
    }

    /// <summary>系统盘上某个特殊大文件的审计结果。</summary>
    public class SystemFileInfo
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public long Bytes { get; set; }
    }

    /// <summary>提权命令执行结果。</summary>
    public class CommandResult
    {
        public bool Success { get; set; }
        public bool Cancelled { get; set; }
        public string Output { get; set; }

        public CommandResult()
        {
            Output = "";
        }
    }
}