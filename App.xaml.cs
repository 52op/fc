using System;
using System.Windows;
using FC.Services;
using FC.Themes;

namespace FC
{
    /// <summary>应用入口：装配服务、应用主题、挂全局异常钩子、手工创建主窗口。</summary>
    public partial class App : Application
    {
        public static AppServices Services { get; private set; }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            Services = AppServices.CreateDefault();

            // 命令行提权重试：--migrate "源" "目标根目录"
            string[] args = e.Args;
            if (args != null && args.Length >= 3 &&
                string.Equals(args[0], "--migrate", StringComparison.OrdinalIgnoreCase))
            {
                ElevatedRunner.RunMigrateFromCommandLine(args[1], args[2]);
                return;
            }

            ThemeManager.LoadAndApply();

            DispatcherUnhandledException += (s, args2) =>
            {
                MessageBox.Show(
                    "未处理的异常：\n" + args2.Exception.Message + "\n\n" + args2.Exception.StackTrace,
                    "FC 出错",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args2.Handled = true;
            };

            var main = new MainWindow();
            main.Show();
        }
    }
}