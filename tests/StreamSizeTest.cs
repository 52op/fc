using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FC.Models;
using FC.Services;

class StreamSizeTest
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_strm_" + Guid.NewGuid().ToString("N"));

        // 造 5 层目录、每层若干文件，总字节已知
        Directory.CreateDirectory(root);
        long expected = 0;
        string curDir = root;
        for (int lvl = 0; lvl < 5; lvl++)
        {
            curDir = Path.Combine(curDir, "level" + lvl);
            Directory.CreateDirectory(curDir);
            for (int f = 0; f < 20; f++)
            {
                int sz = 100 + lvl * 10 + f;
                File.WriteAllBytes(Path.Combine(curDir, "f" + f + ".bin"), new byte[sz]);
                expected += sz;
            }
        }
        // exact sum
        long manualSum = 0;
        Queue<string> q = new Queue<string>();
        q.Enqueue(root);
        while (q.Count > 0)
        {
            var dir = q.Dequeue();
            foreach (var fi in new DirectoryInfo(dir).GetFiles()) manualSum += fi.Length;
            foreach (var sub in new DirectoryInfo(dir).GetDirectories()) q.Enqueue(sub.FullName);
        }

        // 1) 流式求和与扫描器(FindFirstFileEx List 方式)同源，应与 manualSum 一致
        long fast = FastDirectory.SumSizeRecursive(root);
        if (fast != manualSum)
        {
            Console.WriteLine("FAIL sum: fast=" + fast + " manual=" + manualSum);
            return 1;
        }
        Console.WriteLine("OK SumSizeRecursive=" + fast + " == manual=" + manualSum);

        // 2) RefreshSizes 并发限制仍正确（可清理项估算）
        var item = new CleanupItem
        {
            Key = "t",
            Title = "t",
            Description = "d",
            Paths = new[] { root },
            Safety = CleanupSafety.Safe,
            Selected = true
        };
        CleanupService.RefreshSizes(new List<CleanupItem> { item },
            new Progress<string>(s => { }), CancellationToken.None);
        if (!item.IsSized || item.Bytes != manualSum)
        {
            Console.WriteLine("FAIL RefreshSizes item=" + item.Bytes + " expected=" + manualSum);
            return 1;
        }
        Console.WriteLine("OK RefreshSizes bytes=" + item.Bytes);

        // 3) 并发 3：7 个项并行估算，全部完成且结果正确
        var items = new List<CleanupItem>();
        for (int i = 0; i < 7; i++)
        {
            var sub = Path.Combine(root, "i" + i);
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "x.bin"), new byte[10 + i]);
            items.Add(new CleanupItem
            {
                Key = "i" + i,
                Title = "i" + i,
                Description = "d",
                Paths = new[] { sub },
                Safety = CleanupSafety.Safe,
                Selected = true
            });
        }
        CleanupService.RefreshSizes(items, new Progress<string>(s => { }), CancellationToken.None);
        for (int i = 0; i < 7; i++)
        {
            if (!items[i].IsSized || items[i].Bytes != 10 + i)
            {
                Console.WriteLine("FAIL concurrent item " + i + " sized=" + items[i].IsSized + " bytes=" + items[i].Bytes);
                return 1;
            }
        }
        Console.WriteLine("OK concurrent 7 items all sized correctly");

        Console.WriteLine("ALL STREAM-SIZE TESTS PASSED");
        return 0;
    }

    private static void TryDelete(string p)
    {
        try { if (p != null && Directory.Exists(p)) { Directory.Delete(p, true); } }
        catch (Exception) { }
    }
}