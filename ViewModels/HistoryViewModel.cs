using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using FC.Models;
using FC.Services;

namespace FC.ViewModels
{
    /// <summary>
    /// 迁移记录窗口 VM：列表 + 还原 + 删除记录。
    /// </summary>
    public class HistoryViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private MigrationRecord _selectedRecord;
        private string _statusText;

        public HistoryViewModel(AppServices services)
        {
            _services = services;

            Records = new ObservableCollection<MigrationRecord>();

            RefreshCommand = new RelayCommand(Refresh, null);
            RestoreCommand = new RelayCommand(async () => await RestoreSelectedAsync(),
                () => _selectedRecord != null && _selectedRecord.Status != MigrationStatus.Restored);
            DeleteCommand = new RelayCommand(DeleteSelected, () => _selectedRecord != null);

            _statusText = "";
            Refresh();
        }

        public ObservableCollection<MigrationRecord> Records { get; private set; }

        public MigrationRecord SelectedRecord
        {
            get { return _selectedRecord; }
            set
            {
                if (Set(ref _selectedRecord, value))
                {
                    RaiseCommands();
                }
            }
        }

        public string StatusText
        {
            get { return _statusText; }
            set { Set(ref _statusText, value); }
        }

        public RelayCommand RefreshCommand { get; private set; }

        public RelayCommand RestoreCommand { get; private set; }

        public RelayCommand DeleteCommand { get; private set; }

        private void Refresh()
        {
            Records.Clear();
            var list = _services.Records.Load();
            foreach (var r in list)
            {
                Records.Add(r);
            }
            StatusText = "共 " + Records.Count + " 条记录。";
            RaiseCommands();
        }

        private async Task RestoreSelectedAsync()
        {
            var r = _selectedRecord;
            if (r == null)
            {
                _services.Dialogs.ShowError("未选择", "请先在列表中选择一条记录。");
                return;
            }
            if (r.Status == MigrationStatus.Restored)
            {
                _services.Dialogs.ShowError("无需还原", "这条记录已经还原过了。");
                return;
            }

            string msg = string.Format(
                "还原记录：\n源路径（数据将移回）：{0}\n目标路径（数据当前所在）：{1}\n迁移日期：{2:yyyy-MM-dd HH:mm}\n\n执行顺序：\n1) 删除源位置联接\n2) 重建空目录\n3) robocopy 把数据拷回\n4) 校验\n5) 清理目标位置副本\n\n确认还原？",
                r.SourcePath, r.DestPath, r.MigratedAt);

            if (!_services.Dialogs.Confirm("确认还原", msg))
            {
                return;
            }

            try
            {
                using (var dlg = _services.Dialogs.ShowProgress("正在还原", "还原中…"))
                {
                    var progress = new Progress<string>(dlg.AppendLog);

                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        MigrationResult result = await _services.Migrator.RestoreAsync(
                            r, progress, dlg.Token, dlg.SetProgress);
                        dlg.AppendLog(result.Message);

                        if (result.Success || result.Cancelled)
                        {
                            break;
                        }

                        // 有占用进程：提示用户结束并重试
                        if (result.Lockers.Count > 0 && attempt < 2)
                        {
                            string lockMsg = "检测到以下进程可能占用目录，结束它们并重试？\n\n"
                                + LockFinder.Describe(result.Lockers)
                                + "\n\n（不会结束系统关键进程与 FC 自身）";
                            if (_services.Dialogs.Confirm("结束占用进程并重试", lockMsg))
                            {
                                dlg.AppendLog("正在结束占用进程…");
                                var errors = LockFinder.Kill(result.Lockers);
                                if (errors.Count > 0)
                                {
                                    dlg.AppendLog("部分进程结束失败：" + string.Join("；", errors));
                                    _services.Dialogs.ShowError("结束进程失败", string.Join("\n", errors));
                                    break;
                                }
                                dlg.AppendLog("已结束占用进程，自动重试…");
                                await Task.Delay(800);
                                continue;
                            }
                            break;
                        }

                        _services.Dialogs.ShowError("还原失败", result.Message);
                        break;
                    }
                }
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("还原异常", ex.Message);
            }
            finally
            {
                Refresh();
            }
        }

        private void DeleteSelected()
        {
            var r = _selectedRecord;
            if (r == null)
            {
                return;
            }

            string msg = string.Format("删除这条记录？\n源：{0}\n目标：{1}\n\n注意：删除记录只是删掉列表项，不会改动磁盘上的任何目录。",
                r.SourcePath, r.DestPath);
            if (!_services.Dialogs.Confirm("删除记录", msg))
            {
                return;
            }

            var list = _services.Records.Load();
            var target = list.FirstOrDefault(x => string.Equals(x.Id, r.Id, StringComparison.Ordinal));
            if (target != null)
            {
                list.Remove(target);
                _services.Records.SaveAll(list);
            }
            Refresh();
        }

        private void RaiseCommands()
        {
            RestoreCommand.RaiseCanExecuteChanged();
            DeleteCommand.RaiseCanExecuteChanged();
        }
    }
}