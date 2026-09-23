using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using FC.Converters;
using FC.Models;
using FC.Services;
using FC.Themes;
using FC.Views;

namespace FC.ViewModels
{
    /// <summary>
    /// 主界面 VM（TreeSize 式）：
    /// - 不自动扫描，等待用户选择盘符/目录后开始；
    /// - 单遍并行扫描，流式出树：根节点统计行 + 第一层目录固定展开，子目录默认折叠；
    /// - 每行进度条随计算增长（计时器节流刷新已展开行）；完成后按大小降序并保持展开状态；
    /// - 表头排序、单位切换、展开/折叠全部、列显隐、深浅主题。
    /// 迁移入口在 TreeNodeViewModel 右键菜单；历史记录窗口从这里打开。
    /// </summary>
    public class MainViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly DispatcherTimer _progressTimer;
        private CancellationTokenSource _scanCts;
        private DiskNode _rootNode;
        private bool _isScanning;
        private string _selectedDrive;
        private string _statusText;
        private string _sortColumnKey = "size";
        private bool _sortDescending = true;
        private string _unitMode = "自动";
        private bool _showSizeColumn = true;
        private bool _showAllocatedColumn = true;
        private bool _showFilesColumn = true;
        private bool _showFoldersColumn = true;
        private bool _showPercentColumn = true;
        private string _volumeStatusText = "";
        private string _filesStatusText = "";
        private string _excludedStatusText = "";
        private bool _quickReuse;

        public MainViewModel(AppServices services)
        {
            _services = services;

            Drives = new ObservableCollection<string>();
            Roots = new ObservableCollection<TreeNodeViewModel>();

            ScanCommand = new RelayCommand(async () => await ScanDriveAsync(),
                () => !_isScanning && !string.IsNullOrEmpty(_selectedDrive));
            SelectFolderCommand = new RelayCommand(async () => await ScanFolderAsync(), () => !_isScanning);
            CancelScanCommand = new RelayCommand(CancelScan, () => _isScanning);
            ShowHistoryCommand = new RelayCommand(ShowHistory, () => !_isScanning);
            SortCommand = new RelayCommand(o => Sort(o as string));
            ExpandAllCommand = new RelayCommand(() => SetAllExpanded(true));
            CollapseAllCommand = new RelayCommand(() => SetAllExpanded(false));
            ThemeToggleCommand = new RelayCommand(ToggleTheme);
            OpenCleanupCommand = new RelayCommand(OpenCleanup, () => !_isScanning);
            ShowLargeFilesCommand = new RelayCommand(ShowLargeFiles, () => !_isScanning);
            ShowTypeStatsCommand = new RelayCommand(ShowTypeStats, () => !_isScanning);
            ShowDeltaCommand = new RelayCommand(ShowDelta, () => !_isScanning);

            // 扫描期每行进度条刷新（250ms 节流，避免每事件全量刷新）
            _progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _progressTimer.Tick += (s, e) => RefreshProgressRows();

            var settings = ThemeManager.CurrentSettings;
            SizeText.Mode = ParseUnitMode(settings != null ? settings.UnitMode : null);
            _unitMode = SizeText.Mode == SizeUnitMode.Auto ? "自动" : SizeText.Mode.ToString();
            if (settings != null)
            {
                _showSizeColumn = settings.ShowSizeColumn;
                _showAllocatedColumn = settings.ShowAllocatedColumn;
                _showFilesColumn = settings.ShowFilesColumn;
                _showFoldersColumn = settings.ShowFoldersColumn;
                _showPercentColumn = settings.ShowPercentColumn;
                _quickReuse = settings.QuickReuse;
            }

            _statusText = "就绪。选择盘符后点“扫描”，或点“选择目录”分析任意文件夹。";
        }

        public ObservableCollection<string> Drives { get; private set; }

        /// <summary>单位下拉选项</summary>
        public string[] Units
        {
            get { return new[] { "自动", "GB", "MB", "KB" }; }
        }

        public string SelectedDrive
        {
            get { return _selectedDrive; }
            set { Set(ref _selectedDrive, value); }
        }

        public ObservableCollection<TreeNodeViewModel> Roots { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }

        public bool IsScanning
        {
            get { return _isScanning; }
            set
            {
                if (Set(ref _isScanning, value))
                {
                    RaiseCommands();
                }
            }
        }

        // ---------- 单位 ----------
        public string UnitMode
        {
            get { return _unitMode; }
            set
            {
                if (Set(ref _unitMode, value))
                {
                    SizeText.Mode = ParseUnitMode(value);
                    RefreshAllDisplay();
                    SaveSettings();
                }
            }
        }

