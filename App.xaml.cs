using System;
using System.Threading.Tasks;
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

            // 全局兜底：任何线程/任何位置的未处理异常都落盘，避免"闪退无痕"
            AppDomain.CurrentDomain.UnhandledException += (s, args2) =>
            {
                var ex = args2.ExceptionObject as Exception;
                CrashLog.Write("AppDomain-Unhandled", ex);
            };
            DispatcherUnhandledException += (s, args2) =>
            {
                CrashLog.Write("Dispatcher-Unhandled", args2.Exception);
                MessageBox.Show(
                    "未处理的异常：\n" + args2.Exception.Message + "\n\n" + args2.Exception.StackTrace,
                    "FC 出错",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                args2.Handled = true;
            };
            TaskScheduler.UnobservedTaskException += (s, args2) =>
            {
                CrashLog.Write("Task-Unobserved", args2.Exception);
                args2.SetObserved();
            };

            Services = AppServices.CreateDefault();

            // 启动时静默尝试更新环境变量规则数据（未配置 URL 或失败都无影响）
            EnvRulesUpdater.TrySilentUpdate();

            // 命令行提权重试：--migrate "源" "目标根目录"
            string[] argList = e.Args;
            if (argList != null && argList.Length >= 3 &&
                string.Equals(argList[0], "--migrate", StringComparison.OrdinalIgnoreCase))
            {
                ElevatedRunner.RunMigrateFromCommandLine(argList[1], argList[2]);
                return;
            }

            ThemeManager.LoadAndApply();

            var main = new MainWindow();
            main.Show();
        }
    }
}