using System;
using System.IO;
using System.Threading;
using FC.Models;
using FC.Services;

class Test
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_test_" + Guid.NewGuid().ToString("N"));
        string cacheFile = Path.Combine(Path.GetTempPath(), "fc_cache_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "dir1"));
            Directory.CreateDirectory(Path.Combine(root, "dir2"));
            File.WriteAllBytes(Path.Combine(root, "dir1", "a.bin"), new byte[100]);
            File.WriteAllBytes(Path.Combine(root, "dir1", "b.bin"), new byte[200]);
            File.WriteAllBytes(Path.Combine(root, "dir2", "c.bin"), new byte[300]);

            var scanner = new Scanner();
            var result = scanner.ScanAsync(root, null, CancellationToken.None).Result;

            // 总大小 = 600
            if (result.TotalBytes != 600 || result.TotalFiles != 3)
            {
                Console.WriteLine("FAIL totals bytes=" + result.TotalBytes + " files=" + result.TotalFiles);
                return 1;
            }
            // dir1 = 300, dir2 = 300（按大小降序）
            if (result.Children.Count != 2)
            {
                Console.WriteLine("FAIL children count=" + result.Children.Count);
                return 1;
            }
            Console.WriteLine("OK scan totals: dirs=" + result.Children.Count + " files=" + result.TotalFiles + " bytes=" + result.TotalBytes);

            // 扫描根节点大小应等于 total
            if (result.RootNode.TotalSize != 600)
            {
                Console.WriteLine("FAIL root total=" + result.RootNode.TotalSize);
                return 1;
            }
            Console.WriteLine("ALL TESTS PASSED");
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