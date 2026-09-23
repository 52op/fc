using System.Windows;
using System.Windows.Controls;

namespace FC.Controls
{
    /// <summary>
    /// 树形表格（TreeSize 式）：表头 + 可拖列宽 + 虚拟化 TreeView。
    /// 列宽以 DP 暴露，表头与行模板都绑定这些 DP；列显隐由 DataContext（MainViewModel）的 Show* 布尔控制。
    /// </summary>
    public partial class TreeListView : UserControl
    {
        public static readonly DependencyProperty ItemsSourceProperty =
            DependencyProperty.Register("ItemsSource", typeof(object), typeof(TreeListView),
                new PropertyMetadata(null));

        public static readonly DependencyProperty ColNameWidthProperty =
            DependencyProperty.Register("ColNameWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(300.0));

        public static readonly DependencyProperty ColSizeWidthProperty =
            DependencyProperty.Register("ColSizeWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(120.0));

        public static readonly DependencyProperty ColAllocatedWidthProperty =
            DependencyProperty.Register("ColAllocatedWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(130.0));

        public static readonly DependencyProperty ColFilesWidthProperty =
            DependencyProperty.Register("ColFilesWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(90.0));

        public static readonly DependencyProperty ColFoldersWidthProperty =
            DependencyProperty.Register("ColFoldersWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(100.0));

        public static readonly DependencyProperty ColPercentWidthProperty =
            DependencyProperty.Register("ColPercentWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(150.0));

        public static readonly DependencyProperty ColModifiedWidthProperty =
            DependencyProperty.Register("ColModifiedWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(140.0));

        public static readonly DependencyProperty ColSplitterWidthProperty =
            DependencyProperty.Register("ColSplitterWidth", typeof(double), typeof(TreeListView),
                new PropertyMetadata(4.0));

        public object ItemsSource
        {
            get { return GetValue(ItemsSourceProperty); }
            set { SetValue(ItemsSourceProperty, value); }
        }

        public double ColNameWidth
        {
            get { return (double)GetValue(ColNameWidthProperty); }
            set { SetValue(ColNameWidthProperty, value); }
        }

        public double ColSizeWidth
        {
            get { return (double)GetValue(ColSizeWidthProperty); }
            set { SetValue(ColSizeWidthProperty, value); }
        }

        public double ColAllocatedWidth
        {
            get { return (double)GetValue(ColAllocatedWidthProperty); }
            set { SetValue(ColAllocatedWidthProperty, value); }
        }

        public double ColFilesWidth
        {
            get { return (double)GetValue(ColFilesWidthProperty); }
            set { SetValue(ColFilesWidthProperty, value); }
        }

        public double ColFoldersWidth
        {
            get { return (double)GetValue(ColFoldersWidthProperty); }
            set { SetValue(ColFoldersWidthProperty, value); }
        }

        public double ColPercentWidth
        {
            get { return (double)GetValue(ColPercentWidthProperty); }
            set { SetValue(ColPercentWidthProperty, value); }
        }

        public double ColModifiedWidth
        {
            get { return (double)GetValue(ColModifiedWidthProperty); }
            set { SetValue(ColModifiedWidthProperty, value); }
        }

        public double ColSplitterWidth
        {
            get { return (double)GetValue(ColSplitterWidthProperty); }
            set { SetValue(ColSplitterWidthProperty, value); }
        }

        public TreeListView()
        {
            InitializeComponent();
        }
    }
}