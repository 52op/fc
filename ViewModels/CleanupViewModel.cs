using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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

        public CleanupViewModel(AppServices services)
        {
            _services = services;
            Items = new ObservableCollection<CleanupItem>(CleanupService.BuildItems());
            Logs = new ObservableCollection<string>();

            RefreshCommand = new RelayCommand(async () => await RefreshSizesAsync());
            CleanCommand = new RelayCommand(async () => await CleanAsync(),
                () => !_busy && Items.Any(i => i.Selected));
            CancelCommand = new RelayCommand(Cancel, () => _busy);

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
            Task.Run(() =>
            {
                CleanupService.RefreshSizes(snapshot, new Progress<string>(s => AppendLog(s)), ct);
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
                    StatusText = string.Format("可清理项共 {0} 项，预计最多可释放 {1}。勾选后点“清理所选”。",
                        snapshot.Count(i => i.Bytes >= 0), FC.Converters.SizeText.Format(total));
                    SelectedBytes = snapshot.Where(i => i.Selected && i.Bytes > 0).Sum(i => i.Bytes);
                }
            }, TaskScheduler.FromCurrentSynchronizationContext());
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
            Logs.Clear();
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
                var progress = new Progress<string>(AppendLog);
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
                StatusText = string.Format("清理完成：释放约 {0}。",
                    FC.Converters.SizeText.Format(outcome.FreedBytes));

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

                // 清理后刷新占用显示
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
                IsBusy = false;
            }
        }

        private void AppendLog(string line)
        {
            if (Logs.Count > 800)
            {
                Logs.RemoveAt(0);
            }
            Logs.Add(line);
        }
    }
}