        // ---------- 列显隐（Sort 等下方） ----------
        /// <summary>快速复用上次扫描缓存（勾选则未变目录免重算）</summary>
        public bool QuickReuse
        {
            get { return _quickReuse; }
            set
            {
                if (Set(ref _quickReuse, value))
                {
                    SaveSettings();
                }
            }
        }

        // ---------- 排序 ----------
        public string SortColumnKey
        {
            get { return _sortColumnKey; }
            set { Set(ref _sortColumnKey, value); }
        }

        public bool SortDescending
        {
            get { return _sortDescending; }
            set { Set(ref _sortDescending, value); }
        }

        // ---------- 列显隐 ----------
        public bool ShowSizeColumn
        {
            get { return _showSizeColumn; }
            set
            {
                if (Set(ref _showSizeColumn, value))
                {
                    SaveSettings();
                }
            }
        }

        public bool ShowAllocatedColumn
        {
            get { return _showAllocatedColumn; }
            set
            {
                if (Set(ref _showAllocatedColumn, value))
                {
                    SaveSettings();
                }
            }
        }

        public bool ShowFilesColumn
        {
            get { return _showFilesColumn; }
            set
            {
                if (Set(ref _showFilesColumn, value))
                {
                    SaveSettings();
                }
            }
        }

        public bool ShowFoldersColumn
        {
            get { return _showFoldersColumn; }
            set
            {
                if (Set(ref _showFoldersColumn, value))
                {
                    SaveSettings();
                }
            }
        }

        public bool ShowPercentColumn
        {
            get { return _showPercentColumn; }
            set
            {
                if (Set(ref _showPercentColumn, value))
                {
                    SaveSettings();
                }
            }
        }

        // ---------- 状态栏 ----------
        public string VolumeStatusText
        {
            get { return _volumeStatusText; }
            set { Set(ref _volumeStatusText, value); }
        }

        public string FilesStatusText
        {
            get { return _filesStatusText; }
            set { Set(ref _filesStatusText, value); }
        }

        public string ExcludedStatusText
        {
            get { return _excludedStatusText; }
            set { Set(ref _excludedStatusText, value); }
        }

        public string ThemeToggleText
        {
            get { return ThemeManager.IsDark ? "☀ 浅色" : "☾ 深色"; }
        }

        public RelayCommand ScanCommand { get; private set; }

        public RelayCommand SelectFolderCommand { get; private set; }

        public RelayCommand CancelScanCommand { get; private set; }

        public RelayCommand ShowHistoryCommand { get; private set; }

        public RelayCommand SortCommand { get; private set; }

        public RelayCommand ExpandAllCommand { get; private set; }

        public RelayCommand CollapseAllCommand { get; private set; }

        public RelayCommand ThemeToggleCommand { get; private set; }

        public RelayCommand OpenCleanupCommand { get; private set; }

        public RelayCommand ShowLargeFilesCommand { get; private set; }

        public RelayCommand ShowTypeStatsCommand { get; private set; }

        public RelayCommand ShowDeltaCommand { get; private set; }

        /// <summary>最近一次完整扫描的结果（大文件/类型统计数据源）</summary>
        public ScanResult LastResult { get; private set; }

        /// <summary>启动初始化：只发现盘符并选中默认，不自动扫描。</summary>
        public async Task InitializeAsync()
        {
            DiscoverDrives();

            if (Drives.Count == 0)
            {
                StatusText = "未发现可用盘符。";
                return;
            }

            if (string.IsNullOrEmpty(_selectedDrive))
            {
                bool hasDefault = Drives.Any(d => string.Equals(d, "C:\\", StringComparison.OrdinalIgnoreCase));
                SelectedDrive = hasDefault ? "C:\\" : Drives[0];
            }

            StatusText = "就绪。选择盘符后点“扫描”，或点“选择目录”分析任意文件夹。";
            await Task.CompletedTask;
        }

        private void DiscoverDrives()
        {
            Drives.Clear();
            try
            {
                foreach (var d in System.IO.DriveInfo.GetDrives())
                {
                    Drives.Add(d.RootDirectory.FullName);
                }
            }
            catch (Exception)
            {
                // 个别盘符读取失败不影响
            }
        }

        public async Task ScanDriveAsync()
        {
            await ScanTargetAsync(_selectedDrive);
        }

