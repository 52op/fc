using System.Windows;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「最大文件」窗口：展示最近一次扫描的全局 Top 大文件。</summary>
    public partial class LargeFilesWindow : Window
    {
        public LargeFilesWindow(LargeFilesViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void Row_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var vm = DataContext as LargeFilesViewModel;
            if (vm == null)
            {
                return;
            }
            var row = Grid.SelectedItem as LargeFilesViewModel.LargeFileRow;
            if (row != null)
            {
                vm.OpenCommand.Execute(row);
            }
        }

        private void Grid_Sorting(object sender, System.Windows.Controls.DataGridSortingEventArgs e)
        {
            e.Handled = true; // 只读快照，禁用内置排序（保持大小降序展示）
        }
    }
}