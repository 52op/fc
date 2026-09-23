using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using FC.Converters;
using FC.ViewModels;

namespace FC
{
    /// <summary>
    /// 主窗口：绑定 MainViewModel；菜单栏按钮弹出程序化 ContextMenu（主题安全）。
    /// </summary>
    public partial class MainWindow : Window
    {
        private readonly MainViewModel _viewModel;

        public MainWindow()
        {
            InitializeComponent();
            _viewModel = new MainViewModel(App.Services);
            DataContext = _viewModel;
            Loaded += async (s, e) => await _viewModel.InitializeAsync();
        }

        // ==================== 菜单栏 ====================

        private void OnMenuFile(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            AddCommandItem(menu, "清理建议…", _viewModel.OpenCleanupCommand);
            AddCommandItem(menu, "查看最大文件…", _viewModel.ShowLargeFilesCommand);
            AddCommandItem(menu, "类型统计…", _viewModel.ShowTypeStatsCommand);
            menu.Items.Add(new Separator());
            AddCommandItem(menu, "查看迁移记录", _viewModel.ShowHistoryCommand);
            menu.Items.Add(new Separator());
            var exit = new MenuItem { Header = "退出" };
            exit.Click += (s, a) => Close();
            menu.Items.Add(exit);
            ShowMenu((Button)sender, menu);
        }

        private void OnMenuScan(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            AddCommandItem(menu, "选择目录…", _viewModel.SelectFolderCommand);
            AddCommandItem(menu, "扫描", _viewModel.ScanCommand);
            AddCommandItem(menu, "停止", _viewModel.CancelScanCommand);
            AddCommandItem(menu, "刷新", _viewModel.ScanCommand);
            ShowMenu((Button)sender, menu);
        }

        private void OnMenuView(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            AddUnitItem(menu, "自动");
            AddUnitItem(menu, "GB");
            AddUnitItem(menu, "MB");
            AddUnitItem(menu, "KB");
            menu.Items.Add(new Separator());
            AddCommandItem(menu, "展开全部", _viewModel.ExpandAllCommand);
            AddCommandItem(menu, "折叠全部", _viewModel.CollapseAllCommand);
            menu.Items.Add(new Separator());
            AddCommandItem(menu, _viewModel.ThemeToggleText, _viewModel.ThemeToggleCommand);
            ShowMenu((Button)sender, menu);
        }

        private void OnMenuHelp(object sender, RoutedEventArgs e)
        {
            var menu = new ContextMenu();
            var about = new MenuItem { Header = "关于 FC" };
            about.Click += (s, a) => MessageBox.Show(
                "FC - 磁盘目录分析迁移工具\n\n.NET Framework 4.8 / WPF\n\n扫描磁盘 → 右键目录 → 一键迁移（robocopy + 校验 + junction），可随时还原。",
                "关于 FC", MessageBoxButton.OK, MessageBoxImage.Information);
            menu.Items.Add(about);
            ShowMenu((Button)sender, menu);
        }

        private static void AddCommandItem(ContextMenu menu, string header, ICommand command)
        {
            menu.Items.Add(new MenuItem { Header = header, Command = command });
        }

        private void AddUnitItem(ContextMenu menu, string mode)
        {
            var item = new MenuItem
            {
                Header = mode,
                IsCheckable = true
            };
            item.SetBinding(MenuItem.IsCheckedProperty, new Binding("UnitMode")
            {
                Converter = new CompareConverter(),
                ConverterParameter = mode
            });
            item.Click += (s, a) => _viewModel.UnitMode = mode;
            menu.Items.Add(item);
        }

        private void ShowMenu(Button owner, ContextMenu menu)
        {
            menu.PlacementTarget = owner;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}