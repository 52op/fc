using System.Windows;
using FC.Services;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「通过环境变量迁移」操作窗：选作用域 + 目标目录，确认后驱动迁移。</summary>
    public partial class EnvMigrateWindow : Window
    {
        private readonly EnvMigrateViewModel _vm;

        public EnvMigrateWindow(AppServices services, EnvVarMatch match)
        {
            InitializeComponent();
            _vm = new EnvMigrateViewModel(services, match);
            _vm.RequestClose += Close;
            DataContext = _vm;
        }

        /// <summary>迁移是否成功（供调用方读取更新树节点）。</summary>
        public bool Migrated { get { return _vm.Migrated; } }

        /// <summary>迁移目标目录（成功时有效）。</summary>
        public string MigratedDestPath { get { return _vm.MigratedDestPath; } }

        private void OnScopeUser(object sender, RoutedEventArgs e) { _vm.SetScopeUser(); }
        private void OnScopeMachine(object sender, RoutedEventArgs e) { _vm.SetScopeMachine(); }
    }
}