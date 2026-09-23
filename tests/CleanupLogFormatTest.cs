using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using FC.Services;

class CleanupLogFormatTest
{
    [STAThread]
    private static int Main()
    {
        string root = Path.Combine(Path.GetTempPath(), "fc_logfmt_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            File.WriteAllBytes(Path.Combine(root, "a.tmp"), new byte[100]);
            var sb = new System.Text.StringBuilder();
            var item = new CleanupItem
            {
                Key = "t",
                Title = "LogFormatTarget",
                Description = "d",
                Paths = new[] { root },
                Safety = CleanupSafety.Safe,
                Selected = true
            };
            var logs = new List<string>();
            CleanupService.Clean(new List<CleanupItem> { item },
                new SyncProgress(logs.Add), CancellationToken.None);

            // Progress<T> 在无 SynchronizationContext 时会异步派发；直接回调避免竞态
            string log = string.Join("\n", logs);
            Console.WriteLine("LOG-RAW=[" + log + "]");
            if (!log.Contains("LogFormatTarget"))
            {
                Console.WriteLine("FAIL: missing item title line: " + log);
                return 1;
            }

            // VM 解析依赖的行格式：项开始 "=== 标题 ==="、完成 "该项清理完成（…）"
            // （中文用 Unicode 转义，避免源文件编码对 csc 的影响）
            const string DONE_PREFIX = "\u8BE5\u9879\u6E05\u7406\u5B8C\u6210"; // 该项清理完成
            bool hasStart = false;
            bool hasDone = false;
            using (var rd = new StringReader(log))
            {
                string line;
                while ((line = rd.ReadLine()) != null)
                {
                    string t = line.Trim();
                    if (t.StartsWith("=== ", StringComparison.Ordinal) && t.EndsWith(" ===", StringComparison.Ordinal))
                    {
                        hasStart = true;
                        Console.WriteLine("START-> " + t);
                    }
                    if (t.StartsWith(DONE_PREFIX, StringComparison.Ordinal))
                    {
                        hasDone = true;
                        Console.WriteLine("DONE -> " + t);
                    }
                }
            }
            if (!hasStart || !hasDone)
            {
                Console.WriteLine("FAIL: start=" + hasStart + " done=" + hasDone + " log:\n" + log);
                return 1;
            }
            Console.WriteLine("ALL LOG-FORMAT TESTS PASSED");
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

    /// <summary>同步 IProgress（无 SynchronizationContext 时 Progress&lt;T&gt; 会异步派发，测试需同步）。</summary>
    private sealed class SyncProgress : IProgress<string>
    {
        private readonly Action<string> _action;
        public SyncProgress(Action<string> action) { _action = action; }
        public void Report(string value) { _action(value); }
    }
}