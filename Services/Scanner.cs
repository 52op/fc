using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Configuration;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FC.Models;

namespace FC.Services
{
    /// <summary>
    /// 扫描器（TreeSize 式，单遍并行 + 流式出树 + 增量复用）：
    /// - D：FindFirstFileEx + FIND_FIRST_EX_LARGE_FETCH 批量抓取；扫描状态内嵌在 DiskNode（无字典查找）；工作队列批量出队；线程数可配。
    /// - E：命中排除列表(SkipList) 的目录整棵跳过，计入"已排除"。
    /// - C：快速复用模式(QuickReuseMode)下：目标缓存树的目录按 mtime 校验，未变分支整棵复用免枚举，变更分支递归深入。
    /// 完成后根节点统计行 + 第一层目录固定可见；每行进度条随计算增长；实时重排由 MainViewModel 计时器驱动。
    /// </summary>
    public class Scanner : IScanner
    {
        private readonly int _threads;
        private readonly int _batchSize = 32;

        // mtime 两侧统一用 GetLastWriteTimeUtc 采集，可严格相等比较（0 容差）
        private const long MtimeToleranceTicks = 0;

        private static bool SameMtime(long cachedUtc, long liveUtc)
        {
            return cachedUtc == liveUtc;
        }

        /// <summary>快速复用模式（UI 勾选后重扫复用上次缓存）。</summary>
        public bool QuickReuseMode { get; set; }

        /// <summary>增量缓存（AppServices 注入）。</summary>
        public ScanCache Cache { get; set; }

        public Scanner()
            : this(ReadConfigThreads())
        {
        }

        public Scanner(int threads)
        {
            _threads = Math.Max(1, threads);
        }

        private static int ReadConfigThreads()
        {
            try
            {
                string s = ConfigurationManager.AppSettings["ScanThreads"];
                int v;
                return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) && v > 0 ? v : 32;
            }
            catch (Exception)
            {
                return 32;
            }
        }

        // ==================== 入口 ====================

