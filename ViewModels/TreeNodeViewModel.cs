using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using FC.Converters;
using FC.Models;
using FC.Services;

namespace FC.ViewModels
{
    /// <summary>
    /// 树节点 VM：包装 DiskNode。
    /// - 子树懒加载：展开时从 Data.Children 生成子目录 VM；直接文件行由 FileLister 展开时按需读取（扫描期不造文件对象）；
    /// - 有子内容但未展开时挂占位子节点保证展开箭头可见（模板里折叠）；
    /// - 提供表格列显示属性与右键命令（迁移/刷新/打开/资源管理器/复制路径）。
    /// </summary>
    public class TreeNodeViewModel : ViewModelBase
    {
        private static readonly TreeNodeViewModel Dummy = new TreeNodeViewModel(null, null, 0);

        /// <summary>整盘扫描是否已完成（控制"统计中…"占位显示）</summary>
        public static bool SizesComplete;

        private readonly AppServices _services;
        private readonly double _parentTotal;
        private readonly Dispatcher _dispatcher;
        private DiskNode _data;
        private bool _isExpanded;
        private bool _busy;
        private bool _filesRequested;
        private bool _filesLoaded;
        private System.Windows.Media.ImageSource _icon;

        public TreeNodeViewModel(DiskNode data, AppServices services, double parentTotal)
        {
            _data = data;
            _services = services;
            _parentTotal = parentTotal;
            _dispatcher = Dispatcher.CurrentDispatcher;

            Children = new ObservableCollection<TreeNodeViewModel>();
            if (HasAnyChildren(data))
            {
                Children.Add(Dummy);
            }
            NodeRegistry.Register(this);

            MigrateCommand = new RelayCommand(async () => await MigrateAsync(),
                () => !_busy && _data != null && _data.IsFolder && !_data.IsReparsePoint);
            MigrateViaEnvCommand = new RelayCommand(async () => await MigrateViaEnvAsync(),
                () => !_busy && _data != null && _data.IsFolder && !_data.IsReparsePoint
                      && EnvRule != null && EnvRule.Kind == RedirectKind.EnvVar);
            RefreshCommand = new RelayCommand(async () => await RefreshAsync(),
                () => !_busy && _data != null && _data.IsFolder);
            OpenExplorerCommand = new RelayCommand(OpenInExplorer,
                () => _data != null);
            CopyPathCommand = new RelayCommand(CopyPath,
                () => _data != null);
            OpenCommand = new RelayCommand(Open,
                () => _data != null && !_data.IsFolder);
        }

        public ObservableCollection<TreeNodeViewModel> Children { get; private set; }

        public RelayCommand MigrateCommand { get; private set; }

        public RelayCommand RefreshCommand { get; private set; }

        /// <summary>通过环境变量迁移（仅规则命中且为 EnvVar 类时可用）</summary>
        public RelayCommand MigrateViaEnvCommand { get; private set; }

        public RelayCommand OpenExplorerCommand { get; private set; }

        public RelayCommand CopyPathCommand { get; private set; }

        /// <summary>打开文件（仅文件行）</summary>
        public RelayCommand OpenCommand { get; private set; }

        /// <summary>是否为占位节点（仅为展开箭头）</summary>
        public bool IsDummy
        {
            get { return _data == null; }
        }

        public string FullPath
        {
            get { return _data == null ? null : _data.FullPath; }
        }

        public string HeaderText
        {
            get { return _data == null ? "" : _data.Name; }
        }

        /// <summary>名称列图标（资源管理器取回的真实图标，按类型缓存）</summary>
        public System.Windows.Media.ImageSource Icon
        {
            get
            {
                if (_icon == null && _data != null)
                {
                    _icon = SystemIcons.GetIcon(_data);
                }
                return _icon;
            }
        }

        public string SizeDisplayText
        {
            get
            {
                if (_data == null)
                {
                    return "";
                }
                if (!SizesComplete && _data.TotalSize == 0 && _data.FileCount == 0
                    && !_data.IsReparsePoint && string.IsNullOrEmpty(_data.Error))
                {
                    return "统计中…";
                }
                return SizeText.Format(_data.TotalSize);
            }
        }

