using System.Windows;
using FC.Themes;

namespace FC.Views
{
    /// <summary>「帮助→更新→设置更新地址」窗口：读写 %APPDATA%\FC\settings.xml 的 RulesUpdateUrl。</summary>
    public partial class UpdateSettingsWindow : Window
    {
        public UpdateSettingsWindow()
        {
            InitializeComponent();
            var s = ThemeManager.CurrentSettings;
            UrlBox.Text = (s != null && !string.IsNullOrWhiteSpace(s.RulesUpdateUrl))
                ? s.RulesUpdateUrl
                : "";
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            var s = ThemeManager.CurrentSettings;
            if (s == null)
            {
                MessageBox.Show(this, "设置未初始化，无法保存。请重启 FC 后重试。",
                    "更新设置", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            s.RulesUpdateUrl = string.IsNullOrWhiteSpace(UrlBox.Text) ? null : UrlBox.Text.Trim();
            ThemeManager.SaveSettings();
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}