        public Task<ScanResult> ScanAsync(string path, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                long[] counters = new long[5]; // [0]=处理计数, [1]=字节, [2]=排除, [3]=复用目录数, [4]=跳过目录数
                Dictionary<string, ScanCache.CachedDir> cachedIndex = null;
                ScanCache.CachedDir cachedRoot = null;

                if (Cache != null)
                {
                    cachedRoot = Cache.GetTargetRoot(path);
                    if (cachedRoot != null)
                    {
                        cachedIndex = new Dictionary<string, ScanCache.CachedDir>(StringComparer.OrdinalIgnoreCase);
                        IndexCache(cachedRoot, cachedIndex);
                    }
                }
                bool hadNoCacheBefore = (cachedRoot == null);

                ScanContext ctx = new ScanContext(counters, progress, cachedIndex, ct);

                bool quickRoot = false;
                DiskNode root = null;
                var queue = new ConcurrentQueue<DiskNode>();

                // C：快速复用 —— 全树逐目录 stat 校验（轻量属性调用），未变目录复用缓存，变化目录入队完整枚举
                if (QuickReuseMode && ctx.CachedIndex != null && cachedRoot != null)
                {
                    root = QuickReuseBuild(cachedRoot, null, queue, ctx);
                    quickRoot = true;
                }

                if (root == null)
                {
                    root = new DiskNode
                    {
                        FullPath = path,
                        Name = Path.GetFileName(path),
                        IsFolder = true,
                        LastWriteTime = SafeDirMtime(path)
                    };
                    if (string.IsNullOrEmpty(root.Name))
                    {
                        root.Name = path;
                    }
                    queue.Enqueue(root);
                }

                if (quickRoot)
                {
                    // 快速模式：树一到手就铺 UI（cs 复用子树与已枚举内容），后台继续处理和校验变化分支
                    Report(ctx, new ScanProgress
                    {
                        Phase = ScanPhase.Sizing,
                        RootReady = root,
                        CompletedFolders = Interlocked.Read(ref counters[0]),
                        CompletedBytes = Interlocked.Read(ref counters[1]),
                        ExcludedCount = Interlocked.Read(ref counters[2])
                    });
                }

                var tasks = new List<Task>();
                for (int i = 0; i < _threads; i++)
                {
                    tasks.Add(Task.Run(() => ScanWorker(queue, ctx)));
                }
                try
                {
                    Task.WaitAll(tasks.ToArray(), ct);
                }
                catch (AggregateException ae)
                {
                    if (ct.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(ct);
                    }
                    throw ae.Flatten().InnerException ?? ae;
                }
                ct.ThrowIfCancellationRequested();

                TryComplete(root, ctx);

                if (Cache != null && hadNoCacheBefore)
                {
                    Cache.Save(path, root);
                }

                progress?.Report(new ScanProgress
                {
                    Phase = ScanPhase.Done,
                    IsDone = true,
                    RootNode = root,
                    CompletedFolders = Interlocked.Read(ref counters[0]),
                    CompletedBytes = Interlocked.Read(ref counters[1]),
                    ExcludedCount = Interlocked.Read(ref counters[2]),
                    ReusedCount = Interlocked.Read(ref counters[3]),
                    SkippedCount = Interlocked.Read(ref counters[4]),
                    TotalFolders = CountDirNodes(root)
                });

                return new ScanResult
                {
                    RootNode = root,
                    Children = root.Children,
                    ExcludedCount = Interlocked.Read(ref counters[2]),
                    TotalFiles = root.FileCount,
                    TotalBytes = root.TotalSize,
                    TotalFolders = CountDirNodes(root),
                    ReusedCount = Interlocked.Read(ref counters[3])
                };
            }, ct);
        }

        // ==================== 扫描上下文 ====================

        private sealed class ScanContext
        {
            public long[] Counters;
            public IProgress<ScanProgress> Progress;
            public Dictionary<string, ScanCache.CachedDir> CachedIndex;
            public CancellationToken Ct;

            public ScanContext(long[] counters, IProgress<ScanProgress> progress,
                Dictionary<string, ScanCache.CachedDir> cachedIndex, CancellationToken ct)
            {
                Counters = counters;
                Progress = progress;
                CachedIndex = cachedIndex;
                Ct = ct;
            }
        }

        private void ScanWorker(ConcurrentQueue<DiskNode> queue, ScanContext ctx)
        {
            var local = new List<DiskNode>(_batchSize);
            while (true)
            {
                if (ctx.Ct.IsCancellationRequested)
                {
                    return;
                }
                local.Clear();
                for (int i = 0; i < _batchSize; i++)
                {
                    DiskNode d;
                    if (queue.TryDequeue(out d))
                    {
                        local.Add(d);
                    }
                    else
                    {
                        break;
                    }
                }
                if (local.Count == 0)
                {
                    return;
                }
                foreach (var d in local)
                {
                    if (ctx.Ct.IsCancellationRequested)
                    {
                        return;
                    }
                    EnumNode(d, queue, ctx);
                    TryComplete(d, ctx);
                }
            }
        }

        // ==================== 单目录 ====================

        private void EnumNode(DiskNode d, ConcurrentQueue<DiskNode> queue, ScanContext ctx)
        {
            if (d.PendingFilesDone == 1)
            {
                return; // 已由缓存/复用标记完成
            }

            // E：排除列表整棵跳过（扫描目标本身不跳过）
            if (d.IsSkipped && d.ParentNode != null)
            {
                Interlocked.Increment(ref ctx.Counters[4]);
                d.Error = "已排除（跳过列表）";
                d.PendingFilesDone = 1;
                d.PendingRemaining = 0;
                return;
            }

            // C：快速复用 —— 目录 mtime 与缓存一致则整棵免枚举
            if (QuickReuseMode && ctx.CachedIndex != null)
            {
                DateTime? liveMtime = null;
                ScanCache.CachedDir cached;
                if (d.ParentNode == null)
                {
                    // 根：单独取一次 mtime
                    if (ctx.CachedIndex.TryGetValue(d.FullPath, out cached))
                    {
                        try
                        {
                            liveMtime = Directory.GetLastWriteTimeUtc(d.FullPath);
                            d.LastWriteTime = liveMtime;
                        }
                        catch (Exception)
                        {
                            liveMtime = null;
                        }
                    }
                }
                else if (d.LastWriteTime.HasValue)
                {
                    liveMtime = d.LastWriteTime; // 父枚举已给出
                }

                if (liveMtime.HasValue &&
                    ctx.CachedIndex.TryGetValue(d.FullPath, out cached) &&
                    SameMtime(cached.LastWriteUtc, liveMtime.Value.ToFileTimeUtc()))
                {
                    ApplyCacheTo(d, cached);
                    Interlocked.Increment(ref ctx.Counters[3]);
                    return;
                }
            }

            // 正常枚举
            var dirs = new List<DiskNode>();
            long size = 0;
            long alloc = 0;
            long fileCount = 0;

            if (string.IsNullOrEmpty(d.Error) && !d.IsReparsePoint)
            {
                try
                {
                    if (d.ParentNode == null)
                    {
                        d.LastWriteTime = SafeDirMtime(d.FullPath); // 根用自己的 mtime，供缓存校验
                    }

                    var dirsData = new List<FastDirectory.DirInfoData>();
                    var filesData = new List<FastDirectory.FileInfoData>();
                    FastDirectory.List(d.FullPath, dirsData, filesData);

                    int cluster = VolumeInfo.GetClusterSize(d.FullPath);
                    foreach (var f in filesData)
                    {
                        size += f.Length;
                        alloc += VolumeInfo.RoundUpToCluster(f.Length, cluster);
                        fileCount++;
                    }

                    foreach (var s in dirsData)
                    {
                        var sub = new DiskNode
                        {
                            FullPath = s.FullPath,
                            Name = s.Name,
                            IsFolder = true,
                            // 统一用属性查询拿修改时间：与快速复用的校验同源，避免两侧 1ms 抖动
                            LastWriteTime = SafeDirMtime(s.FullPath),
                            IsReparsePoint = s.IsReparse
                        };
                        if (sub.IsReparsePoint)
                        {
                            sub.Error = "联接（junction）";
                        }
                        else if (SkipList.IsSkipped(sub.Name))
                        {
                            sub.IsSkipped = true;
                            sub.Error = "已排除（跳过列表）";
                        }
                        dirs.Add(sub);
                    }
                }
                catch (UnauthorizedAccessException ex)
                {
                    d.Error = "无权限：" + ex.Message;
                }
                catch (PathTooLongException ex)
                {
                    d.Error = "路径过长：" + ex.Message;
                }
                catch (IOException ex)
                {
                    d.Error = ex.Message;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    d.Error = ex.Message;
                }
            }

            d.DirectSize = size;
            d.DirectAllocated = alloc;
            d.DirectFileCount = fileCount;
            d.Children = dirs;

            // 只对可深入子目录排队
            int enqueued = 0;
            foreach (var sub in dirs)
            {
                if (sub.IsSkipped || sub.IsReparsePoint || sub.Error != null)
                {
                    continue;
                }
                sub.ParentNode = d;
                sub.PendingFilesDone = 0;
                queue.Enqueue(sub);
                enqueued++;
            }
            d.PendingRemaining = enqueued;
            d.PendingDone = 0;
            Interlocked.Exchange(ref d.PendingFilesDone, 1);

            // 上报：根就绪 / 浅层枚举完成 / 节流
            long enumerated = Interlocked.Increment(ref ctx.Counters[0]);
            if (d.ParentNode == null)
            {
                Report(ctx, new ScanProgress
                {
                    Phase = ScanPhase.Sizing,
                    RootReady = d,
                    CompletedFolders = enumerated,
                    CompletedBytes = size,
                    ExcludedCount = Interlocked.Read(ref ctx.Counters[2])
                });
            }
            else if (d.ParentNode.ParentNode == null || enumerated % 32 == 0)
            {
                Report(ctx, new ScanProgress
                {
                    Phase = ScanPhase.Sizing,
                    EnumeratedNode = d,
                    CompletedFolders = enumerated,
                    CompletedBytes = Interlocked.Read(ref ctx.Counters[1]) + size,
                    ExcludedCount = Interlocked.Read(ref ctx.Counters[2])
                });
            }
        }

        /// <summary>把缓存镜像套到节点上，子树标记为已完成。</summary>
        private void ApplyCacheTo(DiskNode d, ScanCache.CachedDir cached)
        {
            d.LastWriteTime = cached.LastWriteUtc > 0 ? DateTime.FromFileTimeUtc(cached.LastWriteUtc).ToLocalTime() : (DateTime?)null;
            d.TotalSize = cached.TotalSize;
            d.TotalAllocated = cached.TotalAllocated;
            d.DirectSize = cached.DirectSize;
            d.DirectAllocated = cached.DirectAllocated;
            d.FileCount = cached.FileCount;
            d.DirectFileCount = cached.DirectFileCount;
            d.IsReparsePoint = cached.IsReparsePoint;
            d.IsSkipped = cached.IsSkipped;
            d.Error = cached.Error;
            d.Children = new List<DiskNode>();

            foreach (var child in cached.Children)
            {
                DiskNode c = ScanCache.FromCache(child);
                c.ParentNode = d;
                c.PendingFilesDone = 1;
                c.PendingCompleted = 1;
                d.Children.Add(c);
            }
            d.CompletedChildrenCount = d.Children.Count;
            d.PendingRemaining = 0;
            d.PendingDone = 0;
            d.PendingFilesDone = 1;
        }

        /// <summary>
        /// 快速复用：递归 stat 校验缓存树。未变目录复用缓存（子树递归 stat），
        /// 变化目录建占位并入队（EnumNode 完整枚举，其子目录按缓存 mtime 逐个复用）。
        /// 复用节点自身 PendingFilesDone=1，全部子节点就绪后立即完成并上抛。
        /// </summary>
        private DiskNode QuickReuseBuild(ScanCache.CachedDir c, DiskNode parent, ConcurrentQueue<DiskNode> queue, ScanContext ctx)
        {
            DateTime? live = SafeDirMtime(c.Path);
            bool unchanged = live.HasValue && SameMtime(c.LastWriteUtc, live.Value.ToFileTimeUtc());

            var node = new DiskNode
            {
                FullPath = c.Path,
                Name = c.Name,
                IsFolder = true,
                LastWriteTime = live,
                ParentNode = parent
            };

            if (unchanged)
            {
                node.DirectSize = c.DirectSize;
                node.DirectAllocated = c.DirectAllocated;
                node.DirectFileCount = c.DirectFileCount;
                node.IsReparsePoint = c.IsReparsePoint;
                node.IsSkipped = c.IsSkipped;
                node.Error = c.Error;
                node.PendingFilesDone = 1;
                node.PendingRemaining = 0;
                node.PendingDone = 0;

                int enqueuedCount = 0;
                foreach (var child in c.Children)
                {
                    DiskNode childNode = QuickReuseBuild(child, node, queue, ctx);
                    node.Children.Add(childNode);
                    if (childNode.PendingFilesDone == 0)
                    {
                        enqueuedCount++; // 变化的子目录：等它枚举完成
                    }
                }
                // 复用节点按"全部子节点"计账：复用子树底部完成时会向上上报
                node.PendingRemaining = node.Children.Count;
                node.CompletedChildrenCount = node.Children.Count;
                Interlocked.Increment(ref ctx.Counters[3]); // 复用目录
                if (enqueuedCount == 0)
                {
                    TryComplete(node, ctx); // 整枝全复用：直接完成并上抛
                }
                return node;
            }

            // 变化：占位 + 入队完整枚举
            node.PendingFilesDone = 0;
            node.PendingRemaining = 0;
            node.PendingDone = 0;
            queue.Enqueue(node);
            return node;
        }

        private static DateTime? SafeDirMtime(string path)
        {
            try
            {
                return Directory.GetLastWriteTimeUtc(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        // ==================== 完成聚合 ====================

        private void TryComplete(DiskNode d, ScanContext ctx)
        {
            if (d.PendingFilesDone == 0 || d.PendingDone != d.PendingRemaining)
            {
                return;
            }
            if (Interlocked.CompareExchange(ref d.PendingCompleted, 1, 0) != 0)
            {
                return;
            }

            long total = d.DirectSize;
            long totalAllocated = d.DirectAllocated;
            long totalFiles = d.DirectFileCount;

            foreach (var c in d.Children)
            {
                total += c.TotalSize;
                totalAllocated += c.TotalAllocated;
                totalFiles += c.FileCount;
            }
            d.TotalSize = total;
            d.TotalAllocated = totalAllocated;
            d.FileCount = totalFiles;

            // 子目录按大小降序（实时重排另由 UI 计时器做）
            d.Children.Sort((a, b) =>
            {
                int bySize = b.TotalSize.CompareTo(a.TotalSize);
                if (bySize != 0)
                {
                    return bySize;
                }
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            long completed = Interlocked.Increment(ref ctx.Counters[0]);
            long bytes = Interlocked.Add(ref ctx.Counters[1], d.DirectSize);
            if (string.IsNullOrEmpty(d.Error) == false || d.IsReparsePoint || d.IsSkipped)
            {
                Interlocked.Increment(ref ctx.Counters[2]);
            }

            // 顶层目录完成：通知 UI 刷新该行（大小/文件就位）
            if (d.ParentNode != null && d.ParentNode.ParentNode == null)
            {
                Report(ctx, new ScanProgress
                {
                    Phase = ScanPhase.Sizing,
                    NewTopNode = d,
                    CompletedFolders = completed,
                    CompletedBytes = bytes,
                    ExcludedCount = Interlocked.Read(ref ctx.Counters[2]),
                    CurrentPath = d.FullPath
                });
            }
            else if (completed % 64 == 0)
            {
                Report(ctx, new ScanProgress
                {
                    Phase = ScanPhase.Sizing,
                    EnumeratedNode = d,
                    CompletedFolders = completed,
                    CompletedBytes = bytes,
                    ExcludedCount = Interlocked.Read(ref ctx.Counters[2]),
                    CurrentPath = d.FullPath
                });
            }

            // 向上通知父节点
            if (d.ParentNode != null)
            {
                int done = Interlocked.Increment(ref d.ParentNode.PendingDone);
                d.ParentNode.CompletedChildrenCount = done;
                if (done == d.ParentNode.PendingRemaining)
                {
                    TryComplete(d.ParentNode, ctx);
                }
            }
        }

        private static void Report(ScanContext ctx, ScanProgress p)
        {
            var pr = ctx.Progress;
            if (pr != null)
            {
                pr.Report(p);
            }
        }

        // ==================== 辅助 ====================

        private static void IndexCache(ScanCache.CachedDir root, Dictionary<string, ScanCache.CachedDir> index)
        {
            var stack = new Stack<ScanCache.CachedDir>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var c = stack.Pop();
                if (c.Path != null)
                {
                    index[c.Path] = c;
                }
                foreach (var child in c.Children)
                {
                    stack.Push(child);
                }
            }
        }

        private static long CountDirNodes(DiskNode root)
        {
            long n = 0;
            foreach (var _ in EnumerateAllDirs(root))
            {
                n++;
            }
            return n;
        }

        private static IEnumerable<DiskNode> EnumerateAllDirs(DiskNode root)
        {
            var stack = new Stack<DiskNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                DiskNode d = stack.Pop();
                yield return d;
                if (d.Children != null)
                {
                    foreach (var c in d.Children)
                    {
                        if (c.IsFolder)
                        {
                            stack.Push(c);
                        }
                    }
                }
            }
        }

        /// <summary>取某个目录"直接文件"的字节数（不递归）。读取失败返回 0。</summary>
        public static long GetDirectBytes(string dir)
        {
            try
            {
                long total = 0;
                foreach (var fi in new DirectoryInfo(dir).EnumerateFiles())
                {
                    total += fi.Length;
                }
                return total;
            }
            catch (Exception)
            {
                return 0;
            }
        }
    }
}