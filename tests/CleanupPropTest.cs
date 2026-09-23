using System;
using System.Linq;
using FC.Services;

class CleanupPropTest
{
    [STAThread]
    private static int Main()
    {
        // 属性绑定值
        const string PENDING = "\u7EDF\u8BA1\u4E2D";   // 统计中
        const string UNKNOWN = "\u672A\u77E5";          // 未知
        CleanupItem item = new CleanupItem();
        item.Title = "r";
        item.Description = "d";
        item.Bytes = 1234;
        item.IsSized = true;
        if (item.Title != "r" || item.Description == null)
        {
            Console.WriteLine("FAIL: Title/Description getter");
            return 1;
        }
        if (item.SizeText.Contains(PENDING))
        {
            Console.WriteLine("FAIL: Sized item still shows pending: " + item.SizeText);
            return 1;
        }

        // 0 字节 = "0 B"
        CleanupItem zero = new CleanupItem();
        zero.Bytes = 0;
        zero.IsSized = true;
        if (zero.SizeText != "0 B")
        {
            Console.WriteLine("FAIL: zero sized text=" + zero.SizeText);
            return 1;
        }

        // 失败 = 未知
        CleanupItem failed = new CleanupItem();
        failed.Bytes = -1;
        failed.IsSized = true;
        if (!failed.SizeText.Equals(UNKNOWN))
        {
            Console.WriteLine("FAIL: failed text=" + failed.SizeText);
            return 1;
        }

        // 未统计 = 统计中
        CleanupItem busy = new CleanupItem();
        busy.Bytes = 0;
        busy.IsSized = false;
        if (!busy.SizeText.Contains(PENDING))
        {
            Console.WriteLine("FAIL: busy text=" + busy.SizeText);
            return 1;
        }

        // BuildItems 每个都有非空属性
        var items = CleanupService.BuildItems();
        if (items.Count == 0)
        {
            Console.WriteLine("FAIL: BuildItems empty");
            return 1;
        }
        foreach (var it in items)
        {
            if (string.IsNullOrEmpty(it.Title) || string.IsNullOrEmpty(it.Description))
            {
                Console.WriteLine("FAIL: item null title/desc key=" + it.Key);
                return 1;
            }
        }

        Console.WriteLine("OK items=" + items.Count);
        Console.WriteLine("ALL CLEANUP-PROP TESTS PASSED");
        return 0;
    }
}