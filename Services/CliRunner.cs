using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FC.Services
{
    public class CliResult
    {
        public int ExitCode { get; set; }

        public bool Cancelled { get; set; }

        public List<string> OutputLines { get; set; }

        public CliResult()
        {
            OutputLines = new List<string>();
        }
    }

    /// <summary>
    /// 通用命令行执行器：异步启动进程、逐行转发 stdout/stderr、支持取消（Kill）。
    /// 用于 robocopy（复制/回拷）。
    /// </summary>
    public class CliRunner
    {
        public async Task<CliResult> RunAsync(
            string fileName, string arguments,
            IProgress<string> lineSink, CancellationToken ct)
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            try
            {
                psi.StandardOutputEncoding = GetOemEncoding();
                psi.StandardErrorEncoding = GetOemEncoding();
            }
            catch (Exception)
            {
                // 编码不可用则用默认
            }

            var result = new CliResult();
            var queue = new ConcurrentQueue<string>();

            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };

            DataReceivedEventHandler onData = (s, e) =>
            {
                if (e.Data == null)
                {
                    return;
                }
                queue.Enqueue(e.Data);
                try
                {
                    if (lineSink != null)
                    {
                        lineSink.Report(e.Data);
                    }
                }
                catch (Exception)
                {
                    // 日志消费者抛错不影响进程读取
                }
            };

            process.OutputDataReceived += onData;
            process.ErrorDataReceived += onData;

            var exitSignal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.Exited += (s, e) => exitSignal.TrySetResult(true);

            try
            {
                if (!process.Start())
                {
                    result.ExitCode = -100;
                    result.OutputLines.Add("进程启动失败：" + fileName);
                    return result;
                }
            }
            catch (Exception ex)
            {
                result.ExitCode = -101;
                result.OutputLines.Add("进程启动异常：" + ex.Message);
                return result;
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using (ct.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception)
                {
                    // 进程可能已退出
                }
            }))
            {
                await exitSignal.Task.ConfigureAwait(false);

                // 进程退出后，再等一次 WaitForExit() 让异步输出事件全部处理完
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
            }

            result.Cancelled = ct.IsCancellationRequested;
            try
            {
                result.ExitCode = process.ExitCode;
            }
            catch (Exception)
            {
                result.ExitCode = -1;
            }

            string line;
            while (queue.TryDequeue(out line))
            {
                result.OutputLines.Add(line);
            }

            process.Dispose();
            return result;
        }

        private static Encoding GetOemEncoding()
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch (Exception)
            {
                return Encoding.Default;
            }
        }
    }
}
