using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 检测哪些进程占用了给定文件/目录：
    /// 1) 句柄级枚举（NtQuerySystemInformation + NtQueryObject 判型 + DuplicateHandle + GetFinalPathNameByHandle）；
    /// 2) 未检出时回退 Restart Manager。
    /// 结束进程用 taskkill /F /T（跳过自身与系统关键进程）。
    /// </summary>
    public static class LockFinder
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX
        {
            public IntPtr Object;
            public UIntPtr UniqueProcessId;
            public UIntPtr HandleValue;
            public uint GrantedAccess;
            public ushort CreatorBackTraceIndex;
            public ushort ObjectTypeIndex;
            public uint HandleAttributes;
            public uint Reserved;
        }

        [DllImport("ntdll.dll")]
        private static extern int NtQuerySystemInformation(
            int SystemInformationClass, IntPtr SystemInformation,
            int SystemInformationLength, out int ReturnLength);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryObject(
            IntPtr Handle, int ObjectInformationClass,
            IntPtr ObjectInformation, int ObjectInformationLength, out int ReturnLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DuplicateHandle(
            IntPtr hSourceProcessHandle, IntPtr hSourceHandle, IntPtr hTargetProcessHandle,
            out IntPtr lpTargetHandle, uint dwDesiredAccess, bool bInheritHandle, uint dwOptions);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            IntPtr hFile, StringBuilder lpszFilePath, uint cchFilePath, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr ProcessHandle, uint DesiredAccess, out IntPtr TokenHandle);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool AdjustTokenPrivileges(IntPtr TokenHandle, bool DisableAllPrivileges,
            ref TOKEN_PRIVILEGES NewState, uint BufferLength, IntPtr PreviousState, IntPtr ReturnLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct TOKEN_PRIVILEGES
        {
            public uint PrivilegeCount;
            public long Luid;
            public uint Attributes;
        }

        private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
        private const uint TOKEN_QUERY = 0x8;
        private const uint SE_PRIVILEGE_ENABLED = 0x2;

        private const uint PROCESS_QUERY_INFORMATION = 0x0400;
        private const uint PROCESS_DUP_HANDLE = 0x0040;
        private const uint DUPLICATE_SAME_ACCESS = 0x00000002;
        private const uint FILE_NAME_NORMALIZED = 0x00000000;

        private const int SystemExtendedHandleInformation = 64;
        private const int ObjectTypeInformation = 2;
        private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
        private const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

        // ==================== 对外入口 ====================

        /// <summary>检测占用给定路径（文件或目录）的进程。自身与系统关键进程不会出现。绝不抛异常（异常时返回空）。</summary>
        public static List<LockerInfo> Find(params string[] paths)
        {
            try
            {
                TryEnableDebugPrivilege();

                var targets = BuildTargets(paths);
                if (targets == null || targets.Count == 0)
                {
                    return new List<LockerInfo>();
                }

                // Restart Manager 快，优先；它没有检出时再走限时句柄枚举兜底
                var rm = FindByRestartManager(paths);
                if (rm != null && rm.Count > 0)
                {
                    return rm;
                }

                List<LockerInfo> byHandle = null;
                var task = Task.Run(() => { byHandle = FindByHandles(targets); });
                try
                {
                    if (task.Wait(TimeSpan.FromSeconds(20)) && byHandle != null && byHandle.Count > 0)
                    {
                        return byHandle;
                    }
                }
                catch (AggregateException)
                {
                    // 后台句柄扫描内部异常：吞掉，当"未检出"处理
                }
                catch (Exception)
                {
                }
                return new List<LockerInfo>();
            }
            catch (Exception)
            {
                return new List<LockerInfo>();
            }
        }

        // ==================== 句柄级枚举 ====================

        /// <summary>尝试为当前进程启用 SeDebugPrivilege（管理员下可复制更高完整性进程的句柄）。失败静默。</summary>
        private static void TryEnableDebugPrivilege()
        {
            try
            {
                IntPtr token;
                if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out token))
                {
                    return;
                }
                try
                {
                    long luid;
                    if (!LookupPrivilegeValue(null, "SeDebugPrivilege", out luid))
                    {
                        return;
                    }
                    var tp = new TOKEN_PRIVILEGES
                    {
                        PrivilegeCount = 1,
                        Luid = luid,
                        Attributes = SE_PRIVILEGE_ENABLED
                    };
                    AdjustTokenPrivileges(token, false, ref tp, (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(TOKEN_PRIVILEGES)), IntPtr.Zero, IntPtr.Zero);
                }
                finally
                {
                    CloseHandle(token);
                }
            }
            catch (Exception)
            {
            }
        }

        private static List<string> BuildTargets(string[] paths)
        {
            var list = new List<string>();
            if (paths == null)
            {
                return list;
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (string.IsNullOrEmpty(p))
                {
                    continue;
                }
                if (!Directory.Exists(p) && !File.Exists(p))
                {
                    continue;
                }
                string t = NormalizePath(p);
                if (t != null && seen.Add(t))
                {
                    list.Add(t);
                }
            }
            return list;
        }

        private static string NormalizePath(string p)
        {
            try
            {
                string full = Path.GetFullPath(p).TrimEnd('\\', '/');
                if (full.StartsWith(@"\\?\", StringComparison.Ordinal))
                {
                    return full;
                }
                return @"\\?\" + full;
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static List<LockerInfo> FindByHandles(List<string> targets)
        {
            var result = new List<LockerInfo>();
            uint selfPid = (uint)Process.GetCurrentProcess().Id;

            byte[] buffer = QueryHandleTable();
            if (buffer == null)
            {
                return null; // 拿不到句柄表 → 交给 RM 兜底
            }

            var foundPids = new HashSet<uint>();
            int entrySize = Marshal.SizeOf(typeof(SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX));
            int count = (IntPtr.Size == 8)
                ? (int)BitConverter.ToInt64(buffer, 0)
                : BitConverter.ToInt32(buffer, 0);
            int baseOff = IntPtr.Size * 2; // 表头 = NumberOfHandles + Reserved

            // 学习"文件对象"的类型索引（主循环据此过滤，避免逐句柄查路径）
            int fileTypeIndex = FindFileTypeIndex(buffer, count, baseOff, entrySize, selfPid);
                        var stopwatch = Stopwatch.StartNew();

            {
                var caches = new ConcurrentDictionary<uint, IntPtr>();
                object gate = new object();

                Parallel.For(0, count,
                    new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount) },
                    (i, state) =>
                    {
                        // 总时长护栏：句柄爆炸时不无限等
                        if (stopwatch.ElapsedMilliseconds > 15000)
                        {
                            state.Break();
                            return;
                        }

                        int off = baseOff + i * entrySize;
                        if (off + entrySize > buffer.Length)
                        {
                            return;
                        }

                        SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX entry = ReadEntry(buffer, off);

                        uint pid = (uint)entry.UniqueProcessId;
                        if (pid <= 4 || pid == selfPid)
                        {
                            return;
                        }
                        if (entry.HandleValue == UIntPtr.Zero)
                        {
                            return;
                        }
                        if (fileTypeIndex >= 0 && entry.ObjectTypeIndex != fileTypeIndex)
                        {
                            return; // 非文件对象
                        }

                        IntPtr proc = GetCachedProcessHandleParallel(pid, caches);
                        if (proc == IntPtr.Zero)
                        {
                            return;
                        }

                        IntPtr dup;
                        if (!DuplicateHandle(proc, new IntPtr(unchecked((long)entry.HandleValue.ToUInt64())), GetCurrentProcess(),
                            out dup, 0, false, DUPLICATE_SAME_ACCESS))
                        {
                            return;
                        }

                        try
                        {
                            var sb = new StringBuilder(1024);
                            uint len = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
                            if (len == 0)
                            {
                                return;
                            }
                            if (len >= sb.Capacity)
                            {
                                sb.Capacity = (int)len + 8;
                                len = GetFinalPathNameByHandle(dup, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
                                if (len == 0)
                                {
                                    return;
                                }
                            }

                            string path = sb.ToString().TrimEnd('\\');
                            if (!MatchesTarget(path, targets))
                            {
                                return;
                            }

                            lock (gate)
                            {
                                if (foundPids.Contains(pid))
                                {
                                    return;
                                }
                                foundPids.Add(pid);
                                string name = "";
                                try
                                {
                                    var p2 = Process.GetProcessById((int)pid);
                                    name = p2.ProcessName;
                                }
                                catch (Exception)
                                {
                                }
                                result.Add(new LockerInfo { ProcessId = (int)pid, ProcessName = name });
                            }
                        }
                        finally
                        {
                            CloseHandle(dup);
                        }
                    });

                foreach (var h in caches.Values)
                {
                    if (h != IntPtr.Zero)
                    {
                        CloseHandle(h);
                    }
                }
            }

                        return result.Count > 0 ? result : null;
        }

        private static IntPtr GetCachedProcessHandleParallel(uint pid, ConcurrentDictionary<uint, IntPtr> cache)
        {
            return cache.GetOrAdd(pid, p => OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_DUP_HANDLE, false, p));
        }

        /// <summary>
        /// 学习"文件对象"的类型索引：先自开一个文件，用 NtQueryObject(ObjectTypeInformation) 确认为 File，
        /// 再扫本进程句柄取同类型句柄的 ObjectTypeIndex。找不到返回 -1（主循环不过滤）。
        /// </summary>
        private static int FindFileTypeIndex(
            byte[] buffer, int count, int baseOff, int entrySize, uint selfPid)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "fc_probe_" + Guid.NewGuid().ToString("N") + ".tmp");
            FileStream fs = null;
            try
            {
                File.WriteAllText(tmp, "x");
                fs = File.Open(tmp, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                IntPtr baseHandle = fs.SafeFileHandle.DangerousGetHandle();
                if (!IsFileTypeHandle(baseHandle))
                {
                    return -1;
                }

                IntPtr self = GetCurrentProcess();
                for (int i = 0; i < count; i++)
                {
                    int off = baseOff + i * entrySize;
                    if (off + entrySize > buffer.Length)
                    {
                        break;
                    }
                    SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX e = ReadEntry(buffer, off);
                    if ((uint)e.UniqueProcessId != selfPid || e.HandleValue == UIntPtr.Zero)
                    {
                        continue;
                    }
                    IntPtr dup;
                    if (!DuplicateHandle(self, new IntPtr(unchecked((long)e.HandleValue.ToUInt64())), self,
                        out dup, 0, false, DUPLICATE_SAME_ACCESS))
                    {
                        continue;
                    }
                    try
                    {
                        if (IsFileTypeHandle(dup))
                        {
                            return e.ObjectTypeIndex;
                        }
                    }
                    finally
                    {
                        CloseHandle(dup);
                    }
                }
            }
            catch (Exception)
            {
            }
            finally
            {
                if (fs != null)
                {
                    fs.Dispose();
                }
                try
                {
                    File.Delete(tmp);
                }
                catch (Exception)
                {
                }
            }
            return -1;
        }

        /// <summary>句柄对应的对象类型是否为 File（NtQueryObject ObjectTypeInformation）。</summary>
        private static bool IsFileTypeHandle(IntPtr handle)
        {
            try
            {
                int returned;
                IntPtr p = Marshal.AllocHGlobal(4096);
                try
                {
                    int st = NtQueryObject(handle, ObjectTypeInformation, p, 4096, out returned);
                    if (st != 0)
                    {
                        return false;
                    }
                    // UNICODE_STRING 前两字段: Length(0-2) MaximumLength(2-4)
                    ushort len = (ushort)Marshal.ReadInt16(p, 0);
                    IntPtr bufPtr = (IntPtr.Size == 8) ? Marshal.ReadIntPtr(p, 8) : Marshal.ReadIntPtr(p, 4);
                    if (bufPtr == IntPtr.Zero || len == 0)
                    {
                        return false;
                    }
                    string typeName = Marshal.PtrToStringUni(bufPtr, len / 2);
                    return string.Equals(typeName, "File", StringComparison.Ordinal);
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX ReadEntry(byte[] buffer, int off)
        {
            var e = new SYSTEM_HANDLE_TABLE_ENTRY_INFO_EX();
            e.Object = new IntPtr(BitConverter.ToInt64(buffer, off));
            e.UniqueProcessId = new UIntPtr(BitConverter.ToUInt64(buffer, off + IntPtr.Size));
            e.HandleValue = new UIntPtr(BitConverter.ToUInt64(buffer, off + 2 * IntPtr.Size));
            e.GrantedAccess = BitConverter.ToUInt32(buffer, off + 3 * IntPtr.Size);
            e.CreatorBackTraceIndex = BitConverter.ToUInt16(buffer, off + 3 * IntPtr.Size + 4);
            e.ObjectTypeIndex = BitConverter.ToUInt16(buffer, off + 3 * IntPtr.Size + 6);
            e.HandleAttributes = BitConverter.ToUInt32(buffer, off + 3 * IntPtr.Size + 8);
            e.Reserved = BitConverter.ToUInt32(buffer, off + 3 * IntPtr.Size + 12);
            return e;
        }

        private static bool MatchesTarget(string path, List<string> targets)
        {
            foreach (var t in targets)
            {
                if (string.Equals(path, t, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(t + "\\", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>返回句柄表原始字节，失败（含权限不足）返回 null。</summary>
        private static byte[] QueryHandleTable()
        {
            int size = 0;
            int status = NtQuerySystemInformation(SystemExtendedHandleInformation, IntPtr.Zero, 0, out size);
            bool ok = status == 0 || status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL;
            if (!ok || size <= 0)
            {
                return null;
            }

            for (int attempt = 0; attempt < 4; attempt++)
            {
                IntPtr p = Marshal.AllocHGlobal(size);
                try
                {
                    int returned = 0;
                    status = NtQuerySystemInformation(SystemExtendedHandleInformation, p, size, out returned);
                    if (status == 0)
                    {
                        var data = new byte[size];
                        Marshal.Copy(p, data, 0, size);
                        return data;
                    }
                    if (status == STATUS_INFO_LENGTH_MISMATCH || status == STATUS_BUFFER_TOO_SMALL)
                    {
                        size = returned;
                        continue;
                    }
                    return null;
                }
                finally
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            return null;
        }

        // ==================== Restart Manager 兜底 ====================

        [StructLayout(LayoutKind.Sequential)]
        private struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public SYSTEMTIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SYSTEMTIME
        {
            public ushort wYear;
            public ushort wMonth;
            public ushort wDayOfWeek;
            public ushort wDay;
            public ushort wHour;
            public ushort wMinute;
            public ushort wSecond;
            public ushort wMilliseconds;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)]
            public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RmRegisterResources(
            uint dwSessionHandle, uint nFiles,
            [In, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPWStr)] string[] rgsFilenames,
            uint nApplications, [In] IntPtr rgApplications,
            uint nServices, [In] IntPtr rgsServiceNames);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RmGetList(
            uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
            [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int RmEndSession(uint dwSessionHandle);

        private static List<LockerInfo> FindByRestartManager(string[] paths)
        {
            var result = new List<LockerInfo>();
            uint session;
            int hr = RmStartSession(out session, 0, Guid.NewGuid().ToString("N"));
            if (hr != 0)
            {
                return result;
            }
            try
            {
                var existing = new List<string>();
                if (paths != null)
                {
                    foreach (var p in paths)
                    {
                        if (!string.IsNullOrEmpty(p) && (Directory.Exists(p) || File.Exists(p)))
                        {
                            existing.Add(p);
                        }
                    }
                }
                if (existing.Count == 0)
                {
                    return result;
                }
                hr = RmRegisterResources(session, (uint)existing.Count, existing.ToArray(), 0, IntPtr.Zero, 0, IntPtr.Zero);
                if (hr != 0)
                {
                    return result;
                }

                uint needed = 0;
                uint count = 0;
                uint rebootReasons = 0;
                hr = RmGetList(session, out needed, ref count, null, ref rebootReasons);
                if (hr != 0 || needed == 0)
                {
                    return result;
                }

                var infos = new RM_PROCESS_INFO[needed];
                count = needed;
                hr = RmGetList(session, out needed, ref count, infos, ref rebootReasons);
                if (hr != 0)
                {
                    return result;
                }

                var seen = new HashSet<int>();
                for (int i = 0; i < infos.Length; i++)
                {
                    int pid = infos[i].Process.dwProcessId;
                    if (pid <= 4 || pid == Process.GetCurrentProcess().Id || seen.Contains(pid))
                    {
                        continue;
                    }
                    seen.Add(pid);
                    result.Add(new LockerInfo
                    {
                        ProcessId = pid,
                        ProcessName = infos[i].strAppName
                    });
                }
                return result;
            }
            finally
            {
                RmEndSession(session);
            }
        }

        // ==================== 结束进程 ====================

        /// <summary>该进程是否允许被结束（自身与系统关键进程除外）。</summary>
        public static bool IsSafeToKill(LockerInfo locker)
        {
            if (locker == null || locker.ProcessId <= 0)
            {
                return false;
            }
            if (locker.ProcessId <= 4 || locker.ProcessId == Process.GetCurrentProcess().Id)
            {
                return false;
            }
            string name = (locker.ProcessName ?? string.Empty).ToLowerInvariant();
            if (name == "system" || name == "idle" || name == "secure system" || name == "registry")
            {
                return false;
            }
            return true;
        }

        /// <summary>结束占用进程（taskkill /F /T）。返回失败描述列表，空即全部成功。</summary>
        public static List<string> Kill(List<LockerInfo> lockers)
        {
            var errors = new List<string>();
            if (lockers == null)
            {
                return errors;
            }
            var killed = new HashSet<int>();
            foreach (var l in lockers)
            {
                if (!IsSafeToKill(l) || killed.Contains(l.ProcessId))
                {
                    continue;
                }
                killed.Add(l.ProcessId);
                try
                {
                    var psi = new ProcessStartInfo("taskkill.exe", "/F /T /PID " + l.ProcessId)
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string outp = p.StandardOutput.ReadToEnd();
                        string errs = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        if (p.ExitCode != 0)
                        {
                            errors.Add(l.ToString() + "：" + (outp + errs).Trim());
                        }
                    }
                }
                catch (Exception ex)
                {
                    errors.Add(l.ToString() + "：" + ex.Message);
                }
            }
            return errors;
        }

        /// <summary>
        /// 未抓到具体句柄时的辅助提示：列出可能在目标目录留下"工作目录(CWD)"句柄的控制台进程
        /// （cmd/powershell/windowsterminal 等）。只提示、不会被自动结束。
        /// </summary>
        public static string LookupConsoleSuspects()
        {
            var hit = new List<string>();
            try
            {
                int self = Process.GetCurrentProcess().Id;
                foreach (var p in Process.GetProcesses())
                {
                    try
                    {
                        if (p.Id == self)
                        {
                            continue;
                        }
                        string n = p.ProcessName;
                        if (string.Equals(n, "cmd", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n, "powershell", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n, "pwsh", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n, "windowsterminal", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(n, "conhost", StringComparison.OrdinalIgnoreCase))
                        {
                            hit.Add(n + " (PID " + p.Id + ")");
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            catch (Exception)
            {
            }
            return hit.Count == 0 ? "" : string.Join("、", hit);
        }

        /// <summary>锁持有者列表的友好描述（每行一个）。</summary>
        public static string Describe(List<LockerInfo> lockers)
        {
            if (lockers == null || lockers.Count == 0)
            {
                return "";
            }
            var sb = new StringBuilder();
            foreach (var l in lockers)
            {
                if (sb.Length > 0)
                {
                    sb.AppendLine();
                }
                sb.Append("  · " + l);
            }
            return sb.ToString();
        }
    }
}