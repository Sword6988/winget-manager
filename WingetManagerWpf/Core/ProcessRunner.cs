using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WingetManagerWpf.Core
{
    /// <summary>一次子进程执行的结果（原始字节，未解码）。</summary>
    public sealed class ProcessRunResult
    {
        public ProcessRunResult(int exitCode, byte[] standardOutput, byte[] standardError, int durationMs)
        {
            ExitCode = exitCode;
            StandardOutput = standardOutput ?? new byte[0];
            StandardError = standardError ?? new byte[0];
            DurationMs = durationMs;
        }

        public int ExitCode { get; private set; }

        /// <summary>stdout 原始字节（**故意不解码**：编码判定统一在 EncodedText 里做）。</summary>
        public byte[] StandardOutput { get; private set; }

        /// <summary>stderr 原始字节。</summary>
        public byte[] StandardError { get; private set; }

        public int DurationMs { get; private set; }
    }

    /// <summary>
    /// 子进程执行器（唯一与 Process 打交道的地方）。要点：
    /// <list type="number">
    /// <item><c>UseShellExecute=false</c> + <c>CreateNoWindow=true</c>：不弹黑框、能重定向；</item>
    /// <item>**不设** <c>StandardOutputEncoding</c>：一旦设定就锁死编码，破坏
    /// 「utf-8-sig → utf-8 → GBK」的优先级（易错点 10），所以只收字节；</item>
    /// <item>stdout/stderr **各起一个 Task 并发读** <c>BaseStream</c>：winget 输出超过管道
    /// 缓冲区（约 4KB）时，单线程顺序读会双向阻塞，扫描永远不返回（易错点 11）；</item>
    /// <item>200ms 轮询 <c>WaitForExit(200)</c>：.NET Framework 4.8 没有
    /// <c>WaitForExitAsync</c>，也没有 <c>Kill(entireProcessTree)</c>。</item>
    /// </list>
    /// </summary>
    public static class ProcessRunner
    {
        /// <summary>取消后等待子进程退出的上限（超过则 taskkill /T 强杀整棵树）。</summary>
        public const int KillGraceMs = 800;

        /// <summary>
        /// 启动并等待子进程。
        /// </summary>
        /// <exception cref="WingetNotFoundException">exe 不存在或无法启动（Win32 错误码 2/3）。</exception>
        /// <exception cref="WingetCommandFailedException">超时 / 其他启动失败。</exception>
        /// <exception cref="ScanCancelledException">用户取消。</exception>
        public static ProcessRunResult Run(string exePath, string[] args, int timeoutSeconds,
                                           CancellationToken cancellationToken, AppLogger log)
        {
            if (string.IsNullOrEmpty(exePath))
            {
                throw new WingetNotFoundException("未指定可执行文件路径。");
            }

            int timeout = timeoutSeconds > 0 ? timeoutSeconds : AppConfig.DefaultCommandTimeout;
            string argumentLine = BuildArgumentLine(args);

            var startInfo = new ProcessStartInfo(exePath, argumentLine)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            Process process;
            try
            {
                process = Process.Start(startInfo);
            }
            catch (Win32Exception ex)
            {
                // 2 = 找不到文件，3 = 找不到路径 → 按「没装 winget」处理，给出安装引导
                if (ex.NativeErrorCode == 2 || ex.NativeErrorCode == 3)
                {
                    throw new WingetNotFoundException("无法执行 " + exePath + "：" + ex.Message);
                }

                throw new WingetCommandFailedException("无法执行 " + exePath + "：" + ex.Message);
            }
            catch (Exception ex)
            {
                throw new WingetCommandFailedException("无法执行 " + exePath + "：" + ex.Message);
            }

            if (process == null)
            {
                throw new WingetCommandFailedException("无法执行 " + exePath + "（Process.Start 返回 null）。");
            }

            var stopwatch = Stopwatch.StartNew();
            Task<byte[]> stdoutTask = null;
            Task<byte[]> stderrTask = null;

            try
            {
                // 必须并发读：单个线程先读 stdout 会把 stderr 的管道写满，反之亦然
                stdoutTask = Task.Run(() => ReadAll(process.StandardOutput.BaseStream));
                stderrTask = Task.Run(() => ReadAll(process.StandardError.BaseStream));

                bool timedOut = false;
                while (true)
                {
                    if (process.WaitForExit(Metrics.PollMs))
                    {
                        break;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        KillTree(process);
                        throw new ScanCancelledException("winget 命令已取消：" + argumentLine);
                    }

                    if (stopwatch.Elapsed.TotalSeconds >= timeout)
                    {
                        timedOut = true;
                        KillTree(process);
                        break;
                    }
                }

                byte[] outBytes = Collect(stdoutTask);
                byte[] errBytes = Collect(stderrTask);
                int duration = (int)stopwatch.ElapsedMilliseconds;

                if (timedOut)
                {
                    throw new WingetCommandFailedException(
                        "winget 命令超时（>" + timeout.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + "s，含重试退避）：" + argumentLine);
                }

                int exitCode;
                try
                {
                    exitCode = process.ExitCode;
                }
                catch (InvalidOperationException)
                {
                    throw new WingetCommandFailedException("winget 进程未正常结束：" + argumentLine);
                }

                return new ProcessRunResult(exitCode, outBytes, errBytes, duration);
            }
            finally
            {
                // 任何异常路径都不能把子进程留在后台偷偷跑（否则下一次调用会撞互斥）
                try
                {
                    if (!process.HasExited)
                    {
                        KillTree(process);
                    }
                }
                catch (Exception)
                {
                    // 已经退出 / 句柄已释放
                }

                try
                {
                    process.Dispose();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }
        }

        /// <summary>
        /// 参数拼装：.NET Framework 无 <c>ProcessStartInfo.ArgumentList</c>，
        /// 含空格/引号的参数必须自己加双引号并转义反斜杠（易错点 13）。
        /// </summary>
        public static string BuildArgumentLine(string[] args)
        {
            if (args == null || args.Length == 0)
            {
                return string.Empty;
            }

            var sb = new StringBuilder(128);
            for (int i = 0; i < args.Length; i++)
            {
                if (i > 0)
                {
                    sb.Append(' ');
                }

                sb.Append(QuoteArgument(args[i]));
            }

            return sb.ToString();
        }

        /// <summary>按 Windows 命令行解析规则给单个参数加引号（MSDN「Parsing C++ Command-Line Arguments」）。</summary>
        public static string QuoteArgument(string argument)
        {
            if (string.IsNullOrEmpty(argument))
            {
                return "\"\"";
            }

            bool needQuote = argument.IndexOf(' ') >= 0
                             || argument.IndexOf('\t') >= 0
                             || argument.IndexOf('"') >= 0;

            if (!needQuote)
            {
                return argument;
            }

            var sb = new StringBuilder(argument.Length + 8);
            sb.Append('"');
            int backslashes = 0;
            for (int i = 0; i < argument.Length; i++)
            {
                char ch = argument[i];
                if (ch == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (ch == '"')
                {
                    // 引号前的反斜杠要双写，再补一个转义引号
                    sb.Append('\\', (backslashes * 2) + 1);
                    sb.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    sb.Append('\\', backslashes);
                    backslashes = 0;
                }

                sb.Append(ch);
            }

            if (backslashes > 0)
            {
                // 结尾的反斜杠要双写，否则会把收尾引号转义掉
                sb.Append('\\', backslashes * 2);
            }

            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>把流读到内存。读到 IOException（管道被关）就当作结束，不抛。</summary>
        private static byte[] ReadAll(Stream stream)
        {
            using (var buffer = new MemoryStream(4096))
            {
                var chunk = new byte[8192];
                while (true)
                {
                    int read;
                    try
                    {
                        read = stream.Read(chunk, 0, chunk.Length);
                    }
                    catch (IOException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }

                    if (read <= 0)
                    {
                        break;
                    }

                    buffer.Write(chunk, 0, read);
                }

                return buffer.ToArray();
            }
        }

        private static byte[] Collect(Task<byte[]> task)
        {
            if (task == null)
            {
                return new byte[0];
            }

            try
            {
                // 进程已退出，管道应立刻 EOF；给 2s 余量防止极端情况下永久阻塞
                if (task.Wait(2000))
                {
                    return task.Result ?? new byte[0];
                }
            }
            catch (AggregateException)
            {
                // 读取任务自身出错（流异常）：按空输出处理，上层会走「无输出」分支
            }

            return new byte[0];
        }

        /// <summary>
        /// 先 Kill，再给它 <see cref="KillGraceMs"/> 退出；还不退就用
        /// <c>taskkill /F /T</c> 连子进程树一起清掉（4.8 没有 Kill(entireProcessTree)）。
        /// </summary>
        private static void KillTree(Process process)
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (process.HasExited)
                {
                    return;
                }
            }
            catch (Exception)
            {
                return;
            }

            int processId;
            try
            {
                processId = process.Id;
            }
            catch (Exception)
            {
                processId = -1;
            }

            try
            {
                process.Kill();
            }
            catch (Exception)
            {
                // 已被系统回收 / 无权限
            }

            try
            {
                if (process.WaitForExit(KillGraceMs))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // 继续走 taskkill 兜底
            }

            if (processId <= 0)
            {
                return;
            }

            try
            {
                var killInfo = new ProcessStartInfo("taskkill", "/F /T /PID " + processId.ToString(System.Globalization.CultureInfo.InvariantCulture))
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (Process killer = Process.Start(killInfo))
                {
                    if (killer != null)
                    {
                        killer.WaitForExit(1500);
                    }
                }
            }
            catch (Exception)
            {
                // 兜底也失败只能算了：主进程已 Kill，残留概率极低
            }
        }
    }
}
