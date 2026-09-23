using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using FC.Services;
using FC.ViewModels;

class CleanupFastTest
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_fast_" + Guid.NewGuid().ToString("N"));
        try
        {
            // 造 ~1500 文件分散在 30 子目录，模拟中等临时目录
            Directory.CreateDirectory(root);
            for (int d = 0; d < 30; d++)
            {
                string sub = Path.Combine(root, "dir" + d);
                Directory.CreateDirectory(sub);
                for (int f = 0; f < 50; f++)
                {
                    File.WriteAllBytes(Path.Combine(sub, "f" + f + ".tmp"), new byte[100]);
                }
            }

            // 1) 单遍尽力删除：应秒级完成，不死磕
            var failed = new List<string>();
            var log = new List<string>();
            var sw = Stopwatch.StartNew();
            FileDeleter.EmptyDirectoryContents(root,
                new SyncProgress(s => log.Add(s)), CancellationToken.None, failed);
            sw.Stop();

            bool hasChild = false;
            if (Directory.Exists(root)) { hasChild = Directory.EnumerateFileSystemEntries(root).Any(); }
            if (hasChild)
            {
                Console.WriteLine("FAIL: contents not emptied");
                return 1;
            }
            if (!Directory.Exists(root))
            {
                Console.WriteLine("FAIL: root dir should be preserved");
                return 1;
            }
            Console.WriteLine("OK single-pass empty: 1500 files cleaned in " + sw.Elapsed.TotalSeconds.ToString("0.0") + "s, failed=" + failed.Count);

            // 2) 删除计数解析
            if (CleanupViewModel.TryParseDeletedCount("...deleted 300 items") != -1 &&
                CleanupViewModel.TryParseDeletedCount("\u5DF2\u5220\u9664 300 \u9879") != 300)
            {
                Console.WriteLine("FAIL: parse deleted count");
                return 1;
            }
            if (CleanupViewModel.TryParseDeletedCount("some unrelated line") != -1)
            {
                Console.WriteLine("FAIL: non-count line should return -1");
                return 1;
            }
            Console.WriteLine("OK deleted-count parse");

            Console.WriteLine("ALL CLEANUP-FAST TESTS PASSED");
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

    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _a;
        public SyncProgress(Action<string> a) { _a = a; }
        public void Report(string v) { _a(v); }
    }

    private static void TryDelete(string p)
    {
        try { if (p != null && Directory.Exists(p)) { Directory.Delete(p, true); } }
        catch (Exception) { }
    }
}