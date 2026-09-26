using System;
using System.Threading.Tasks;
using FC.Services;

namespace FC.ViewModels
{
    /// <summary>
    /// 「通过环境变量迁移」操作窗 VM。
    /// 展示规则信息 + 让用户选作用域 + 目标目录，确认后驱动 EnvVarMigrator 全流程。
    /// 迁移过程用 MigrateProgressWindow 显示进度与日志。
    /// </summary>
    public class EnvMigrateViewModel : ViewModelBase
    {
        private readonly AppServices _services;
        private readonly EnvVarMatch _match;
        private bool _busy;
        private string _targetDir;
        private bool _createJunctionFallback = true;
        private EnvVarManager.Scope _scope = EnvVarManager.Scope.User;

        public EnvMigrateViewModel(AppServices services, EnvVarMatch match)
        {
            _services = services;
            _match = match;

            PickFolderCommand = new RelayCommand(PickFolder, () => !_busy);
            MigrateCommand = new RelayCommand(async () => await MigrateAsync(), () => CanMigrate);
            CancelCommand = new RelayCommand(() => RequestClose?.Invoke(), () => !_busy);
        }

        /// <summary>请求关闭窗口（由 Window 挂接）。</summary>
        public Action RequestClose { get; set; }

        public string SoftwareName { get { return _match.Rule.SoftwareName; } }

        public string EnvVarName { get { return _match.Rule.EnvVarName; } }

        public string SourcePath { get { return _match.MatchedPath; } }

        /// <summary>可信度提示：官方文档化 / 社区源码支持。</summary>
        public string ConfidenceText
        {
            get
            {
                return _match.Rule.Confidence == RuleConfidence.Official
                    ? "官方文档确认支持"
                    : "官方源码支持（未文档化），迁移前请确认软件版本兼容";
            }
        }

        public string SourceUrl { get { return _match.Rule.SourceUrl; } }

        public string TargetDir
        {
            get { return _targetDir; }
            set
            {
                if (Set(ref _targetDir, value))
                {
                    OnPropertyChanged("CanMigrate");
                }
            }
        }

        public bool CreateJunctionFallback
        {
            get { return _createJunctionFallback; }
            set
            {
                if (Set(ref _createJunctionFallback, value))
                {
                    OnPropertyChanged("JunctionHintText");
                }
            }
        }

        public string JunctionHintText
        {
            get
            {
                return _createJunctionFallback
                    ? "源位置将保留联接（junction）作为兜底，软件若仍读旧路径也能工作。"
                    : "源目录将被删除（不留联接）。";
            }
        }

        public bool ScopeIsUser { get { return _scope == EnvVarManager.Scope.User; } }
        public bool ScopeIsMachine { get { return _scope == EnvVarManager.Scope.Machine; } }

        public string ScopeText
        {
            get
            {
                return _scope == EnvVarManager.Scope.Machine
                    ? "所有用户（需管理员）"
                    : "当前用户";
            }
        }

        public bool CanMigrate
        {
            get { return !_busy && !string.IsNullOrWhiteSpace(TargetDir); }
        }

        public bool IsBusy { get { return _busy; } }

        /// <summary>迁移是否成功（供调用方读取，成功后更新树节点状态）。</summary>
        public bool Migrated { get; private set; }

        /// <summary>迁移目标目录（成功时有效，供标注"已迁移 → 位置"）。</summary>
        public string MigratedDestPath { get; private set; }

        public RelayCommand PickFolderCommand { get; private set; }
        public RelayCommand MigrateCommand { get; private set; }
        public RelayCommand CancelCommand { get; private set; }

        public void SetScopeUser()
        {
            if (_scope != EnvVarManager.Scope.User)
            {
                _scope = EnvVarManager.Scope.User;
                OnPropertyChanged("ScopeIsUser");
                OnPropertyChanged("ScopeIsMachine");
                OnPropertyChanged("ScopeText");
            }
        }

        public void SetScopeMachine()
        {
            if (_scope != EnvVarManager.Scope.Machine)
            {
                _scope = EnvVarManager.Scope.Machine;
                OnPropertyChanged("ScopeIsUser");
                OnPropertyChanged("ScopeIsMachine");
                OnPropertyChanged("ScopeText");
            }
        }

        private void PickFolder()
        {
            string initial = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            try
            {
                string picked = _services.Dialogs.PickFolder(
                    "选择数据迁移到的目标目录（环境变量将指向它）", initial, out bool cancelled);
                if (!cancelled && !string.IsNullOrEmpty(picked))
                {
                    TargetDir = picked;
                }
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("选择目录失败", ex.Message);
            }
        }

        private async Task MigrateAsync()
        {
            if (_busy || string.IsNullOrWhiteSpace(TargetDir))
            {
                return;
            }
            string source = SourcePath;
            string dest = TargetDir.Trim();

            var migrator = new EnvVarMigrator(_services.Verifier, _services.Records);
            string err = migrator.Validate(source, dest);
            if (err != null)
            {
                _services.Dialogs.ShowError("无法迁移", err);
                return;
            }

            bool ok = _services.Dialogs.Confirm("确认迁移",
                "将新建环境变量：" + EnvVarName + " = " + dest + "\n"
                + "作用域：" + ScopeText + "\n"
                + "源目录：" + source + "\n"
                + "可信度：" + ConfidenceText + "\n"
                + "\n执行流程：结束占用进程 → 复制数据 → 校验 → 设置环境变量 → 清理源目录。\n"
                + JunctionHintText + "\n\n确认开始？");
            if (!ok)
            {
                return;
            }

            _busy = true;
            RaiseStateChanged();

            try
            {
                using (var dlg = _services.Dialogs.ShowProgress("环境变量迁移", "正在迁移 " + SoftwareName + " …"))
                {
                    var progress = new Progress<string>(dlg.AppendLog);
                    var result = await migrator.MigrateAsync(
                        _match.Rule, source, dest, _scope, CreateJunctionFallback,
                        progress, dlg.Token, dlg.SetProgress);

                    dlg.AppendLog(result.Message);
                    if (result.Success)
                    {
                        dlg.SetProgress(100);
                        Migrated = true;
                        MigratedDestPath = dest;
                        _services.Dialogs.ShowInfo("迁移完成", result.Message);
                        RequestClose?.Invoke();
                        return;
                    }
                    if (result.Cancelled)
                    {
                        return; // 用户取消：保持窗口
                    }
                    // 失败：提示 + 保持窗口供重试
                    _services.Dialogs.ShowError("迁移未完成", result.Message);
                }
            }
            catch (Exception ex)
            {
                _services.Dialogs.ShowError("迁移失败", ex.Message);
            }
            finally
            {
                _busy = false;
                RaiseStateChanged();
            }
        }

        private void RaiseStateChanged()
        {
            OnPropertyChanged("CanMigrate");
            OnPropertyChanged("IsBusy");
        }
    }
}