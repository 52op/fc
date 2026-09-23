using System;
using System.Windows;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「文件类型统计」窗口：展示最近一次扫描的扩展名聚合占用。</summary>
    public partial class TypeStatsWindow : Window
    {
        public TypeStatsWindow(TypeStatsViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void Grid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            var row = Grid.SelectedItem as TypeStatsViewModel.TypeRow;
            if (row == null)
            {
                return;
            }
            try
            {
                Clipboard.SetText(row.ExtensionText);
            }
            catch (Exception)
            {
            }
        }
    }
}