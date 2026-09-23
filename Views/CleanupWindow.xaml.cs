using System.Windows;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>「一键清理建议」窗口：勾选可清理项后真实执行清理。</summary>
    public partial class CleanupWindow : Window
    {
        public CleanupWindow(CleanupViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}