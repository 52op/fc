using System;
using FC.ViewModels;

class CleanupProgressTest
{
    [STAThread]
    private static int Main()
    {
        // 项开始行 → title
        string t;
        bool done;
        if (!CleanupViewModel.TryParseCleanProgress("=== 回收站 ===", out t, out done))
        {
            Console.WriteLine("FAIL: start line not parsed");
            return 1;
        }
        if (done || t != "回收站")
        {
            Console.WriteLine("FAIL: start title=" + t + " done=" + done);
            return 1;
        }
        // 完成行 → done
        if (!CleanupViewModel.TryParseCleanProgress("\u8BE5\u9879\u6E05\u7406\u5B8C\u6210（释放约 0 B）。", out t, out done))
        {
            Console.WriteLine("FAIL: done line not parsed");
            return 1;
        }
        if (!done || t != null)
        {
            Console.WriteLine("FAIL: done flag=" + done);
            return 1;
        }
        // 无关行不解析
        if (CleanupViewModel.TryParseCleanProgress("正在统计可清理空间…", out t, out done))
        {
            Console.WriteLine("FAIL: unrelated line should return false");
            return 1;
        }
        // 空行
        if (CleanupViewModel.TryParseCleanProgress(null, out t, out done))
        {
            Console.WriteLine("FAIL: null line should return false");
            return 1;
        }
        Console.WriteLine("ALL CLEANUP-PROGRESS TESTS PASSED");
        return 0;
    }
}