        private async Task ScanFolderAsync()
        {
            string initial = string.IsNullOrEmpty(_selectedDrive) ? null : _selectedDrive;
            string folder;
            try
            {
                folder = _services.Dialogs.PickFolder("选择要分析的目录", initial, out var cancelled);
                if (cancelled || string.IsNullOrEmpty(folder))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("选择目录失败", ex.Message);
                return;
            }

            await ScanTargetAsync(folder);
        }

        private void CancelScan()
        {
            if (_scanCts != null)
            {
                _scanCts.Cancel();
                StatusText = "正在停止…（等待当前目录统计结束）";
            }
        }

        private async Task ScanTargetAsync(string target)
        {
            if (_isScanning || string.IsNullOrEmpty(target))
            {
                return;
            }
            try
            {
                if (!Directory.Exists(target))
                {
                    StatusText = "目标不存在：" + target;
                    return;
                }
            }
            catch (Exception ex)
            {
                StatusText = "无法访问目标：" + ex.Message;
                return;
            }

            if (_scanCts != null)
            {
                _scanCts.Dispose();
                _scanCts = null;
            }
            _scanCts = new CancellationTokenSource();
            CancellationToken ct = _scanCts.Token;

            // 应用快速复用开关到扫描器
            var scannerImpl = _services.Scanner as FC.Services.Scanner;
            if (scannerImpl != null)
            {
                scannerImpl.QuickReuseMode = _quickReuse;
            }

            // 卷信息
            long free, total;
            if (VolumeInfo.GetVolumeStats(target, out free, out total))
            {
                VolumeStatusText = string.Format("可用 {0}  |  总容量 {1}", SizeText.Format(free), SizeText.Format(total));
            }
            else
            {
                VolumeStatusText = "可用/总容量未知";
            }
            FilesStatusText = "";
            ExcludedStatusText = "";

            IsScanning = true;
            Roots.Clear();
            NodeRegistry.Clear();
            _rootNode = null;
            TreeNodeViewModel.SizesComplete = false;
            StatusText = "正在读取目录结构…";

            try
            {
                var progress = new Progress<ScanProgress>(OnScanProgress);
                var result = await _services.Scanner.ScanAsync(target, progress, ct);
                LastResult = result;
                SaveDeltaBase(result);

                _progressTimer.Stop();
                TreeNodeViewModel.SizesComplete = true;
                _rootNode = result.RootNode;
                SetRoot(_rootNode);

                SortColumnKey = "size";
                SortDescending = true;

                FilesStatusText = string.Format("文件 {0}", SizeText.FormatCount(result.TotalFiles));
                ExcludedStatusText = string.Format("已排除 {0}", SizeText.FormatCount(result.ExcludedCount));
                StatusText = string.Format("扫描完成：{0} 个目录 / {1} 个文件 / {2}",
                    result.TotalFolders, SizeText.FormatCount(result.TotalFiles), SizeText.Format(result.TotalBytes));
                if (result.ReusedCount > 0)
                {
                    StatusText += string.Format("（复用缓存 {0} 目录）", SizeText.FormatCount(result.ReusedCount));
                }
            }
            catch (OperationCanceledException)
            {
                _progressTimer.Stop();
                TreeNodeViewModel.SizesComplete = true;
                Roots.Clear();
                _rootNode = null;
                LastResult = null;
                StatusText = "扫描已取消。";
            }
            catch (Exception ex)
            {
                _progressTimer.Stop();
                LastResult = null;
                StatusText = "扫描失败：" + ex.Message;
            }
            finally
            {
                if (_scanCts != null)
                {
                    _scanCts.Dispose();
                    _scanCts = null;
                }
                IsScanning = false;
            }
        }

        private void OnScanProgress(ScanProgress p)
        {
            if (p.RootReady != null)
            {
                // 根节点枚举完成：铺"根统计行 + 第一层目录"，根行固定展开，子目录默认折叠
                _rootNode = p.RootReady;
                SetRoot(p.RootReady);
                _progressTimer.Start();
                StatusText = "目录结构已就绪，正在计算大小…";
                ExcludedStatusText = string.Format("已排除 {0}", SizeText.FormatCount(p.ExcludedCount));
            }
            else if (p.EnumeratedNode != null)
            {
                // 某目录枚举/聚合完成：若其 VM 已展开可见，刷新占位/文件/大小
                var vm = NodeRegistry.Find(p.EnumeratedNode.FullPath);
                if (vm != null)
                {
                    vm.ResyncFromData();
                }
                UpdateProgressText(p);
            }
            else if (p.NewTopNode != null)
            {
                var vm = NodeRegistry.Find(p.NewTopNode.FullPath);
                if (vm != null)
                {
                    vm.ResyncFromData();
                }
                UpdateProgressText(p);
            }
            else if (p.Phase == ScanPhase.Sizing)
            {
                UpdateProgressText(p);
            }
        }

