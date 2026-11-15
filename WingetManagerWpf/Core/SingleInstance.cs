using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// 单实例：<c>Local\</c> 命名互斥量 + 命名管道激活。
    /// <list type="bullet">
    /// <item>第二实例不起窗口，只往管道写 <c>SHOW</c>，第一实例收到后把窗口提到前台；</item>
    /// <item>用 <c>Local\</c> 而不是 <c>Global\</c>：受限账户下 <c>Global\</c> 可能没权限（易错点 25）；</item>
    /// <item><see cref="Mutex"/> 必须由字段强引用到进程结束，否则被 GC 回收后单实例就失效了。</item>
    /// </list>
    /// 已知差异：原版用 QLocalServer，本版用命名管道，行为对等、实现不同。
    /// </summary>
    public sealed class SingleInstance : IDisposable
    {
        /// <summary>
        /// 互斥量名。不用 const 而用属性：便携模式下要按数据目录加后缀，
        /// 让不同目录的绿色副本各自独立（否则互相抢窗口）。
        /// </summary>
        public static string DefaultMutexName
        {
            get { return @"Local\WingetManagerWpf.SingleInstance" + AppPaths.InstanceScopeSuffix; }
        }

        /// <summary>命名管道名，作用域后缀与互斥量保持一致。</summary>
        public static string DefaultPipeName
        {
            get { return "WingetManagerWpf.Activate" + AppPaths.InstanceScopeSuffix; }
        }

        public const string ShowCommand = "SHOW";

        private readonly string _pipeName;
        private readonly Mutex _mutex;
        private readonly bool _isFirst;
        private readonly ManualResetEventSlim _stopping = new ManualResetEventSlim(false);

        private Thread _serverThread;
        private bool _disposed;

        public SingleInstance()
            : this(DefaultMutexName, DefaultPipeName)
        {
        }

        public SingleInstance(string mutexName, string pipeName)
        {
            _pipeName = pipeName;

            bool createdNew;
            _mutex = new Mutex(true, mutexName, out createdNew);
            _isFirst = createdNew;

            if (!createdNew)
            {
                // 没抢到所有权就别留着句柄（我们并没有持有它）
                try
                {
                    _mutex.Dispose();
                }
                catch (Exception)
                {
                    // 句柄释放失败不影响流程
                }

                _mutex = null;
            }
        }

        /// <summary>本进程是不是第一个实例。</summary>
        public bool IsFirstInstance
        {
            get { return _isFirst; }
        }

        /// <summary>收到第二实例的激活请求（**在后台线程触发**，UI 必须自己 Dispatcher 回主线程）。</summary>
        public event Action Activated;

        /// <summary>启动管道服务端（只在第一实例调用；幂等）。</summary>
        public void StartServer()
        {
            if (!_isFirst || _serverThread != null || _disposed)
            {
                return;
            }

            _serverThread = new Thread(ServerLoop);
            _serverThread.IsBackground = true;
            _serverThread.Name = "WingetManagerWpf.SingleInstance";
            _serverThread.Start();
        }

        /// <summary>
        /// 通知已有实例把窗口提到前台。失败（没有实例 / 管道没建好）返回 false，
        /// 调用方可选择继续启动第二个实例而不是静默退出。
        /// </summary>
        public static bool TrySendShow(string pipeName, int timeoutMs)
        {
            string name = string.IsNullOrEmpty(pipeName) ? DefaultPipeName : pipeName;
            int deadline = Environment.TickCount + Math.Max(200, timeoutMs);

            while (Environment.TickCount <= deadline)
            {
                NamedPipeClientStream client = null;
                try
                {
                    client = new NamedPipeClientStream(".", name, PipeDirection.Out);
                    // 服务端可能还在初始化，用短超时反复重试而不是一次放弃
                    client.Connect(300);
                    byte[] payload = Encoding.UTF8.GetBytes(ShowCommand + "\n");
                    client.Write(payload, 0, payload.Length);
                    client.Flush();
                    return true;
                }
                catch (Exception)
                {
                    // 继续重试直到超时
                }
                finally
                {
                    if (client != null)
                    {
                        try
                        {
                            client.Dispose();
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                    }
                }

                try
                {
                    Thread.Sleep(100);
                }
                catch (ThreadInterruptedException)
                {
                    return false;
                }
            }

            return false;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopping.Set();

            if (_serverThread != null)
            {
                try
                {
                    // 服务端在 WaitForConnectionAsync().Wait(200) 里轮询，200ms 内就会看到停止标志
                    _serverThread.Join(800);
                }
                catch (Exception)
                {
                    // 忽略
                }

                _serverThread = null;
            }

            if (_isFirst && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // 不是本线程持有的（极少见）：直接释放句柄即可
                }
                catch (ObjectDisposedException)
                {
                    // 已释放
                }

                try
                {
                    _mutex.Dispose();
                }
                catch (Exception)
                {
                    // 忽略
                }
            }

            _stopping.Dispose();
        }

        private void ServerLoop()
        {
            while (!_stopping.IsSet)
            {
                NamedPipeServerStream server = null;
                try
                {
                    server = new NamedPipeServerStream(
                        _pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.None);

                    Task wait = server.WaitForConnectionAsync();
                    // 用 200ms 轮询替代阻塞等待：否则 Dispose 时管线会卡在 WaitForConnection 上
                    while (!wait.Wait(200))
                    {
                        if (_stopping.IsSet)
                        {
                            return;
                        }
                    }

                    if (wait.IsFaulted)
                    {
                        // 客户端半路断开会抛到这里，属于正常情况，重建管线继续等
                        continue;
                    }

                    using (var reader = new StreamReader(server, Encoding.UTF8))
                    {
                        string line = reader.ReadLine();
                        if (!string.IsNullOrEmpty(line)
                            && line.Trim().Equals(ShowCommand, StringComparison.OrdinalIgnoreCase))
                        {
                            Action handler = Activated;
                            if (handler != null)
                            {
                                try
                                {
                                    handler();
                                }
                                catch (Exception)
                                {
                                    // 订阅方异常不能打死服务端线程
                                }
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // 管道被占用/被关闭等：稍等后重建，避免忙等烧 CPU
                    _stopping.Wait(200);
                }
                finally
                {
                    if (server != null)
                    {
                        try
                        {
                            server.Dispose();
                        }
                        catch (Exception)
                        {
                            // 忽略
                        }
                    }
                }
            }
        }
    }
}
