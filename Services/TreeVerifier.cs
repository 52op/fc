using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FC.Services
{
    /// <summary>
    /// 用 .NET 自身的文件枚举做校验（不依赖 robocopy 的 /L 语义）：
    /// 迭代建相对路径字典，比对存在性与大小。
    /// 使用显式栈而非递归，避免深目录栈溢出。
    /// </summary>
    public class TreeVerifier : IVerifier
    {
        private struct FileEntry
        {
            public long Size;
            public DateTime LastWrite;
        }

        public Task<VerificationReport> VerifyAsync(string src, string dst, CancellationToken ct)
        {
            return Task.Run(() =>
            {
                var a = BuildMap(src, ct);
                var b = BuildMap(dst, ct);
                return Compare(a, b);
            }, ct);
        }

        private static Dictionary<string, FileEntry> BuildMap(string root, CancellationToken ct)
        {
            var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string dir = stack.Pop();

                string[] dirs;
                string[] files;
                try
                {
                    dirs = Directory.GetDirectories(dir);
                    files = Directory.GetFiles(dir);
                }
                catch (Exception)
                {
                    map["<unreadable:" + dir + ">"] = new FileEntry { Size = -1 };
                    continue;
                }

                foreach (string f in files)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var fi = new FileInfo(f);
                        if (!fi.Exists)
                        {
                            continue;
                        }
                        string rel = f.Substring(root.Length).TrimStart('\\');
                        map[rel] = new FileEntry { Size = fi.Length, LastWrite = fi.LastWriteTimeUtc };
                    }
                    catch (Exception)
                    {
                        // 个别文件失败：记一个差异键，让上层发现不一致
                        map["<unreadable-file:" + f + ">"] = new FileEntry { Size = -1 };
                    }
                }

                foreach (string d in dirs)
                {
                    if (JunctionUtil.IsReparsePoint(d))
                    {
                        continue; // 不跟随联接
                    }
                    stack.Push(d);
                }
            }

            return map;
        }

        private static VerificationReport Compare(
            Dictionary<string, FileEntry> a, Dictionary<string, FileEntry> b)
        {
            var report = new VerificationReport();
            bool ok = true;

            foreach (var kv in a)
            {
                if (kv.Value.Size >= 0)
                {
                    report.SourceFiles++;
                    report.SourceBytes += kv.Value.Size;
                }
            }
            foreach (var kv in b)
            {
                if (kv.Value.Size >= 0)
                {
                    report.TargetFiles++;
                    report.TargetBytes += kv.Value.Size;
                }
            }

            foreach (var kv in a)
            {
                if (report.Differences.Count >= 50)
                {
                    break;
                }

                FileEntry be;
                if (!b.TryGetValue(kv.Key, out be))
                {
                    ok = false;
                    report.Differences.Add("目标缺少：" + kv.Key);
                }
                else if (be.Size != kv.Value.Size)
                {
                    ok = false;
                    report.Differences.Add("大小不一致：" + kv.Key);
                }
            }

            foreach (var kv in b)
            {
                if (report.Differences.Count >= 50)
                {
                    break;
                }
                if (!a.ContainsKey(kv.Key))
                {
                    report.Differences.Add("目标多出（警告）：" + kv.Key);
                }
            }

            report.Ok = ok;
            return report;
        }
    }
}
