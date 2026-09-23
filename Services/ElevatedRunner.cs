using System;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using System.Threading;
using System.Windows;
using FC.Models;
using FC.Views;

namespace FC.Services
{
    /// <summary>
    /// 提权辅助：检测当前是否管理员；普通权限下用 UAC(runas) 以管理员重启 FC 并自动重试指定迁移。
    /// 说明：删除受 UAC/ACL 保护的目录需要管理员令牌——Sydinternals 等工具同样需要，因此内置"一键提权重试"。
    /// </summary>
    public static class ElevatedRunner
    {
        /// <summary>当前进程是否为管理员（完整令牌）。</summary>
        public static bool IsAdministrator()
        {
            try
            {
                var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>以管理员身份重新启动当前程序（触发 UAC 确认）。</summary>
        public static void RelaunchAsAdmin(string arguments)
        {
            try
            {
                string exe = Assembly.GetEntryAssembly() == null
                    ? Process.GetCurrentProcess().MainModule.FileName
                    : Assembly.GetEntryAssembly().Location;
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    Arguments = arguments,
                    Verb = "runas",
                    UseShellExecute = true
                };
                Process.Start(psi);
            }
            catch (Exception)
            {
                // 用户取消 UAC 等
            }
        }

        /// <summary>
        /// 从命令行参数执行"提权后的自动迁移"（App.OnStartup 调用）。
        /// 参数形如：--migrate "源" "目标根目录"。
        /// 完成后弹出结果并退出。
        /// </summary>
        public static void RunMigrateFromCommandLine(string source, string destRoot)
        {
            var services = AppServices.CreateDefault();
            var dlg = new MigrateProgressWindow("正在迁移", "管理员权限重试：\n" + source + "\n→\n" + destRoot);
            dlg.Show();

            ExecuteAsync(services, source, destRoot, dlg);
        }

        private static async void ExecuteAsync(
            AppServices services, string source, string destRoot, MigrateProgressWindow dlg)
        {
            try
            {
                var progress = new Progress<string>(dlg.AppendLog);
                MigrationResult result = await services.Migrator.MigrateAsync(
                    source, destRoot, progress, dlg.Token, dlg.SetProgress);
                dlg.AppendLog(result.Message);
                dlg.Close();

                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show(
                        result.Message,
                        result.Success ? "迁移完成" : "迁移未完成",
                        MessageBoxButton.OK,
                        result.Success ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    Application.Current.Shutdown(result.Success ? 0 : 1);
                });
            }
            catch (OperationCanceledException)
            {
                try
                {
                    dlg.Close();
                }
                catch (Exception)
                {
                }
                Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown(1));
            }
            catch (Exception ex)
            {
                try
                {
                    dlg.AppendLog("异常：" + ex.Message);
                    dlg.Close();
                }
                catch (Exception)
                {
                }
                Application.Current.Dispatcher.Invoke(() =>
                {
                    MessageBox.Show("提权重试异常：" + ex.Message, "FC", MessageBoxButton.OK, MessageBoxImage.Error);
                    Application.Current.Shutdown(1);
                });
            }
        }
    }
}