        public string AllocatedDisplayText
        {
            get
            {
                if (_data == null)
                {
                    return "";
                }
                return SizeText.Format(_data.TotalAllocated);
            }
        }

        public string FileCountText
        {
            get
            {
                if (_data == null || !_data.IsFolder)
                {
                    return "";
                }
                return SizeText.FormatCount(_data.FileCount);
            }
        }

        public string FolderCountText
        {
            get
            {
                if (_data == null || !_data.IsFolder)
                {
                    return "";
                }
                return SizeText.FormatCount(_data.Children == null ? 0 : _data.Children.Count);
            }
        }

        public string LastWriteText
        {
            get
            {
                if (_data == null || !_data.LastWriteTime.HasValue)
                {
                    return "";
                }
                return _data.LastWriteTime.Value.ToString("yyyy-MM-dd HH:mm");
            }
        }

        /// <summary>文件行附加标签：特殊文件说明或"隐藏/系统文件"。目录行返回空。</summary>
        public string FileTagText
        {
            get
            {
                if (_data == null || _data.IsFolder)
                {
                    return "";
                }
                if (!string.IsNullOrEmpty(_data.SpecialNote))
                {
                    return _data.SpecialNote;
                }
                if (_data.IsHiddenOrSystem)
                {
                    return "隐藏/系统文件";
                }
                return "";
            }
        }

        private EnvVarRule _envRule;
        private bool _envRuleResolved;
        private string _migratedDest;
        private string _recordMigratedDest;
        private bool _recordMigratedResolved;

        /// <summary>
        /// 解析"迁移目标"。优先当前会话迁移（_migratedDest）；
        /// 否则查迁移记录（Environment变量迁移且状态 Active 且 SourcePath 匹配本节点）→ 标注"已迁移"。
        /// 返回 null = 未迁移过。
        /// </summary>
        private string ResolveMigratedDest()
        {
            if (!string.IsNullOrEmpty(_migratedDest))
            {
                return _migratedDest;
            }
            if (!_recordMigratedResolved)
            {
                _recordMigratedResolved = true;
                if (_services != null && _data != null && _data.IsFolder)
                {
                    try
                    {
                        var records = _services.Records.Load();
                        foreach (var r in records)
                        {
                            if (r.MigrationKind == MigrationKind.EnvVar
                                && r.Status == MigrationStatus.Active
                                && !string.IsNullOrEmpty(r.SourcePath)
                                && PathUtil.EndsWithIgnoreCase(_data.FullPath, r.SourcePath)
                                && !string.IsNullOrEmpty(r.DestPath))
                            {
                                _recordMigratedDest = r.DestPath;
                                break;
                            }
                        }
                    }
                    catch (Exception)
                    {
                    }
                }
            }
            return _recordMigratedDest;
        }
        private EnvVarRule EnvRule
        {
            get
            {
                if (!_envRuleResolved)
                {
                    _envRuleResolved = true;
                    if (_data != null && _data.IsFolder)
                    {
                        // 只匹配当前目录自身（后缀匹配），不向上归并。
                        // 这样标注/菜单只出现在规则命中的"软件数据根"那一层，
                        // 不会细分到里面的插件/子目录（用户明确不需要插件级）。
                        _envRule = EnvVarRuleSet.Match(_data.FullPath);
                    }
                }
                return _envRule;
            }
        }

        /// <summary>是否显示"通过环境变量迁移"菜单项（命中 EnvVar 规则、非联接、未迁移过）。</summary>
        public bool CanMigrateViaEnv
        {
            get
            {
                return _data != null && _data.IsFolder && !_data.IsReparsePoint
                    && string.IsNullOrEmpty(ResolveMigratedDest())
                    && EnvRule != null && EnvRule.Kind == RedirectKind.EnvVar;
            }
        }

        /// <summary>目录行附加标签：已迁移显示"已迁移 → 目标"，否则命中规则时显示软件归属 + 环境变量提示（绿色小字）。</summary>
        public string EnvTagText
        {
            get
            {
                if (_data == null || !_data.IsFolder)
                {
                    return "";
                }
                string dest = ResolveMigratedDest();
                if (!string.IsNullOrEmpty(dest))
                {
                    return "已迁移 → " + dest;
                }
                return EnvVarMatcher.BuildTagText(EnvRule);
            }
        }

