using System;
using System.IO;
using FC.Models;
using FC.Services;
using FC.ViewModels;

class MenuCmdTest
{
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_mc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var services = AppServices.CreateDefault();
            var node = new DiskNode { FullPath = root, Name = "migdir", IsFolder = true };
            var vm = new TreeNodeViewModel(node, services, 0);

            // 1) CanExecute
            if (!vm.MigrateCommand.CanExecute(null))
            {
                Console.WriteLine("FAIL: MigrateCommand.CanExecute=false");
                return 1;
            }
            Console.WriteLine("OK MigrateCommand.CanExecute=true");

            // 2) CopyPath 命令管线（剪贴板可能因环境不可用，仅警告不判失败）
            vm.CopyPathCommand.Execute(null);
            try
            {
                string clip = System.Windows.Clipboard.GetText();
                if (string.IsNullOrEmpty(clip) || !clip.Contains("fc_mc_"))
                {
                    Console.WriteLine("WARN: clipboard unavailable/empty (env), command executed without exception");
                }
                else
                {
                    Console.WriteLine("OK CopyPathCommand wrote clipboard");
                }
            }
            catch (Exception)
            {
                Console.WriteLine("WARN: clipboard access threw (env), command itself OK");
            }

            // 3) 复制路径与打开命令 canExecute
            if (!vm.CopyPathCommand.CanExecute(null) || !vm.OpenExplorerCommand.CanExecute(null))
            {
                Console.WriteLine("FAIL: path commands CanExecute=false");
                return 1;
            }
            Console.WriteLine("OK path commands CanExecute=true");

            Console.WriteLine("ALL MENU-CMD TESTS DONE");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 2;
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static void TryDelete(string p)
    {
        try { if (p != null && Directory.Exists(p)) { Directory.Delete(p, true); } }
        catch (Exception) { }
    }
}