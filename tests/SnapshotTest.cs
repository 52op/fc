using System;
using System.IO;
using System.Linq;
using System.Threading;
using FC.Services;

class SnapshotTest
{
    private static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "fc_snap_" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(Path.GetTempPath(), "fc_snapfile_" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, "x"));
            Directory.CreateDirectory(Path.Combine(dir, "y"));
            File.WriteAllBytes(Path.Combine(dir, "x", "a.bin"), new byte[10]);
            File.WriteAllBytes(Path.Combine(dir, "y", "b.bin"), new byte[20]);

            var s = new Scanner();
            var r1 = s.ScanAsync(dir, null, CancellationToken.None).Result;

            var snap1 = ScanSnapshot.Capture(r1.RootNode);
            ScanSnapshot.Save(file, snap1);

            var loaded = ScanSnapshot.Load(file);
            if (loaded == null || loaded.Entries.Count != snap1.Entries.Count)
            {
                Console.WriteLine("FAIL: save/load roundtrip count=" + (loaded == null ? "-1" : "" + loaded.Entries.Count));
                return 1;
            }
            if (!string.Equals(loaded.Target, r1.RootNode.FullPath, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("FAIL: target mismatch");
                return 1;
            }
            Console.WriteLine("OK save/load roundtrip entries=" + loaded.Entries.Count);

            // 变化：新增目录 w(50)，y 增文件 c(50)，x 删文件 a.bin（10→0）
            Directory.CreateDirectory(Path.Combine(dir, "w"));
            File.WriteAllBytes(Path.Combine(dir, "w", "w.bin"), new byte[50]);
            File.WriteAllBytes(Path.Combine(dir, "y", "c.bin"), new byte[50]);
            File.Delete(Path.Combine(dir, "x", "a.bin"));

            var r2 = s.ScanAsync(dir, null, CancellationToken.None).Result;
            var snap2 = ScanSnapshot.Capture(r2.RootNode);
            var diffs = SnapshotDiff.Compute(loaded, snap2, 50);

            var w = diffs.FirstOrDefault(d => string.Equals(d.Path, Path.Combine(dir, "w"), StringComparison.OrdinalIgnoreCase));
            if (w == null || !w.IsNew || w.DeltaBytes != 50)
            {
                Console.WriteLine("FAIL: w new delta=" + (w == null ? "null" : "" + w.DeltaBytes) + " isNew=" + (w == null ? "?" : "" + w.IsNew));
                return 1;
            }
            var y = diffs.FirstOrDefault(d => string.Equals(d.Path, Path.Combine(dir, "y"), StringComparison.OrdinalIgnoreCase));
            if (y == null || y.DeltaBytes != 50)
            {
                Console.WriteLine("FAIL: y delta=" + (y == null ? "null" : "" + y.DeltaBytes));
                return 1;
            }
            var x = diffs.FirstOrDefault(d => string.Equals(d.Path, Path.Combine(dir, "x"), StringComparison.OrdinalIgnoreCase));
            if (x == null || x.DeltaBytes != -10)
            {
                Console.WriteLine("FAIL: x delta=" + (x == null ? "null" : "" + x.DeltaBytes));
                return 1;
            }
            // 排序：绝对值最大的在前
            int pos = diffs.FindIndex(d => d.DeltaBytes == -10);
            int pos50 = diffs.FindIndex(d => d.DeltaBytes == 50);
            if (pos50 < 0 || pos < 0 || pos50 > pos)
            {
                Console.WriteLine("FAIL: order pos50=" + pos50 + " pos-10=" + pos);
                return 1;
            }
            Console.WriteLine("OK diff: " + string.Join(" | ", diffs.Take(4).Select(d => d.Path.Replace(dir, ".") + ":" + d.DeltaBytes)));

            // 删除整目录
            Directory.Delete(Path.Combine(dir, "x"), true);
            var r3 = s.ScanAsync(dir, null, CancellationToken.None).Result;
            var diffs3 = SnapshotDiff.Compute(snap1, ScanSnapshot.Capture(r3.RootNode), 50);
            var xr = diffs3.FirstOrDefault(d => string.Equals(d.Path, Path.Combine(dir, "x"), StringComparison.OrdinalIgnoreCase));
            if (xr == null || !xr.IsRemoved)
            {
                Console.WriteLine("FAIL: removed dir x not marked");
                return 1;
            }
            Console.WriteLine("OK removed directory marked isRemoved");

            Console.WriteLine("ALL SNAPSHOT TESTS PASSED");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine("EXCEPTION: " + ex);
            return 2;
        }
        finally
        {
            TryDelete(dir);
            TryDeleteFile(file);
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