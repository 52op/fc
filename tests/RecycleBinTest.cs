using System;
using System.IO;
using System.Linq;
using FC.Services;

class RecycleBinTest
{
    private static int Main()
    {
        // 1) 估算不抛异常且非负（回归：SHQueryRecycleBin 曾返回 13.8TB 垃圾值）
        long est = CleanupService.EstimateRecycleBin();
        if (est < 0)
        {
            Console.WriteLine("FAIL: negative recycle bin estimate=" + est);
            return 1;
        }

        // 2) 与逐盘 $Recycle.Bin 流式求和交叉验证（同一算法，应一致）
        long manual = 0;
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                string rb = Path.Combine(d.RootDirectory.FullName, "$Recycle.Bin");
                if (Directory.Exists(rb))
                {
                    long s = FastDirectory.SumSizeRecursive(rb);
                    manual += s;
                    Console.WriteLine("  drive " + d.Name.TrimEnd('\\') + " : " + FC.Converters.SizeText.Format(s));
                }
            }
            catch (Exception) { }
        }
        if (est != manual)
        {
            Console.WriteLine("FAIL: estimate=" + est + " manual=" + manual);
            return 1;
        }
        Console.WriteLine("OK recycle bin estimate=" + FC.Converters.SizeText.Format(est) + " (real, matches manual)");

        Console.WriteLine("ALL RECYCLEBIN TESTS PASSED");
        return 0;
    }
}