        /// <summary>标注 tooltip（软件名 + 依据）。</summary>
        public string EnvTagTooltip
        {
            get
            {
                string dest = ResolveMigratedDest();
                if (!string.IsNullOrEmpty(dest))
                {
                    return "数据已迁移到：" + dest;
                }
                return EnvVarMatcher.BuildTooltip(EnvRule);
            }
        }

        /// <summary>占父目录空间的最终百分比（扫描完成后有效）</summary>
        public double PercentOfParent
        {
            get
            {
                if (_parentTotal <= 0)
                {
                    return 0;
                }
                return _data.TotalSize * 100.0 / _parentTotal;
            }
        }

        /// <summary>
        /// 进度条数值（0-100）：
        /// 扫描中 = 该目录"计算完成度"（已聚合子目录计数占比，随计算增长；叶子行留 0 不显示假百分比）；
        /// 扫描完成 = 占上一层真实百分比。
        /// </summary>
        public double ProgressBarValue
        {
            get
            {
                if (_data == null)
                {
                    return 0;
                }
                if (SizesComplete)
                {
                    return Math.Max(0, Math.Min(100, PercentOfParent));
                }

                // 扫描中：有子目录的行显示计算完成度
                var children = _data.Children;
                if (children != null && children.Count > 0)
                {
                    int done = _data.CompletedChildrenCount;
                    return Math.Max(0, Math.Min(100, done * 100.0 / children.Count));
                }
                // 叶子（无子目录）：扫描中不显示，避免"100%"假象
                return 0;
            }
        }

        /// <summary>
        /// 百分比列文字：扫描中显示"已完成子目录/总数"（如 5/12），扫描完成后显示真实占比（如 39.9%）。
        /// </summary>
        public string PercentText
        {
            get
            {
                if (_data == null)
                {
                    return "";
                }
                if (SizesComplete)
                {
                    double p = Math.Max(0, Math.Min(100, PercentOfParent));
                    return p.ToString("0.#") + "%";
                }
                var children = _data.Children;
                if (children != null && children.Count > 0)
                {
                    return _data.CompletedChildrenCount + "/" + children.Count;
                }
                return "";
            }
        }

        /// <summary>百分比列内进度条宽度（最大 90px）</summary>
        public double BarWidth
        {
            get
            {
                double v = ProgressBarValue;
                if (v <= 0)
                {
                    return 0;
                }
                return Math.Max(1, Math.Min(90, v / 100.0 * 90));
            }
        }

        /// <summary>错误或联接提示（红色小字显示在名称右侧）</summary>
        public string DetailText
        {
            get
            {
                if (_data == null)
                {
                    return null;
                }
                if (_data.IsReparsePoint)
                {
                    string target = JunctionUtil.TryResolveTarget(_data.FullPath);
                    return target == null ? "联接（目标不可达）" : "联接 → " + target;
                }
                return _data.Error;
            }
        }

        public bool IsExpanded
        {
            get { return _isExpanded; }
            set
            {
                if (Set(ref _isExpanded, value) && value)
                {
                    EnsureChildrenReady();
                }
            }
        }

        /// <summary>刷新显示属性（单位切换/排序后调用）</summary>
        public void NotifyDisplayChanged()
        {
            OnPropertyChanged("SizeDisplayText");
            OnPropertyChanged("AllocatedDisplayText");
            OnPropertyChanged("FileCountText");
            OnPropertyChanged("FolderCountText");
            OnPropertyChanged("LastWriteText");
            OnPropertyChanged("FileTagText");
            OnPropertyChanged("PercentText");
            OnPropertyChanged("BarWidth");
            OnPropertyChanged("Icon");
            OnPropertyChanged("DetailText");
            OnPropertyChanged("EnvTagText");
            OnPropertyChanged("EnvTagTooltip");
            OnPropertyChanged("CanMigrateViaEnv");
        }

