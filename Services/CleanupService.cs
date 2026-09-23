using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace FC.Services
{
    /// <summary>线程安全自增计数器（.NET Framework 无 Interlocked 带返回值原子版，手写封装）。</summary>
    public sealed class AtomicInteger
    {
        private int _value;
        public AtomicInteger(int initial) { _value = initial; }

        public int GetAndIncrement() { return Interlocked.Increment(ref _value) - 1; }
    }
    /// <summary>可清理项的分级：安全项默认勾选，谨慎项默认不勾（需用户主动选择）。</summary>
    public enum CleanupSafety
    {
        /// <summary>安全：缓存/临时/回收站，删了自动重建，无副作用。</summary>
        Safe,

        /// <summary>谨慎：会影响功能（如预读、更新历史），默认不勾。</summary>
        Caution,

        /// <summary>危险：可能涉及用户数据（如 Windows.old），默认不勾且执行前二次确认。</summary>
        Risky
    }

    /// <summary>
    /// 一个可清理目标。所有绑定字段都是**属性**（WPF 数据绑定不识别公有字段，用字段会显示空白）。
    /// </summary>
    public class CleanupItem : System.ComponentModel.INotifyPropertyChanged
    {
        private long _bytes;
        private bool _selected;
        private bool _isSized;
        private string _title;
        private string _description;
        private string _key;
        private string[] _paths;
        private string _special;
        private CleanupSafety _safety;

        public event System.ComponentModel.PropertyChangedEventHandler PropertyChanged;

        public string Key
        {
            get { return _key; }
            set { _key = value; }
        }

        public string Title
        {
            get { return _title; }
            set
            {
                if (_title != value)
                {
                    _title = value;
                    Raise("Title");
                }
            }
        }

        public string Description
        {
            get { return _description; }
            set
            {
                if (_description != value)
                {
                    _description = value;
                    Raise("Description");
                }
            }
        }

        /// <summary>待清空的目录或待删文件。Special 为空时按普通路径处理。</summary>
        public string[] Paths
        {
            get { return _paths; }
            set { _paths = value; }
        }

        /// <summary>特殊动作："recyclebin"（无 Paths，走 SHEmptyRecycleBin）。</summary>
        public string Special
        {
            get { return _special; }
            set { _special = value; }
        }

        public CleanupSafety Safety
        {
            get { return _safety; }
            set
            {
                if (_safety != value)
                {
                    _safety = value;
                    Raise("Safety");
                    Raise("SafetyText");
                }
            }
        }

        /// <summary>估算占用（已统计后有效；-1 = 统计失败）。</summary>
        public long Bytes
        {
            get { return _bytes; }
            set
            {
                if (_bytes != value)
                {
                    _bytes = value;
                    Raise("Bytes");
                    Raise("SizeText");
                }
            }
        }

        /// <summary>是否已完成占用估算（区分"真为 0"与"还没算"）。</summary>
        public bool IsSized
        {
            get { return _isSized; }
            set
            {
                if (_isSized != value)
                {
                    _isSized = value;
                    Raise("IsSized");
                    Raise("SizeText");
                }
            }
        }

        /// <summary>是否纳入清理（安全项默认 true，谨慎/危险默认 false）。</summary>
        public bool Selected
        {
            get { return _selected; }
            set
            {
                if (_selected != value)
                {
                    _selected = value;
                    Raise("Selected");
                }
            }
        }

        /// <summary>占用文本：未统计="统计中…"，失败="未知"，0="0 B"，其余人类可读。</summary>
        public string SizeText
        {
            get
            {
                if (!_isSized)
                {
                    return "统计中…";
                }
                if (_bytes < 0)
                {
                    return "未知";
                }
                if (_bytes == 0)
                {
                    return "0 B";
                }
                return FC.Converters.SizeText.Format(_bytes);
            }
        }

        public string SafetyText
        {
            get
            {
                switch (_safety)
                {
                    case CleanupSafety.Safe:
                        return "安全";
                    case CleanupSafety.Caution:
                        return "谨慎";
                    default:
                        return "危险";
                }
            }
        }

        private void Raise(string name)
        {
            var h = PropertyChanged;
            if (h != null)
            {
                h(this, new System.ComponentModel.PropertyChangedEventArgs(name));
            }
        }
    }

    /// <summary>一次清理的执行结果。</summary>
    public class CleanupOutcome
    {
        public long FreedBytes;
        public List<string> FailedPaths = new List<string>();
        public List<string> CleanedTitles = new List<string>();
        public bool Cancelled;
    }

    /// <summary>
    /// 「一键清理建议」：枚举常见可清理项（回收站/临时/更新缓存/缩略图/WER/预读/Windows.old），
    /// 并行估算占用，并真实删除所选内容（复用 FileDeleter 的稳健删除战术）。
    /// 删除是"清空内容、保留目录本身"；回收站走 SHEmptyRecycleBin。
    /// </summary>
    public static class CleanupService
    {
        private const uint SHERB_NOCONFIRMATION = 0x1;
        private const uint SHERB_NOPROGRESSUI = 0x2;
        private const uint SHERB_NOSOUND = 0x4;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern int SHEmptyRecycleBin(IntPtr hwnd, string pszRootPath, uint dwFlags);

        private static readonly string Windir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        private static readonly string SystemDrive = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));

        /// <summary>构造全部候选清理项（路径按当前系统展开；不存在的自动过滤，回收站恒在）。</summary>
        public static List<CleanupItem> BuildItems()
        {
            var items = new List<CleanupItem>();

            AddDirItem(items, "recyclebin", "回收站", "已删除文件（清空后不可恢复）。",
                null, CleanupSafety.Safe, true, "recyclebin");

            AddDirItem(items, "usertemp", "用户临时文件（%TEMP%）",
                "用户会话临时文件，程序退出后遗留，可安全清理。",
                new[] { GetUserTemp() }, CleanupSafety.Safe, true, null);

            AddDirItem(items, "wintemp", "Windows 临时文件（Windows\\Temp）",
                "系统临时文件。安装程序中断等会遗留，可安全清理。",
                new[] { Path.Combine(Windir, "Temp") }, CleanupSafety.Safe, true, null);

            AddDirItem(items, "updatecache", "Windows 更新下载缓存",
                "已下载的更新安装包缓存（SoftwareDistribution\\Download）。删除后仅下次更新时重新下载，不影响已装更新。",
                new[] { Path.Combine(Windir, "SoftwareDistribution", "Download") },
                CleanupSafety.Safe, true, null);

            AddDirItem(items, "deliveryopt", "传递优化缓存（Delivery Optimization）",
                "Windows 更新对等分发缓存，可安全清理。",
                new[] { Path.Combine(Windir, "ServiceProfiles", "NetworkService",
                    "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache") },
                CleanupSafety.Safe, true, null);

            var thumbFiles = CollectThumbnailFiles();
            if (thumbFiles.Count > 0)
            {
                AddDirItem(items, "thumbcache", "缩略图缓存",
                    "文件管理器缩略图缓存（thumbcache_*.db / iconcache_*.db），删除后自动重建，仅首次浏览略慢。",
                    thumbFiles.ToArray(), CleanupSafety.Safe, true, null);
            }

            AddDirItem(items, "wer", "错误报告（WER）",
                "Windows 错误报告（用户 + 系统两处）。崩溃转储可能较大，可安全清理。",
                new[]
                {
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "Microsoft", "Windows", "WER"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                        "Microsoft", "Windows", "WER")
                },
                CleanupSafety.Safe, true, null);

            AddDirItem(items, "prefetch", "预读缓存（Prefetch）",
                "程序启动预读缓存，删除后仅首次启动略慢，会自动重建。",
                new[] { Path.Combine(Windir, "Prefetch") }, CleanupSafety.Caution, false, null);

            AddDirItem(items, "windowsold", "Windows.old（旧系统/旧版本文件）",
                "升级留下的旧系统文件，可能数 GB~数十 GB。删除后无法回滚旧系统。建议优先用“磁盘清理→清理系统文件→以前的 Windows 安装”。",
                new[] { Path.Combine(SystemDrive, "Windows.old") }, CleanupSafety.Risky, false, null);

            return items;
        }

        private static void AddDirItem(List<CleanupItem> items, string key, string title, string description,
            string[] paths, CleanupSafety safety, bool selected, string special)
        {
            var pathsOk = new List<string>();
            if (special == null)
            {
                if (paths == null)
                {
                    return;
                }
                foreach (var p in paths)
                {
                    try
                    {
                        if (string.IsNullOrEmpty(p))
                        {
                            continue;
                        }
                        if (Directory.Exists(p) || File.Exists(p))
                        {
                            pathsOk.Add(p);
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
                if (pathsOk.Count == 0)
                {
                    return; // 全部不存在 → 该盘上无此清理项，不列出
                }
            }

            items.Add(new CleanupItem
            {
                Key = key,
                Title = title,
                Description = description,
                Paths = pathsOk.ToArray(),
                Special = special,
                Safety = safety,
                Selected = selected,
                IsSized = false
            });
        }

        private static string GetUserTemp()
        {
            try
            {
                string t = Path.GetTempPath();
                if (!string.IsNullOrEmpty(t))
                {
                    return t.TrimEnd('\\');
                }
            }
            catch (Exception)
            {
            }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Temp");
        }

        private static List<string> CollectThumbnailFiles()
        {
            var result = new List<string>();
            try
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "Microsoft", "Windows", "Explorer");
                if (!Directory.Exists(dir))
                {
                    return result;
                }
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    string name = Path.GetFileName(f);
                    if (name.StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("iconcache_", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(f);
                    }
                }
            }
            catch (Exception)
            {
            }
            return result;
        }

        // ==================== 占用估算 ====================

        /// <summary>并行估算各项占用（后台线程调用；进度回调每完成一项报告）。
        /// 并发限制为 3 个 worker 按任务队列取项，避免大目录同时全速统计造成内存尖峰（小内存机器闪退）。
        /// <para>worker 不直接改 item（跨线程改 observable 会崩绑定），结果经 <paramref name="onSized"/> 回调交回前
        /// （调用方负责 marshal 到 UI 线程）；onSized 为 null 时直接写（无 UI 场景，如测试）。</para></summary>
        public static void RefreshSizes(List<CleanupItem> items, IProgress<string> progress,
            CancellationToken ct, Action<CleanupItem, long> onSized = null)
        {
            if (items == null)
            {
                return;
            }
            int total = items.Count;
            if (total == 0)
            {
                return;
            }
            int workers = Math.Min(3, total);
            var next = new AtomicInteger(0);
            var mre = new CountdownEvent(workers);
            for (int w = 0; w < workers; w++)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    try
                    {
                        while (true)
                        {
                            ct.ThrowIfCancellationRequested();
                            int idx = next.GetAndIncrement();
                            if (idx >= total)
                            {
                                break;
                            }
                            var it = items[idx];
                            long size;
                            try
                            {
                                size = EstimateItem(it);
                            }
                            catch (Exception)
                            {
                                size = -1;
                            }
                            if (onSized != null)
                            {
                                onSized(it, size);
                            }
                            else
                            {
                                it.Bytes = size;
                                it.IsSized = true;
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception)
                    {
                    }
                    finally
                    {
                        mre.Signal();
                    }
                });
            }
            while (!mre.Wait(200))
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report("正在统计可清理空间…");
            }
        }

        /// <summary>
        /// 估算回收站占用：遍历各盘根目录下的 $Recycle.Bin（含各用户 SID 子目录）真实求和。
        /// 不使用 SHQueryRecycleBin——该 API 在多盘/某些系统上会返回异常大的垃圾值（曾出现 13.8TB）。
        /// </summary>
        public static long EstimateRecycleBin()
        {
            long total = 0;
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable)
                        {
                            continue;
                        }
                        string rb = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin");
                        if (Directory.Exists(rb))
                        {
                            total += FastDirectory.SumSizeRecursive(rb);
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
            return total;
        }

        private static long EstimateItem(CleanupItem item)
        {
            if (item.Special == "recyclebin")
            {
                return EstimateRecycleBin();
            }
            if (item.Paths == null)
            {
                return 0;
            }
            long total = 0;
            foreach (var p in item.Paths)
            {
                try
                {
                    if (Directory.Exists(p))
                    {
                        total += SizeDirectory(p);
                    }
                    else if (File.Exists(p))
                    {
                        total += new FileInfo(p).Length;
                    }
                }
                catch (Exception)
                {
                }
            }
            return total;
        }

        /// <summary>递归统计目录大小（流式，不跟随重解析点，带 60s/200 万文件上限，超限返回已计部分）。
        /// 用 FastDirectory.SumSizeRecursive——不物化 List，内存恒定，避免大目录引发内存尖峰。</summary>
        private static long SizeDirectory(string dir)
        {
            try
            {
                return FastDirectory.SumSizeRecursive(dir);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ==================== 执行清理 ====================

        /// <summary>真实清理所选各项。返回结果（含失败路径与释放估算）。</summary>
        public static CleanupOutcome Clean(
            List<CleanupItem> items, IProgress<string> log, CancellationToken ct)
        {
            var outcome = new CleanupOutcome();
            if (items == null)
            {
                return outcome;
            }

            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();
                if (!item.Selected)
                {
                    continue;
                }
                log?.Report("=== " + item.Title + " ===");

                if (item.Special == "recyclebin")
                {
                    try
                    {
                        int hr = SHEmptyRecycleBin(IntPtr.Zero, null,
                            SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
                        if (hr == 0)
                        {
                            log?.Report("回收站已清空（估算释放 " + FormatSize(item.Bytes) + "）。");
                            outcome.FreedBytes += Math.Max(0, item.Bytes);
                            outcome.CleanedTitles.Add(item.Title);
                        }
                        else
                        {
                            outcome.FailedPaths.Add("回收站（Win32 " + hr + "）");
                            log?.Report("回收站清空失败（Win32 " + hr + "）。");
                        }
                    }
                    catch (Exception ex)
                    {
                        outcome.FailedPaths.Add("回收站：" + ex.Message);
                        log?.Report("回收站清空异常：" + ex.Message);
                    }
                    continue;
                }

                long itemFreed = 0;
                if (item.Paths != null)
                {
                    foreach (var p in item.Paths)
                    {
                        ct.ThrowIfCancellationRequested();
                        try
                        {
                            if (Directory.Exists(p))
                            {
                                log?.Report("清理目录：" + p);
                                FileDeleter.EmptyDirectoryContents(p, log, ct, outcome.FailedPaths);
                            }
                            else if (File.Exists(p))
                            {
                                log?.Report("删除文件：" + p);
                                FileDeleter.DeleteFile(p, outcome.FailedPaths);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            outcome.FailedPaths.Add(p + "：" + ex.Message);
                        }
                    }
                }

                // 估算本次释放：整项占用 - 仍存在的部分
                long remains = EstimateItem(item);
                itemFreed = Math.Max(0, item.Bytes - remains);
                outcome.FreedBytes += itemFreed;
                if (itemFreed > 0)
                {
                    outcome.CleanedTitles.Add(item.Title);
                }
                log?.Report("该项清理完成（释放约 " + FormatSize(itemFreed) + "）。");
            }

            outcome.Cancelled = ct.IsCancellationRequested;
            return outcome;
        }

        /// <summary>是否存在需要管理员权限才能访问的清理项路径（用于清理前提示提权）。</summary>
        public static bool AnySystemPaths(List<CleanupItem> items)
        {
            if (items == null)
            {
                return false;
            }
            return items.Any(i => i.Selected && i.Paths != null
                && i.Paths.Any(p =>
                {
                    try
                    {
                        return p.StartsWith(Windir, StringComparison.OrdinalIgnoreCase);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                }));
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 0)
            {
                return "未知";
            }
            return FC.Converters.SizeText.Format(bytes);
        }
    }
}
