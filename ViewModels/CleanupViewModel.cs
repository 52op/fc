using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using FC.Services;

namespace FC.ViewModels
{
    /// <summary>
    /// 「清理建议」窗口 VM：列可清理项 → 后台并行估算占用 → 勾选 → 真实执行清理。
    /// 安全项默认勾选；谨慎/危险项默认不勾；危险项执行前单独二次确认。
    /// </summary>
    public class CleanupViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private CancellationTokenSource _cts;
        private string _statusText;
        private bool _busy;
        private long _selectedBytes = -1;
        private string _systemFilesText;
        private string _winSxSInfo;
        private bool _winSxSDone;
        private bool _cleaning;
        private string _cleaningTitle;
        private int _cleaningStep;
        private int _cleaningTotal;
        private int _cleaningDeleted;
        private bool _keepStatusAfterRefresh;

        public CleanupViewModel(AppServices services)
        {
            _services = services;
            Items = new ObservableCollection<CleanupItem>(CleanupService.BuildItems());
            Logs = new ObservableCollection<string>();

            RefreshCommand = new RelayCommand(async () => await RefreshSizesAsync());
            CleanCommand = new RelayCommand(async () => await CleanAsync(),
                () => !_busy && Items.Any(i => i.Selected));
            CancelCommand = new RelayCommand(Cancel, () => _busy);
            OpenVirtualMemoryCommand = new RelayCommand(SystemAudit.OpenVirtualMemorySettings);
            OpenDiskCleanupCommand = new RelayCommand(() => SystemAudit.OpenDiskCleanup());
            DisableHibernateCommand = new RelayCommand(async () => await DisableHibernateAsync());
            RunDismCommand = new RelayCommand(async () => await RunDismAsync());

            // A2：系统特殊大文件审计（同步快）
            RefreshSystemFiles();
            // C8：WinSxS 大小（递归，后台算，算完刷新绑定）
            _winSxSInfo = "组件存储（WinSxS）统计中…";
            Task.Run(() => SystemAudit.GetWinSxSSize()).ContinueWith((Task<long> t) =>
            {
                if (!t.IsFaulted)
                {
                    _winSxSInfo = string.Format("组件存储（WinSxS）≈ {0}，可用 DISM 清理失效组件以减少占用",
                        FC.Converters.SizeText.Format(t.Result));
                    _winSxSDone = true;
                    OnPropertyChanged("WinSxSInfo");
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());

            StatusText = "正在统计可清理空间…";
            BeginSizeRefresh();
        }

        public ObservableCollection<CleanupItem> Items { get; private set; }

        public ObservableCollection<string> Logs { get; private set; }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }

        public bool IsBusy
        {
            get { return _busy; }
            set
            {
                if (Set(ref _busy, value))
                {
                    RefreshCommand.RaiseCanExecuteChanged();
                    CleanCommand.RaiseCanExecuteChanged();
                    CancelCommand.RaiseCanExecuteChanged();
                }
            }
        }

        /// <summary>是否正在清理（驱动不确定进度条与按钮状态）。</summary>
        public bool IsCleaning
        {
            get { return _cleaning; }
            private set
            {
                if (Set(ref _cleaning, value))
                {
                    OnPropertyChanged("CleanButtonText");
                }
            }
        }

        /// <summary>清理按钮文案：清理中显示"清理中…"。</summary>
        public string CleanButtonText
        {
            get { return _cleaning ? "清理中…" : "清理所选"; }
        }

        /// <summary>所选项目前估算的可释放空间（-1 = 尚未算完）。</summary>
        public long SelectedBytes
        {
            get { return _selectedBytes; }
            private set
            {
                if (Set(ref _selectedBytes, value))
                {
                    RaiseSelectedSummary();
                }
            }
        }

        public RelayCommand RefreshCommand { get; private set; }

        public RelayCommand CleanCommand { get; private set; }

        public RelayCommand CancelCommand { get; private set; }

        public RelayCommand OpenVirtualMemoryCommand { get; private set; }

        public RelayCommand OpenDiskCleanupCommand { get; private set; }

        public RelayCommand DisableHibernateCommand { get; private set; }

        public RelayCommand RunDismCommand { get; private set; }

        /// <summary>系统盘特殊大文件审计文本（hiberfil/pagefile/swapfile 大小与建议）。</summary>
        public string SystemFilesText
        {
            get { return _systemFilesText; }
            set { Set(ref _systemFilesText, value); }
        }

        public bool HasSystemFiles
        {
            get { return !string.IsNullOrEmpty(_systemFilesText); }
        }

        /// <summary>WinSxS 组件存储提示文本。</summary>
        public string WinSxSInfo
        {
            get { return _winSxSInfo; }
            set { Set(ref _winSxSInfo, value); }
        }

        public bool WinSxSDone
        {
            get { return _winSxSDone; }
        }

        private void RefreshSystemFiles()
        {
            var parts = new List<string>();
            foreach (var f in SystemAudit.ListSystemFiles())
            {
                string hint;
                if (string.Equals(f.Name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase))
                {
                    hint = "休眠文件（≈内存大小）。点下方按钮“关闭休眠”可删除并释放";
                }
                else if (string.Equals(f.Name, "pagefile.sys", StringComparison.OrdinalIgnoreCase))
                {
                    hint = "虚拟内存页文件。点下方按钮“打开虚拟内存设置”可调节";
                }
                else if (string.Equals(f.Name, "swapfile.sys", StringComparison.OrdinalIgnoreCase))
                {
                    hint = "系统交换文件，一般无需手动处理";
                }
                else
                {
                    hint = "系统临时/转储文件";
                }
                parts.Add(f.Name + " = " + FC.Converters.SizeText.Format(f.Bytes) + "（" + hint + "）");
            }
            SystemFilesText = parts.Count > 0 ? string.Join("\n", parts) : "";
        }

        /// <summary>关闭休眠（需管理员，触发 UAC）：删除 hiberfil.sys 并释放空间。</summary>
        private async Task DisableHibernateAsync()
        {
            if (!_services.Dialogs.Confirm("关闭休眠",
                "关闭休眠会删除 hiberfil.sys（约 " + SystemFilesHiberBytesText + "），释放该空间。\n"
                + "注意：将无法使用“休眠”功能（睡眠不受影响）。\n\n"
                + "继续？"))
            {
                return;
            }
            if (!ElevatedRunner.IsAdministrator())
            {
                if (!_services.Dialogs.Confirm("需要管理员",
                    "关闭休眠需要管理员权限。\n将弹出 UAC 确认框，请点“是”。\n\n继续？"))
                {
                    return;
                }
            }
            AppendLog("正在以管理员权限关闭休眠…（如弹出 UAC 请点“是”）");
            StatusText = "正在关闭休眠（需管理员）…";
            CommandResult r = await Task.Run(() =>
                SystemAudit.RunCommandElevated("powercfg.exe", "/hibernate off"));
            if (r.Success)
            {
                AppendLog("已关闭休眠。hiberfil.sys 已被系统删除。");
                StatusText = "已关闭休眠，hiberfil.sys 已删除。";
                RefreshSystemFiles();
            }
            else
            {
                AppendLog("关闭休眠未成功（可能被取消或权限不足）：" + r.Output);
                StatusText = "关闭休眠未成功。";
                _services.Dialogs.ShowError("关闭休眠未成功",
                    r.Cancelled ? "已取消 UAC 确认。" : (string.IsNullOrEmpty(r.Output) ? "权限不足。" : r.Output));
            }
        }

        /// <summary>DISM 清理 WinSxS 失效组件（需管理员，慢，分钟级）。</summary>
        private async Task RunDismAsync()
        {
            if (!_services.Dialogs.Confirm("DISM 组件清理",
                "将执行系统组件清理（WinSxS）：\n" + SystemAudit.DismCommandText() + "\n\n"
                + "该操作需管理员权限，耗时数分钟，期间电脑可能变慢。\n"
                + "完成后可释放数 GB 空间。\n\n继续？"))
            {
                return;
            }
            if (!ElevatedRunner.IsAdministrator())
            {
                if (!_services.Dialogs.Confirm("需要管理员",
                    "DISM 清理需要管理员权限。\n将弹出 UAC 确认框，请点“是”。\n\n继续？"))
                {
                    return;
                }
            }
            IsBusy = true;
            AppendLog("正在执行 DISM 组件清理（需管理员，数分钟）…");
            StatusText = "正在执行 DISM 组件清理…（数分钟，期间可继续浏览）";
            try
            {
                CommandResult r = await Task.Run(() =>
                    SystemAudit.RunCommandElevated("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup"));
                AppendLog(r.Output);
                if (r.Success)
                {
                    StatusText = "DISM 组件清理完成。";
                    _winSxSInfo = "组件存储（WinSxS）已清理完成。";
                    OnPropertyChanged("WinSxSInfo");
                }
                else
                {
                    StatusText = "DISM 清理未成功。";
                    _services.Dialogs.ShowError("DISM 清理未成功",
                        r.Cancelled ? "已取消 UAC 确认。" : (string.IsNullOrEmpty(r.Output) ? "权限不足。" : r.Output));
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>hiberfil.sys 大小文本（供确认提示用）。</summary>
        private string SystemFilesHiberBytesText
        {
            get
            {
                foreach (var f in SystemAudit.ListSystemFiles())
                {
                    if (string.Equals(f.Name, "hiberfil.sys", StringComparison.OrdinalIgnoreCase))
                    {
                        return FC.Converters.SizeText.Format(f.Bytes);
                    }
                }
                return "若干 GB";
            }
        }

        public string SelectedSummary
        {
            get
            {
                int n = Items.Count(i => i.Selected);
                if (_selectedBytes < 0)
                {
                    return string.Format("已选 {0} 项（占用统计中…）", n);
                }
                return string.Format("已选 {0} 项，预计可释放 {1}",
                    n, FC.Converters.SizeText.Format(_selectedBytes));
            }
        }

        private void RaiseSelectedSummary()
        {
            OnPropertyChanged("SelectedSummary");
        }

        /// <summary>启动/刷新大小：后台并行估算，逐项完成即更新绑定（CleanupItem 已 INPC）。</summary>
        private async Task RefreshSizesAsync()
        {
            BeginSizeRefresh();
            try
            {
                await Task.Delay(1);
            }
            catch (Exception)
            {
            }
        }

        private void BeginSizeRefresh()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            SelectedBytes = -1;

            var snapshot = Items.ToList();

            // 关键：Progress<T> 必须在 UI 线程构造（捕获 DispatcherSynchronizationContext），
            // 否则 Report 在后台线程直接调用 handler → 跨线程改 ObservableCollection 崩溃（闪退）。
            var progress = new Progress<string>(AppendLog);
            var dispatch = Application.Current != null ? Application.Current.Dispatcher : null;
            Task.Run(() =>
            {
                CleanupService.RefreshSizes(snapshot, progress, ct,
                    (it, size) => ApplySizedOnUi(dispatch, it, size));
            }).ContinueWith(t =>
            {
                if (t.IsCanceled)
                {
                    StatusText = "大小统计已取消。";
                }
                else if (t.IsFaulted)
                {
                    StatusText = "统计失败：" + (t.Exception != null ? t.Exception.Message : "未知");
                }
                else
                {
                    long total = 0;
                    foreach (var i in snapshot)
                    {
                        if (i.Bytes > 0)
                        {
                            total += i.Bytes;
                        }
                    }
                    // 清理刚完成时不覆盖"清理完成"提示，只刷新占用数值
                    if (!_keepStatusAfterRefresh)
                    {
                        StatusText = string.Format("可清理项共 {0} 项，预计最多可释放 {1}。勾选后点“清理所选”。",
                            snapshot.Count(i => i.Bytes >= 0), FC.Converters.SizeText.Format(total));
                    }
                    SelectedBytes = snapshot.Where(i => i.Selected && i.Bytes > 0).Sum(i => i.Bytes);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>把统计结果应用到 CleanupItem（必须回到 UI 线程，避免跨线程 INPC/绑定崩溃）。</summary>
        private static void ApplySizedOnUi(System.Windows.Threading.Dispatcher dispatch, CleanupItem item, long size)
        {
            if (dispatch == null || dispatch.CheckAccess())
            {
                item.Bytes = size;
                item.IsSized = true;
                return;
            }
            dispatch.Invoke(new Action(() =>
            {
                item.Bytes = size;
                item.IsSized = true;
            }));
        }

        private void Cancel()
        {
            if (_cts != null)
            {
                _cts.Cancel();
            }
        }

        /// <summary>执行清理：确认 → （提示提权）→ 后台真实删除 → 汇报结果。</summary>
        private async Task CleanAsync()
        {
            var selected = Items.Where(i => i.Selected).ToList();
            if (selected.Count == 0)
            {
                return;
            }

            // 危险项二次确认
            var risky = selected.Where(i => i.Safety == CleanupSafety.Risky).ToList();
            if (risky.Count > 0)
            {
                string msg = "以下项目删除后不可恢复：\n\n"
                    + string.Join("\n", risky.Select(r => "• " + r.Title + "（" + r.Description + "）"))
                    + "\n\n确定要删除它们吗？";
                if (!_services.Dialogs.Confirm("危险操作确认", msg))
                {
                    return;
                }
            }

            // 普通确认（合计预计释放）
            string confirm = "即将清理以下内容（清空目录内容，保留目录本身）：\n\n"
                + string.Join("\n", selected.Select(s => "• " + s.Title + " — " + s.SizeText))
                + "\n\n预计释放 " + (SelectedBytes >= 0 ? FC.Converters.SizeText.Format(SelectedBytes) : "未知")
                + "。\n\n继续？";
            if (!_services.Dialogs.Confirm("确认清理", confirm))
            {
                return;
            }

            // 涉及系统目录但非管理员 → 提示（不强制，允许继续尝试）
            if (CleanupService.AnySystemPaths(selected) && !ElevatedRunner.IsAdministrator())
            {
                if (!_services.Dialogs.Confirm("权限提示",
                    "所选项目包含系统目录（如 Windows\\Temp、更新缓存）。\n"
                    + "当前不是管理员，部分项目可能清理失败。\n"
                    + "建议用管理员身份运行 FC 后再试。\n\n仍要继续清理吗？"))
                {
                    return;
                }
            }

            IsBusy = true;
            IsCleaning = true;
            _cleaningTotal = selected.Count;
            _cleaningStep = 0;
            _cleaningDeleted = 0;
            _cleaningTitle = null;
            _keepStatusAfterRefresh = false;
            Logs.Clear();
            StatusText = string.Format("正在清理… 0/{0}", _cleaningTotal);
            AppendLog("开始清理…");
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
                _cts = null;
            }
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;

            try
            {
                var progress = new Progress<string>(OnCleanProgress);
                CleanupOutcome outcome = await Task.Run(
                    () => CleanupService.Clean(selected, progress, ct), ct);

                if (outcome.Cancelled)
                {
                    StatusText = "清理已取消。";
                    AppendLog("清理被取消。");
                    return;
                }

                AppendLog("—— 清理完成 ——");
                if (outcome.CleanedTitles.Count > 0)
                {
                    AppendLog("已清理：" + string.Join("、", outcome.CleanedTitles));
                }
                StatusText = outcome.FailedPaths.Count > 0
                    ? string.Format("清理完成：释放约 {0}，{1} 项未能删除（见下方日志）。",
                        FC.Converters.SizeText.Format(outcome.FreedBytes), outcome.FailedPaths.Distinct().Count())
                    : string.Format("清理完成：释放约 {0}。", FC.Converters.SizeText.Format(outcome.FreedBytes));

                if (outcome.FailedPaths.Count > 0)
                {
                    AppendLog("以下内容未能删除（可能被占用，需管理员或关闭占用程序）：");
                    foreach (var f in outcome.FailedPaths.Distinct().Take(40))
                    {
                        AppendLog("  ✗ " + f);
                    }
                    _services.Dialogs.ShowError("部分清理失败",
                        string.Join("\n", outcome.FailedPaths.Distinct().Take(40)));
                }

                // 清理后刷新占用显示（但不覆盖上面的"清理完成"提示）
                _keepStatusAfterRefresh = true;
                BeginSizeRefresh();
            }
            catch (OperationCanceledException)
            {
                StatusText = "清理已取消。";
                AppendLog("清理被取消。");
            }
            catch (Exception ex)
            {
                StatusText = "清理异常：" + ex.Message;
                AppendLog("异常：" + ex.Message);
                _services.Dialogs.ShowError("清理异常", ex.Message);
            }
            finally
            {
                IsCleaning = false;
                IsBusy = false;
            }
        }

        /// <summary>清理过程中解析日志行，实时更新"正在清理 x/y：项目名"提示。</summary>
        private void OnCleanProgress(string line)
        {
            // 删除计数行："…已删除 N 项" → 更新带删除计数的状态，用户能看到确实在推进
            int deleted = TryParseDeletedCount(line);
            if (deleted >= 0)
            {
                _cleaningDeleted = deleted;
                UpdateCleaningStatus();
                AppendLog(line);
                return;
            }

            string title;
            bool done;
            if (TryParseCleanProgress(line, out title, out done))
            {
                if (done)
                {
                    _cleaningStep++;
                }
                if (title != null)
                {
                    _cleaningTitle = title;
                }
                UpdateCleaningStatus();
            }
            AppendLog(line);
        }

        private void UpdateCleaningStatus()
        {
            string tail = _cleaningDeleted > 0 ? string.Format("，已删除 {0} 项", _cleaningDeleted) : "";
            if (_cleaningTitle != null)
            {
                StatusText = string.Format("正在清理… {0}/{1}：{2}{3}", _cleaningStep, _cleaningTotal, _cleaningTitle, tail);
            }
            else
            {
                StatusText = string.Format("正在清理… {0}/{1}{2}", _cleaningStep, _cleaningTotal, tail);
            }
        }

        /// <summary>从"…已删除 N 项"日志行提取 N（非删除计数行返回 -1）。</summary>
        public static int TryParseDeletedCount(string line)
        {
            if (string.IsNullOrEmpty(line))
            {
                return -1;
            }
            int p = line.IndexOf(DeletedMarker, StringComparison.Ordinal);
            if (p < 0)
            {
                return -1;
            }
            int s = p + DeletedMarker.Length;
            int e = s;
            while (e < line.Length && char.IsDigit(line[e]))
            {
                e++;
            }
            if (e == s)
            {
                return -1;
            }
            int v;
            return int.TryParse(line.Substring(s, e - s), out v) ? v : -1;
        }

        internal const string StartPrefix = "=== ";
        internal const string DonePrefix = "该项清理完成";
        internal const string DeletedMarker = "已删除 ";

        /// <summary>
        /// 由日志行识别清理进度事件（纯逻辑，便于单测）：
        /// - "=== 标题 ===" → title=标题, done=false（项开始）
        /// - "该项清理完成…" → title=null, done=true（项完成）
        /// 其它行返回 false。
        /// </summary>
        public static bool TryParseCleanProgress(string line, out string title, out bool done)
        {
            title = null;
            done = false;
            if (string.IsNullOrEmpty(line))
            {
                return false;
            }
            if (line.StartsWith(StartPrefix, StringComparison.Ordinal) && line.EndsWith(" ===", StringComparison.Ordinal))
            {
                title = line.Substring(StartPrefix.Length, line.Length - StartPrefix.Length - 4).Trim();
                return true;
            }
            if (line.StartsWith(DonePrefix, StringComparison.Ordinal))
            {
                done = true;
                return true;
            }
            return false;
        }

        private void AppendLog(string line)
        {
            // 双保险：任何非 UI 线程调用都 marshal 回 UI 线程，避免绑定 CollectionView 崩溃
            if (Application.Current != null && !Application.Current.Dispatcher.CheckAccess())
            {
                Application.Current.Dispatcher.Invoke(new Action(() => AppendLogInner(line)));
                return;
            }
            AppendLogInner(line);
        }

        private void AppendLogInner(string line)
        {
            if (Logs.Count > 800)
            {
                Logs.RemoveAt(0);
            }
            Logs.Add(line);
        }
    }
}