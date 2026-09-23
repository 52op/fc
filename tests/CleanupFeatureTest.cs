using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using FC.Models;
using FC.Services;

class CleanupFeatureTest
{
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_feat_" + Guid.NewGuid().ToString("N"));
        string cacheFile = Path.Combine(Path.GetTempPath(), "fc_featcache_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "video"));
            Directory.CreateDirectory(Path.Combine(root, "docs"));

            File.WriteAllBytes(Path.Combine(root, "video", "movie.mp4"), new byte[9 * 1024 * 1024]);
            File.WriteAllBytes(Path.Combine(root, "video", "clip.mp4"), new byte[3000]);
            File.WriteAllBytes(Path.Combine(root, "docs", "note.txt"), new byte[2000]);
            File.WriteAllBytes(Path.Combine(root, "docs", "readme.txt"), new byte[1000]);

            // ===== B4 + B5: full scan =====
            var scanner = new Scanner();
            var result = scanner.ScanAsync(root, null, CancellationToken.None).Result;

            if (result.LargeFiles == null || result.LargeFiles.Count == 0 ||
                result.LargeFiles[0].Name != "movie.mp4" ||
                result.LargeFiles[0].Size != 9L * 1024 * 1024)
            {
                Console.WriteLine("FAIL B4: large files top=" +
                    (result.LargeFiles != null && result.LargeFiles.Count > 0 ? result.LargeFiles[0].Name : "empty"));
                return 1;
            }
            if (!result.LargeFilesComplete)
            {
                Console.WriteLine("FAIL B4: LargeFilesComplete should be true on full scan");
                return 1;
            }
            Console.WriteLine("OK B4: large files top=" + result.LargeFiles[0].Name + " count=" + result.LargeFiles.Count);

            if (result.TypeStats == null || result.TypeStats.Count == 0)
            {
                Console.WriteLine("FAIL B5: TypeStats empty");
                return 1;
            }
            var mp4 = result.TypeStats.FirstOrDefault(t => t.Extension == ".mp4");
            if (mp4.Extension != ".mp4" || mp4.Count != 2 || mp4.Bytes != 9L * 1024 * 1024 + 3000)
            {
                Console.WriteLine("FAIL B5: mp4 count=" + mp4.Count + " bytes=" + mp4.Bytes);
                return 1;
            }
            if (result.TypeStats[0].Extension != ".mp4")
            {
                Console.WriteLine("FAIL B5: top type=" + result.TypeStats[0].Extension);
                return 1;
            }
            Console.WriteLine("OK B5: types=" + string.Join(",", result.TypeStats.Select(t => t.Extension + ":" + t.Bytes)));

            // ===== quick reuse: LargeFilesComplete=false =====
            var cache = new ScanCache(cacheFile);
            cache.Load();
            var s2 = new Scanner();
            s2.Cache = cache;
            s2.QuickReuseMode = false;
            s2.ScanAsync(root, null, CancellationToken.None).Wait();
            var s3 = new Scanner();
            s3.Cache = cache;
            s3.QuickReuseMode = true;
            var r3 = s3.ScanAsync(root, null, CancellationToken.None).Result;
            if (r3.ReusedCount > 0 && r3.LargeFilesComplete)
            {
                Console.WriteLine("FAIL: quick-reuse should mark LargeFilesComplete=false (reused=" + r3.ReusedCount + ")");
                return 1;
            }
            Console.WriteLine("OK quick reuse: reused=" + r3.ReusedCount + " LargeFilesComplete=" + r3.LargeFilesComplete);

            // ===== A1/A3: real cleanup =====
            string junk = Path.Combine(root, "junk");
            Directory.CreateDirectory(junk);
            Directory.CreateDirectory(Path.Combine(junk, "sub"));
            File.WriteAllBytes(Path.Combine(junk, "a.tmp"), new byte[5000]);
            File.WriteAllBytes(Path.Combine(junk, "sub", "b.tmp"), new byte[7000]);

            var item = new CleanupItem
            {
                Key = "test",
                Title = "TestDir",
                Description = "test",
                Paths = new[] { junk },
                Safety = CleanupSafety.Safe,
                Selected = true,
                IsSized = true,
                Bytes = 12000
            };
            var outcome = CleanupService.Clean(new List<CleanupItem> { item },
                new Progress<string>(s => { }), CancellationToken.None);

            if (Directory.Exists(junk) && Directory.EnumerateFileSystemEntries(junk).Any())
            {
                Console.WriteLine("FAIL A1: junk not emptied");
                return 1;
            }
            if (!Directory.Exists(junk))
            {
                Console.WriteLine("FAIL A1: junk directory itself should be preserved");
                return 1;
            }
            if (outcome.FailedPaths.Count != 0)
            {
                Console.WriteLine("FAIL A1: failed paths=" + outcome.FailedPaths.Count);
                return 1;
            }
            Console.WriteLine("OK A1/A3: contents emptied, dir preserved, freed=" + outcome.FreedBytes);

            // ===== recyclebin item presence =====
            var items = CleanupService.BuildItems();
            if (!items.Any(i => i.Special == "recyclebin"))
            {
                Console.WriteLine("FAIL: recyclebin item missing");
                return 1;
            }
            Console.WriteLine("OK cleanup items=" + items.Count + " (incl recycle bin)");

            Console.WriteLine("ALL FEATURE TESTS PASSED");
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