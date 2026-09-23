using System.Windows;
using System.Windows.Controls;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「增量对比」窗口。</summary>
    public partial class DeltaWindow : Window
    {
        public DeltaWindow(DeltaViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        private void Grid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            e.Handled = true; // 只读快照，保持变化量降序
        }
    }
}