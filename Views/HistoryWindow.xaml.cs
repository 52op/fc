using System.Windows;
using FC.Services;
using FC.ViewModels;

namespace FC.Views
{
    /// <summary>迁移记录窗口：数据绑定到 HistoryViewModel。</summary>
    public partial class HistoryWindow : Window
    {
        public HistoryWindow(AppServices services)
        {
            InitializeComponent();
            DataContext = new HistoryViewModel(services);
        }
    }
}