        private void UpdateProgressText(ScanProgress p)
        {
            ExcludedStatusText = string.Format("已排除 {0}", SizeText.FormatCount(p.ExcludedCount));
            StatusText = string.Format("计算大小中… 已统计 {0} 目录，累计 {1}",
                p.CompletedFolders, SizeText.Format(p.CompletedBytes));
        }

        /// <summary>设置根统计行（树的顶端节点，固定展开显示第一层目录）。</summary>
        private void SetRoot(DiskNode root)
        {
            Roots.Clear();
            if (root == null)
            {
                return;
            }
            var vm = new TreeNodeViewModel(root, _services, root.TotalSize);
            Roots.Add(vm);
            vm.IsExpanded = true; // 展开根 → 显示第一层目录，更深层默认折叠
        }

        /// <summary>扫描期间每 250ms：刷新已展开行大小/进度条，并对已展开目录做实时从大到小排序。</summary>
        private void RefreshProgressRows()
        {
            var rows = NodeRegistry.GetAll();
            if (rows.Count > 1500)
            {
                // 展开过多的极端场景：只刷根，避免卡顿
                foreach (var r in Roots)
                {
                    r.NotifyProgress();
                }
                return;
            }

            foreach (var vm in rows)
            {
                vm.NotifyProgress();
                vm.ApplyLiveSort();
            }
        }

        // ==================== 排序 ====================

        public void Sort(string key)
        {
            if (_isScanning)
            {
                return; // 扫描中数据在变，排序等扫描结束
            }
            if (string.IsNullOrEmpty(key) || _rootNode == null)
            {
                return;
            }

            if (key == _sortColumnKey)
            {
                _sortDescending = !_sortDescending;
            }
            else
            {
                _sortColumnKey = key;
                _sortDescending = true; // 换列默认降序（大在前）
            }

            var cmp = BuildComparer(_sortColumnKey, _sortDescending);
            SortNode(_rootNode, cmp);

            foreach (var r in Roots)
            {
                r.ResyncFromData();
            }

            OnPropertyChanged("SortColumnKey");
            OnPropertyChanged("SortDescending");
        }

        private static void SortNode(DiskNode node, Comparison<DiskNode> cmp)
        {
            if (node.Children.Count > 1)
            {
                node.Children.Sort(cmp);
            }
            foreach (var c in node.Children)
            {
                SortNode(c, cmp);
            }
        }

