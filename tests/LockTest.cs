using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FC.Services;

class LockTest
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_lock_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            string file = Path.Combine(root, "locked.bin");
            File.WriteAllBytes(file, new byte[100]);

            // 独占打开（模拟占用者）
            using (var fs = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                // 占用期间删除应失败
                var failed = new List<string>();
                FileDeleter.DeleteFile(file, failed);
                if (File.Exists(file))
                {
                    Console.WriteLine("OK delete blocked while file held open");
                }
                else
                {
                    Console.WriteLine("FAIL file deleted despite exclusive handle");
                    return 1;
                }
            }

            // 释放后删除应成功
            var failed2 = new List<string>();
            FileDeleter.DeleteFile(file, failed2);
            if (!File.Exists(file) && failed2.Count == 0)
            {
                Console.WriteLine("OK delete succeeds after handle released");
            }
            else
            {
                Console.WriteLine("FAIL delete after release, failed=" + failed2.Count);
                return 1;
            }

            Console.WriteLine("ALL LOCK TESTS PASSED");
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