        /// <summary>轻量刷新行进度（扫描计时器调用，热路径，只刷和进度/大小相关的属性）</summary>
        public void NotifyProgress()
        {
            OnPropertyChanged("SizeDisplayText");
            OnPropertyChanged("PercentText");
            OnPropertyChanged("BarWidth");
        }

        /// <summary>
        /// 实时从大到小排序：目录与文件按当前大小合并降序重排本行子行。
        /// 只读快照（不与扫描器并发竞态）；复用现有 VM 保持展开态。
        /// </summary>
        public void ApplyLiveSort()
        {
            if (_data == null || !_isExpanded)
            {
                return;
            }
            var ordered = BuildSortedChildren();
            if (ordered == null)
            {
                return;
            }
            try
            {
                // 早退：首行已是当前最大基本有序（避免每 250ms 全量重建）
                if (Children.Count > 0 && ordered.Count > 0 && Children[0].FullPath != null &&
                    string.Equals(ordered[0].FullPath, Children[0].FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                Children.Clear();
                foreach (var vm in ordered)
                {
                    Children.Add(vm);
                }
            }
            catch (Exception)
            {
                // 与扫描器并发读改的极罕见竞态：丢弃本次刷新，下一拍再试
            }
        }

        // ==================== 子节点懒加载 ====================

        private static bool HasAnyChildren(DiskNode data)
        {
            if (data == null)
            {
                return false;
            }
            if (data.Children != null && data.Children.Count > 0)
            {
                return true;
            }
            if (data.DirectFiles != null && data.DirectFiles.Count > 0)
            {
                return true;
            }
            return data.DirectFileCount > 0;
        }

        private void EnsureChildrenReady()
        {
            if (_data == null)
            {
                return;
            }
            RefreshChildren();
            MaybeLoadFiles();
        }

        /// <summary>按当前数据重建子行（目录 + 已加载文件，按大小降序合并，复用现有 VM）</summary>
        private void RefreshChildren()
        {
            Children.Clear();

            var dirs = _data.Children;
            var files = _filesLoaded ? _data.DirectFiles : null;
            bool hasDirs = dirs != null && dirs.Count > 0;
            bool hasFiles = files != null && files.Count > 0;

            if (hasDirs || hasFiles)
            {
                var ordered = BuildSortedChildren();
                if (ordered != null)
                {
                    foreach (var vm in ordered)
                    {
                        Children.Add(vm);
                    }
                }
            }
            else if (_data.IsFolder && !_filesLoaded && _data.DirectFileCount > 0)
            {
                // 目录下有文件但明细还没读出：占位保证展开箭头
                Children.Add(Dummy);
            }
        }

        /// <summary>
        /// 合并"目录 + 已加载文件"并按其当前大小降序生成展示顺序（只读快照，复用现有 VM）。
        /// 无子项时返回 null。
        /// </summary>
        private List<TreeNodeViewModel> BuildSortedChildren()
        {
            var dirs = _data.Children;
            var files = _filesLoaded ? _data.DirectFiles : null;
            int dcount = dirs == null ? 0 : dirs.Count;
            int fcount = files == null ? 0 : files.Count;
            if (dcount + fcount == 0)
            {
                return null;
            }
            if (dcount + fcount > 3000)
            {
                return null; // 超大门目录跳过实时重排以免卡顿
            }

            List<DiskNode> dirList = null;
            if (dcount > 0)
            {
                dirList = new List<DiskNode>(dirs);
                if (dcount > 1)
                {
                    dirList.Sort((a, b) =>
                    {
                        int bySize = b.TotalSize.CompareTo(a.TotalSize);
                        if (bySize != 0)
                        {
                            return bySize;
                        }
                        return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    });
                }
            }

            // 现有 VM 按路径索引（复用，保持展开态）
            var byPath = new Dictionary<string, TreeNodeViewModel>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Children)
            {
                if (c.FullPath != null)
                {
                    byPath[c.FullPath] = c;
                }
            }

            var result = new List<TreeNodeViewModel>(dcount + fcount);
            double pt = _data.TotalSize;
            int di = 0;
            int fi = 0;
            while (di < dcount || fi < fcount)
            {
                bool takeDir;
                if (di >= dcount)
                {
                    takeDir = false;
                }
                else if (fi >= fcount)
                {
                    takeDir = true;
                }
                else
                {
                    takeDir = dirList[di].TotalSize >= files[fi].Size;
                }

                if (takeDir)
                {
                    var d = dirList[di];
                    di++;
                    TreeNodeViewModel vm;
                    if (byPath.TryGetValue(d.FullPath, out vm))
                    {
                        result.Add(vm);
                    }
                    else
                    {
                        result.Add(new TreeNodeViewModel(d, _services, pt));
                    }
                }
                else
                {
                    var e = files[fi];
                    fi++;
                    TreeNodeViewModel vm;
                    if (byPath.TryGetValue(e.FullPath, out vm))
                    {
                        result.Add(vm);
                    }
                    else
                    {
                        result.Add(new TreeNodeViewModel(FileNodeFromEntry(e), _services, pt));
                    }
                }
            }
            return result;
        }

        /// <summary>按需读取本目录直接文件（每个目录最多一次）</summary>
        private async void MaybeLoadFiles()
        {
            if (_filesRequested || _data == null || !_data.IsFolder || _data.IsReparsePoint)
            {
                return;
            }
            _filesRequested = true;

            string path = _data.FullPath;
            List<FileEntry> list;
            try
            {
                list = await Task.Run(() => FileLister.ReadEntries(path));
            }
            catch (Exception)
            {
                list = new List<FileEntry>();
            }

            await _dispatcher.InvokeAsync(() =>
            {
                if (_data == null || !string.Equals(_data.FullPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
                _data.DirectFiles = list;
                _filesLoaded = true;
                if (_isExpanded)
                {
                    RefreshChildren();
                }
                NotifyDisplayChanged();
            });
        }

        private static DiskNode FileNodeFromEntry(FileEntry e)
        {
            return new DiskNode
            {
                FullPath = e.FullPath,
                Name = e.Name,
                IsFolder = false,
                TotalSize = e.Size,
                DirectSize = e.Size,
                TotalAllocated = e.Allocated,
                DirectAllocated = e.Allocated,
                LastWriteTime = e.LastWriteTime,
                IsHiddenOrSystem = e.IsHiddenOrSystem,
                SpecialNote = e.SpecialNote
            };
        }

        /// <summary>
        /// 扫描流式/排序后从 Data 重建子树界面，保留展开状态。
        /// </summary>
        public void ResyncFromData()
        {
            var expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            CollectExpanded(this, expanded);

            Children.Clear();
            if (HasAnyChildren(_data))
            {
                Children.Add(Dummy);
            }

            if (_isExpanded)
            {
                EnsureChildrenReady();
                RestoreExpanded(this, expanded);
            }
            NotifyDisplayChanged();
        }

        private static void CollectExpanded(TreeNodeViewModel v, HashSet<string> set)
        {
            if (v._isExpanded && v.FullPath != null)
            {
                set.Add(v.FullPath);
            }
            foreach (var c in v.Children)
            {
                CollectExpanded(c, set);
            }
        }

        private static void RestoreExpanded(TreeNodeViewModel v, HashSet<string> set)
        {
            foreach (var c in v.Children)
            {
                if (set.Contains(c.FullPath))
                {
                    c.IsExpanded = true;
                }
                RestoreExpanded(c, set);
            }
        }

        // ==================== 迁移 ====================

        public async Task MigrateAsync()
        {
            if (_busy || _data == null || !_data.IsFolder)
            {
                return;
            }
            if (_data.IsReparsePoint)
            {
                _services.Dialogs.ShowError("无法迁移", "该目录已是联接（junction），不能再次迁移。" + Environment.NewLine
                    + "如需把数据放回原位置，请在“查看迁移记录”里执行还原。");
                return;
            }

            string destRoot;
            try
            {
                destRoot = _services.Dialogs.PickFolder(
                    "选择目标目录（数据将复制到 目标\\" + _data.Name + "）", null, out var cancelled);
                if (cancelled || string.IsNullOrEmpty(destRoot))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("选择目录失败", ex.Message);
                return;
            }

            string error = _services.Migrator.ValidateMigration(_data.FullPath, destRoot);
            if (error != null)
            {
                _services.Dialogs.ShowError("无法迁移", error);
                return;
            }

            string destFolder = Path.Combine(destRoot, _data.Name);
            string confirmMsg = string.Format(
                "源：{0}\n目标：{1}\n\n约 {2} 个文件 / {3}。\n\n执行流程：robocopy 复制 → 校验 → 删除原目录 → 创建联接(junction)。\n\n确认开始迁移？",
                _data.FullPath, destFolder, _data.FileCount, SizeText.Format(_data.TotalSize));

            if (!_services.Dialogs.Confirm("确认迁移", confirmMsg))
            {
                return;
            }

            _busy = true;
            RaiseCommands();

            try
            {
                using (var dlg = _services.Dialogs.ShowProgress("正在迁移", "正在迁移 " + _data.Name + " …"))
                {
                    var progress = new Progress<string>(dlg.AppendLog);

                    for (int attempt = 0; attempt < 6; attempt++)
                    {
                        MigrationResult result = await _services.Migrator.MigrateAsync(
                            _data.FullPath, destRoot, progress, dlg.Token, dlg.SetProgress);
                        dlg.AppendLog(result.Message);

                        if (result.Success)
                        {
                            MarkMigrated(result);
                            break;
                        }
                        if (result.Cancelled)
                        {
                            break;
                        }

                        // 有占用进程：提示用户结束并重试
                        if (result.Lockers.Count > 0 && attempt < 2)
                        {
                            string msg = "检测到以下进程可能占用目录，结束它们并重试？\n\n"
                                + LockFinder.Describe(result.Lockers)
                                + "\n\n（不会结束系统关键进程与 FC 自身）";
                            if (_services.Dialogs.Confirm("结束占用进程并重试", msg))
                            {
                                dlg.AppendLog("正在结束占用进程…");
                                var errors = LockFinder.Kill(result.Lockers);
                                if (errors.Count > 0)
                                {
                                    dlg.AppendLog("部分进程结束失败：" + string.Join("；", errors));
                                    _services.Dialogs.ShowError("结束进程失败", string.Join("\n", errors));
                                    await TryOfferRollbackAsync(dlg, progress, destFolder, result.Message);
                                    break;
                                }
                                dlg.AppendLog("已结束占用进程，自动重试…");
                                await Task.Delay(800);
                                continue;
                            }
                            await TryOfferRollbackAsync(dlg, progress, destFolder, result.Message);
                            break; // 用户拒绝结束 → 结束流程
                        }

                        // 无占用进程的失败：普通权限下提供"一键以管理员身份重启重试"
                        if (!ElevatedRunner.IsAdministrator())
                        {
                            string ask = result.Message + "\n\n本次失败可能与权限有关。\n要以管理员身份重新启动 FC 并自动重试这次迁移吗？";
                            if (_services.Dialogs.Confirm("需要管理员权限？", ask))
                            {
                                ElevatedRunner.RelaunchAsAdmin(
                                    "--migrate \"" + _data.FullPath + "\" \"" + destRoot + "\"");
                                break;
                            }
                        }

                        // 已自动回退成功，原地提供"重试"（先让用户去关闭占用程序/守护进程）
                        if (attempt >= 5)
                        {
                            _services.Dialogs.ShowError("迁移失败", result.Message);
                            break;
                        }
                        string retryAsk = result.Message + "\n\n已自动回退完成（数据已放回原位置）。\n"
                            + "若目录被程序占用（如 .workbuddy 的守护进程），请先关闭该程序/服务，再点“是”继续重试；点“否”放弃。";
                        if (!_services.Dialogs.Confirm("重试或放弃", retryAsk))
                        {
                            break;
                        }
                        continue;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // 用户取消（对话框自身会把取消写进日志）
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("迁移失败", ex.Message);
            }
            finally
            {
                _busy = false;
                RaiseCommands();
            }
        }

        /// <summary>通过环境变量迁移（打开操作窗，用户在窗内选新目录/作用域后执行）。</summary>
        public async Task MigrateViaEnvAsync()
        {
            if (_busy || _data == null || !_data.IsFolder)
            {
                return;
            }
            var rule = EnvRule;
            if (rule == null || rule.Kind != RedirectKind.EnvVar)
            {
                _services.Dialogs.ShowError("无法迁移",
                    "该目录未命中“通过环境变量迁移”的规则。\n（只能对环境变量可重定位的软件数据目录使用此功能。）");
                return;
            }
            if (JunctionUtil.IsReparsePoint(_data.FullPath))
            {
                _services.Dialogs.ShowError("无法迁移", "源目录已是联接（junction），不能再次迁移。");
                return;
            }

            var match = new EnvVarMatch(rule, _data.FullPath);
            var win = new Views.EnvMigrateWindow(_services, match);
            win.Owner = System.Windows.Application.Current?.MainWindow;
            win.ShowDialog();
            // 迁移成功后：标记本节点为"已迁移"，刷新标注
            if (win.Migrated)
            {
                _migratedDest = win.MigratedDestPath;
                // 数据已搬走。若源位置留了 junction，标记为联接（DetailText 会显示 → 目标）；
                // 若删除，也置为已迁移态。
                if (JunctionUtil.IsReparsePoint(_data.FullPath))
                {
                    _data.IsReparsePoint = true;
                    _data.Error = null; // DetailText 走 junction 分支
                }
                else
                {
                    _data.IsReparsePoint = false;
                    _data.Error = "已迁移（环境变量）";
                }
                NotifyDisplayChanged();
                RaiseCommands();
            }
            else
            {
                NotifyDisplayChanged();
                RaiseCommands();
            }
        }

        /// <summary>迁移中途失败且用户放弃杀进程时，询问是否把数据自动放回原位置。</summary>
        private async Task TryOfferRollbackAsync(
            IProgressDialog dlg, IProgress<string> progress, string destFolder, string reason)
        {
            if (_data == null)
            {
                return;
            }
            string ask = reason + "\n\n数据已复制到目标，但迁移未完成。\n要把数据放回原位置（并清理目标副本）吗？";
            if (!_services.Dialogs.Confirm("自动回退", ask))
            {
                return;
            }
            dlg.AppendLog("正在回退：把数据放回原位置…");
            MigrationResult rb = await _services.Migrator.RollbackAsync(
                _data.FullPath, destFolder, progress, dlg.Token);
            dlg.AppendLog(rb.Message);
            if (!rb.Success && !rb.Cancelled)
            {
                _services.Dialogs.ShowError("回退未完成", rb.Message);
            }
        }

        /// <summary>迁移成功后免重扫：把本节点标记为联接并套用结果大小。</summary>
        private void MarkMigrated(MigrationResult result)
        {
            _filesRequested = true;
            _filesLoaded = true;
            Children.Clear();

            _data.IsReparsePoint = true;
            _data.Error = "已迁移为联接";
            _data.Children = new List<DiskNode>();
            _data.DirectFiles = new List<FileEntry>();
            _data.DirectFileCount = 0;
            if (result.Bytes > 0)
            {
                _data.TotalSize = result.Bytes;
                _data.DirectSize = result.Bytes;
                _data.TotalAllocated = result.Bytes;
                _data.FileCount = result.Files;
            }
            NotifyDisplayChanged();
            RaiseCommands();
        }

        // ==================== 右键菜单 ====================

        private void OpenInExplorer()
        {
            if (_data == null)
            {
                return;
            }
            try
            {
                // 无父路径（如驱动器根）→ 直接打开本身
                string parent = System.IO.Path.GetDirectoryName(_data.FullPath);
                if (string.IsNullOrEmpty(parent))
                {
                    OpenFolder(_data.FullPath);
                    return;
                }
                // 目录、文件统一：打开所在文件夹并高亮选中目标
                if (!OpenContainingSelect(_data.FullPath))
                {
                    OpenFolder(parent);
                }
            }
            catch (Exception)
            {
                try
                {
                    OpenFolder(System.IO.Path.GetDirectoryName(_data.FullPath));
                }
                catch (Exception)
                {
                }
            }
        }

        private static void OpenFolder(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }
            // Shell 方式打开目录：交给资源管理器（已有实例也会正确委托打开，不再丢参数）
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true
            });
        }

        /// <summary>用 SHOpenFolderAndSelectItems 打开所在目录并选中目标（目录/文件通用，P/Invoke 稳定）。</summary>
        private static bool OpenContainingSelect(string targetPath)
        {
            try
            {
                string parent = System.IO.Path.GetDirectoryName(targetPath);
                if (string.IsNullOrEmpty(parent) || !Directory.Exists(parent))
                {
                    return false;
                }
                bool exists = File.Exists(targetPath) || Directory.Exists(targetPath);
                if (!exists)
                {
                    return false;
                }

                IntPtr pidlParent;
                uint sfgao;
                if (SHParseDisplayName(parent, IntPtr.Zero, out pidlParent, 0, out sfgao) != 0 || pidlParent == IntPtr.Zero)
                {
                    return false;
                }
                IntPtr pidlItem;
                if (SHParseDisplayName(targetPath, IntPtr.Zero, out pidlItem, 0, out sfgao) != 0 || pidlItem == IntPtr.Zero)
                {
                    CoTaskMemFree(pidlParent);
                    return false;
                }
                try
                {
                    int hr = SHOpenFolderAndSelectItems(pidlParent, 1, new[] { pidlItem }, 0);
                    return hr == 0;
                }
                finally
                {
                    CoTaskMemFree(pidlParent);
                    CoTaskMemFree(pidlItem);
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern int SHParseDisplayName(
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPWStr)] string pszName,
            IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern int SHOpenFolderAndSelectItems(
            IntPtr pidlFolder, uint cidl,
            [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPArray, SizeParamIndex = 1)]
            System.IntPtr[] apidl, uint dwFlags);

        [System.Runtime.InteropServices.DllImport("shell32.dll")]
        private static extern void CoTaskMemFree(IntPtr pv);

        private void CopyPath()
        {
            if (_data == null)
            {
                return;
            }
            try
            {
                System.Windows.Clipboard.SetText(_data.FullPath);
            }
            catch (Exception)
            {
            }
        }

        private void Open()
        {
            if (_data == null)
            {
                return;
            }
            try
            {
                System.Diagnostics.Process.Start(_data.FullPath);
            }
            catch (Exception)
            {
            }
        }

        // ==================== 刷新 ====================

        /// <summary>重新深度扫描本节点目录（右键"刷新此目录"用）。</summary>
        public async Task RefreshAsync()
        {
            if (_data == null)
            {
                return;
            }
            string path = _data.FullPath;

            var result = await _services.Scanner.ScanAsync(path, null, CancellationToken.None);
            List<DiskNode> children = result.Children;
            bool isJunction = JunctionUtil.IsJunction(path);

            await _dispatcher.InvokeAsync(() =>
            {
                _icon = null;
                _filesRequested = false;
                _filesLoaded = false;
                _data = new DiskNode
                {
                    FullPath = path,
                    Name = Path.GetFileName(path),
                    IsFolder = true,
                    IsReparsePoint = isJunction,
                    Children = children
                };
                if (isJunction)
                {
                    _data.Error = "已迁移为联接";
                }

                long direct = isJunction ? 0 : Scanner.GetDirectBytes(path);
                _data.DirectSize = direct;
                _data.TotalSize = direct;
                _data.FileCount = 0;
                foreach (var c in _data.Children)
                {
                    _data.TotalSize += c.TotalSize;
                    _data.FileCount += c.FileCount;
                }

                Children.Clear();
                if (HasAnyChildren(_data))
                {
                    Children.Add(Dummy);
                }

                NotifyDisplayChanged();
                OnPropertyChanged("HeaderText");
            });
        }

        private void RaiseCommands()
        {
            MigrateCommand.RaiseCanExecuteChanged();
            RefreshCommand.RaiseCanExecuteChanged();
            OpenExplorerCommand.RaiseCanExecuteChanged();
            CopyPathCommand.RaiseCanExecuteChanged();
            OpenCommand.RaiseCanExecuteChanged();
        }
    }
}