        private static Comparison<DiskNode> BuildComparer(string key, bool descending)
        {
            Comparison<DiskNode> cmp;
            switch (key)
            {
                case "name":
                    cmp = (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                    break;
                case "allocated":
                    cmp = (a, b) => a.TotalAllocated.CompareTo(b.TotalAllocated);
                    break;
                case "files":
                    cmp = (a, b) => a.FileCount.CompareTo(b.FileCount);
                    break;
                case "folders":
                    cmp = (a, b) => a.Children.Count.CompareTo(b.Children.Count);
                    break;
                case "modified":
                    cmp = (a, b) => (a.LastWriteTime ?? DateTime.MinValue).CompareTo(b.LastWriteTime ?? DateTime.MinValue);
                    break;
                default: // size / percent（分母相同，等价于按大小）
                    cmp = (a, b) => a.TotalSize.CompareTo(b.TotalSize);
                    break;
            }

            if (descending)
            {
                return (a, b) => -cmp(a, b);
            }
            return cmp;
        }

        // ==================== 展开/折叠全部 ====================

        private void SetAllExpanded(bool expanded)
        {
            foreach (var r in Roots)
            {
                SetExpandedRecursive(r, expanded);
            }
        }

        private static void SetExpandedRecursive(TreeNodeViewModel v, bool expanded)
        {
            v.IsExpanded = expanded;
            foreach (var c in v.Children)
            {
                SetExpandedRecursive(c, expanded);
            }
        }

        // ==================== 主题/单位 ====================

        private void ToggleTheme()
        {
            ThemeManager.Toggle();
            OnPropertyChanged("ThemeToggleText");
            SaveSettings();
        }

        private static SizeUnitMode ParseUnitMode(string mode)
        {
            SizeUnitMode parsed;
            return Enum.TryParse(mode, true, out parsed) ? parsed : SizeUnitMode.Auto;
        }

        private void SaveSettings()
        {
            try
            {
                var s = ThemeManager.CurrentSettings;
                if (s == null)
                {
                    return;
                }
                s.UnitMode = _unitMode;
                s.ShowSizeColumn = _showSizeColumn;
                s.ShowAllocatedColumn = _showAllocatedColumn;
                s.ShowFilesColumn = _showFilesColumn;
                s.ShowFoldersColumn = _showFoldersColumn;
                s.ShowPercentColumn = _showPercentColumn;
                s.QuickReuse = _quickReuse;
                ThemeManager.SaveSettings();
            }
            catch (Exception)
            {
            }
        }

        // ==================== 显示刷新 ====================

        /// <summary>单位切换/排序后刷新所有可见（含已展开子级）节点的显示属性。</summary>
        public void RefreshAllDisplay()
        {
            foreach (var r in Roots)
            {
                RefreshNodeDisplayRecursive(r);
            }
        }

        private static void RefreshNodeDisplayRecursive(TreeNodeViewModel v)
        {
            v.NotifyDisplayChanged();
            foreach (var c in v.Children)
            {
                RefreshNodeDisplayRecursive(c);
            }
        }

        // ==================== 其他 ====================

        private void ShowHistory()
        {
            var win = new HistoryWindow(_services);
            win.Owner = Application.Current != null ? Application.Current.MainWindow : null;
            win.ShowDialog();
        }

        /// <summary>清理建议窗口（不依赖扫描结果，随时可开）。</summary>
        private void OpenCleanup()
        {
            var win = new CleanupWindow(new CleanupViewModel(_services));
            win.Owner = Application.Current != null ? Application.Current.MainWindow : null;
            win.ShowDialog();
        }

        /// <summary>全局最大文件窗口（数据来自最近一次完整扫描）。</summary>
        private void ShowLargeFiles()
        {
            if (LastResult == null)
            {
                _services.Dialogs.ShowError("还没有数据", "请先扫描一个盘符/目录，再查看“最大文件”。");
                return;
            }
            var vm = new LargeFilesViewModel(LastResult.LargeFiles, LastResult.LargeFilesComplete);
            var win = new LargeFilesWindow(vm);
            win.Owner = Application.Current != null ? Application.Current.MainWindow : null;
            win.ShowDialog();
        }

        /// <summary>类型统计窗口（数据来自最近一次完整扫描）。</summary>
        private void ShowTypeStats()
        {
            if (LastResult == null)
            {
                _services.Dialogs.ShowError("还没有数据", "请先扫描一个盘符/目录，再查看“类型统计”。");
                return;
            }
            long total = LastResult.RootNode != null ? LastResult.RootNode.TotalSize : 0;
            var vm = new TypeStatsViewModel(LastResult.TypeStats, total);
            var win = new TypeStatsWindow(vm);
            win.Owner = Application.Current != null ? Application.Current.MainWindow : null;
            win.ShowDialog();
        }

        /// <summary>增量对比窗口（对比最近两次扫描的快照）。</summary>
        private void ShowDelta()
        {
            if (LastResult == null || LastResult.RootNode == null)
            {
                _services.Dialogs.ShowError("还没有数据", "请先扫描一个盘符/目录，再查看“增量对比”。");
                return;
            }
            var vm = new DeltaViewModel(LastResult.RootNode);
            var win = new DeltaWindow(vm);
            win.Owner = Application.Current != null ? Application.Current.MainWindow : null;
            win.ShowDialog();
        }

        /// <summary>扫描完成后保存增量基准快照（后台写文件，不阻塞 UI）。</summary>
        private static void SaveDeltaBase(ScanResult result)
        {
            try
            {
                if (result == null || result.RootNode == null)
                {
                    return;
                }
                string file = SnapshotDiff.SnapshotFilePath(result.RootNode.FullPath);
                Task.Run(() => ScanSnapshot.Save(file, ScanSnapshot.Capture(result.RootNode)));
            }
            catch (Exception)
            {
            }
        }

        private void RaiseCommands()
        {
            ScanCommand.RaiseCanExecuteChanged();
            SelectFolderCommand.RaiseCanExecuteChanged();
            CancelScanCommand.RaiseCanExecuteChanged();
            ShowHistoryCommand.RaiseCanExecuteChanged();
            OpenCleanupCommand.RaiseCanExecuteChanged();
            ShowLargeFilesCommand.RaiseCanExecuteChanged();
            ShowTypeStatsCommand.RaiseCanExecuteChanged();
            ShowDeltaCommand.RaiseCanExecuteChanged();
        }
    }
}