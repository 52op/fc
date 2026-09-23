using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace FC.Services
{
    /// <summary>
    /// junction（目录联接）的创建 / 删除 / 识别 / 目标解析。
    /// 关键点：mklink /J 创建 junction 不需要管理员权限，且允许跨盘。
    /// </summary>
    public static class JunctionUtil
    {
        // ---- P/Invoke：解析 reparse point 目标 ----
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(
            string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition,
            uint dwFlagsAndAttributes, IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeviceIoControl(
            IntPtr hDevice, uint dwIoControlCode,
            IntPtr lpInBuffer, uint nInBufferSize,
            IntPtr lpOutBuffer, uint nOutBufferSize,
            out uint lpBytesReturned, IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        private const uint GENERIC_READ = 0x80000000;
        private const uint FILE_SHARE_READ = 0x00000001;
        private const uint FILE_SHARE_WRITE = 0x00000002;
        private const uint FILE_SHARE_DELETE = 0x00000004;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
        private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
        private const uint FSCTL_GET_REPARSE_POINT = 0x000900A8;
        private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;
        private static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

        /// <summary>path 是否为重解析点（junction / symlink）</summary>
        public static bool IsReparsePoint(string path)
        {
            try
            {
                var di = new DirectoryInfo(path);
                if (!di.Exists)
                {
                    return false;
                }
                return (di.Attributes & FileAttributes.ReparsePoint) != 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>path 是否为目录联接（与 IsReparsePoint 等价，语义更明确）</summary>
        public static bool IsJunction(string path)
        {
            return IsReparsePoint(path);
        }

        /// <summary>
        /// 创建 junction：link -> target。调用 cmd 的 mklink /J。
        /// </summary>
        public static bool Create(string link, string target, out string error)
        {
            error = null;

            if (!Directory.Exists(target))
            {
                error = "目标路径不存在：" + target;
                return false;
            }
            if (Directory.Exists(link))
            {
                error = "链接路径已存在：" + link;
                return false;
            }

            string args = "/c mklink /J \"" + link + "\" \"" + target + "\"";
            using (var p = RunHidden("cmd.exe", args))
            {
                if (p == null)
                {
                    error = "无法启动 cmd.exe";
                    return false;
                }
                string outp = p.StandardOutput.ReadToEnd();
                string errs = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    error = (outp + errs).Trim();
                    return false;
                }
            }

            if (!IsReparsePoint(link))
            {
                error = "mklink 执行完成但未检测到联接，可能目标盘不支持。";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 删除 junction。只删链接本身，绝不删除目标内容。
        /// 路径不存在时视为"已经删掉了"返回 true；路径存在但不是 junction 时拒绝操作。
        /// </summary>
        public static bool Delete(string link, out string error)
        {
            error = null;

            bool isJunction = IsReparsePoint(link);
            bool exists = Directory.Exists(link);

            if (!exists && !isJunction)
            {
                return true; // 路径已不存在
            }
            if (!isJunction)
            {
                error = "路径存在但不是联接（junction），拒绝删除，避免误删数据：" + link;
                return false;
            }

            string args = "/c rmdir \"" + link + "\"";
            using (var p = RunHidden("cmd.exe", args))
            {
                if (p == null)
                {
                    error = "无法启动 cmd.exe";
                    return false;
                }
                string outp = p.StandardOutput.ReadToEnd();
                string errs = p.StandardError.ReadToEnd();
                p.WaitForExit();
                if (p.ExitCode != 0)
                {
                    error = (outp + errs).Trim();
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 解析 junction 指向的目标路径。非 junction 或解析失败返回 null。
        /// 用 DeviceIoControl + FSCTL_GET_REPARSE_POINT 读取挂载点缓冲。
        /// </summary>
        public static string TryResolveTarget(string linkPath)
        {
            if (!IsReparsePoint(linkPath))
            {
                return null;
            }

            IntPtr handle = CreateFile(
                linkPath, GENERIC_READ,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT,
                IntPtr.Zero);

            if (handle == INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                byte[] buffer = new byte[16384];
                uint returned;
                bool ok = false;

                var gch = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                try
                {
                    ok = DeviceIoControl(
                        handle, FSCTL_GET_REPARSE_POINT,
                        IntPtr.Zero, 0, gch.AddrOfPinnedObject(), (uint)buffer.Length,
                        out returned, IntPtr.Zero);
                }
                finally
                {
                    gch.Free();
                }

                if (!ok || returned < 20)
                {
                    return null;
                }

                uint tag = BitConverter.ToUInt32(buffer, 0);
                if (tag != IO_REPARSE_TAG_MOUNT_POINT)
                {
                    return null;
                }

                ushort subOffset = BitConverter.ToUInt16(buffer, 8);
                ushort subLength = BitConverter.ToUInt16(buffer, 10);
                if (subLength == 0)
                {
                    return null;
                }

                string substitute = Encoding.Unicode.GetString(buffer, 20 + subOffset, subLength);
                if (substitute.StartsWith(@"\??\", StringComparison.Ordinal))
                {
                    substitute = @"\\?\" + substitute.Substring(4);
                }
                return substitute;
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private static Process RunHidden(string fileName, string arguments)
        {
            try
            {
                var psi = new ProcessStartInfo(fileName, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                try
                {
                    psi.StandardOutputEncoding = GetOemEncoding();
                    psi.StandardErrorEncoding = GetOemEncoding();
                }
                catch (Exception)
                {
                    // 编码不可用就用默认
                }
                return Process.Start(psi);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static Encoding GetOemEncoding()
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception)
            {
                return Encoding.Default;
            }
        }
    }
}
