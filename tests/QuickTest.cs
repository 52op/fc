using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FC.Models;
using FC.Services;

class QuickTest
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_qt_" + Guid.NewGuid().ToString("N"));
        string cacheFile = Path.Combine(Path.GetTempPath(), "fc_cache_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "data"));
            Directory.CreateDirectory(Path.Combine(root, "node_modules")); // 命中默认跳过列表
            File.WriteAllBytes(Path.Combine(root, "data", "a.bin"), new byte[1234]);
            File.WriteAllBytes(Path.Combine(root, "node_modules", "big.bin"), new byte[999999]);

            var cache = new ScanCache(cacheFile);
            cache.Load();

            // 第一次：全量 + 跳过列表
            var s1 = new Scanner();
            s1.Cache = cache;
            s1.QuickReuseMode = false;
            var r1 = s1.ScanAsync(root, null, CancellationToken.None).Result;

            var nm = FindDir(r1.RootNode, "node_modules");
            if (nm == null || !nm.IsSkipped || nm.Error == null)
            {
                Console.WriteLine("FAIL skip list (skipped node missing)");
                return 1;
            }
            if (r1.TotalFiles != 1 || r1.TotalBytes != 1234)
            {
                Console.WriteLine("FAIL skip totals files=" + r1.TotalFiles + " bytes=" + r1.TotalBytes);
                return 1;
            }
            Console.WriteLine("OK skip list: node_modules skipped, totals exclude its 999999 bytes");

            // 第二次：快速复用
            var sw = Stopwatch.StartNew();
            var s2 = new Scanner();
            s2.Cache = cache;
            s2.QuickReuseMode = true;
            var r2 = s2.ScanAsync(root, null, CancellationToken.None).Result;
            sw.Stop();

            if (r2.ReusedCount <= 0)
            {
                Console.WriteLine("FAIL quick reuse reused=" + r2.ReusedCount);
                return 1;
            }
            if (r2.TotalBytes != r1.TotalBytes || r2.TotalFiles != r1.TotalFiles)
            {
                Console.WriteLine("FAIL reuse totals r1=" + r1.TotalBytes + "/" + r1.TotalFiles + " r2=" + r2.TotalBytes + "/" + r2.TotalFiles);
                return 1;
            }
            Console.WriteLine("OK quick reuse: reused=" + r2.ReusedCount + " dirs, " + sw.Elapsed.TotalMilliseconds.ToString("0") + "ms, totals match");

            // 结构变化（增删文件改目录 mtime）→ 快速复用应检测到并重算
            File.Delete(Path.Combine(root, "data", "a.bin"));
            File.WriteAllBytes(Path.Combine(root, "data", "b.bin"), new byte[7777]);
            var s3 = new Scanner();
            s3.Cache = cache;
            s3.QuickReuseMode = true;
            var r3 = s3.ScanAsync(root, null, CancellationToken.None).Result;
            if (r3.TotalBytes != 7777)
            {
                Console.WriteLine("FAIL structure-change reuse bytes=" + r3.TotalBytes);
                return 1;
            }
            Console.WriteLine("OK structure change captured: bytes=" + r3.TotalBytes);

            Console.WriteLine("ALL QUICK TESTS PASSED");
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
            TryDeleteFile(cacheFile);
        }
    }

    private static DiskNode FindDir(DiskNode root, string name)
    {
        foreach (var c in root.Children)
        {
            if (c.IsFolder && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return c;
            }
        }
        return null;
    }

    private static void TryDelete(string p)
    {
        try { if (p != null && Directory.Exists(p)) { Directory.Delete(p, true); } }
        catch (Exception) { }
    }

    private static void TryDeleteFile(string p)
    {
        try { if (p != null && File.Exists(p)) { File.Delete(p); } }
        catch (Exception) { }
    }
}