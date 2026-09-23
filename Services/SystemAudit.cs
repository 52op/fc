using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

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

        /// <summary>打开"虚拟内存"设置（系统属性 → 高级 → 性能设置）。</summary>
        public static void OpenVirtualMemorySettings()
        {
            TryStart("rundll32.exe", "sysdm.cpl, /Advanced", true);
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
}