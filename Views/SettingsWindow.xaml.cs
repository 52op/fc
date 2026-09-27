using System.Windows;
using System.Windows.Controls;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「设置」窗口：集中管理各设置项（当前：多根扫描保留数量）。后续设置项都加在这里。</summary>
    public partial class SettingsWindow : Window
    {
        private readonly MainViewModel _vm;

        public SettingsWindow(MainViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            // 填充下拉：1-8 个
            for (int n = 1; n <= 8; n++)
            {
                RootsCombo.Items.Add(n.ToString());
            }
            // 选中当前值（先设标志避免初始化时误触发保存）
            _initializing = true;
            RootsCombo.SelectedIndex = vm.MaxRootsIndex;
            _initializing = false;
        }

        private bool _initializing;

        private void OnRootsChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_initializing)
            {
                return;
            }
            // 下拉索引 0-based → 数量 1-based
            _vm.SetMaxRoots(RootsCombo.SelectedIndex + 1);
        }

        private void OnClose(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}