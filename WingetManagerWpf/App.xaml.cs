using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using WingetManagerWpf.Core;

namespace WingetManagerWpf
{
    /// <summary>
    /// 应用入口与组合根：日志 → 全局异常兜底 → 启动配置 → 单实例 → 主窗口。
    /// <para>
    /// 依赖注入顺序不能颠倒：<see cref="MainWindow"/> 的构造函数会立刻用日志和配置
    /// 建出仓储/目录/扫描服务，所以 <c>_log</c> 与 <c>cfg</c> 必须先就绪；
    /// 单实例判定则要放在建窗口**之前**，否则第二实例会白建一个 800ms 才被关掉的窗口，
    /// 用户会看到任务栏闪一下。
    /// </para>
    /// </summary>
    public partial class App : Application
    {
        private AppLogger _log;

        /// <summary>
        /// 单实例对象必须由字段强引用到进程结束：局部变量被 GC 回收后 Mutex 句柄会释放，
        /// 单实例立刻失效（易错点 25）。
        /// </summary>
        private SingleInstance _instance;

        /// <summary>全局日志实例；UI 与 ViewModel 共用同一份，避免多份 AppLogger 争抢同一文件。</summary>
        public AppLogger Logger
        {
            get { return _log; }
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // 先显式关闭：单实例抢占失败或配置致命错误时可能一个窗口都没建，
            // 若此时已是 OnLastWindowClose，进程会在没有窗口的瞬间自行退出。
            ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // 便携模式首次运行时，把旧版留在用户目录里的勾选 / 配置复制过来。
            // 必须在 AppConfig.Load() 和日志初始化之前：否则读到的还是空配置。
            int migrated = AppPaths.MigrateLegacyDataIfNeeded();

            _log = AppLogger.Create();
            HookGlobalExceptionHandlers();

            _log.Info("Winget 软件管家 " + AppVersion.Version + " 启动（数据目录："
                      + AppPaths.DataDir + "）");

            if (!AppPaths.DataDirWritable)
            {
                // 数据目录不可写不该让程序起不来：所有存储会自动退回内存态。
                _log.Warn("数据目录不可写，勾选/缓存/窗口状态将只在本次运行内有效：" + AppPaths.DataDir);
            }

            if (AppPaths.IsPortable)
            {
                // 便携模式下数据跟着 exe 走，用户挪文件夹就等于「迁移」，日志里留个脚印便于排查
                _log.Info("便携模式：数据目录位于 exe 同目录下的 Data 文件夹。");
            }

            if (migrated > 0)
            {
                _log.Info("已从旧数据目录 " + AppPaths.LegacyDataDir + " 复制 "
                          + migrated.ToString() + " 个文件到便携目录（源文件保留）。");
            }

            AppConfig cfg;
            try
            {
                cfg = AppConfig.Load();
            }
            catch (ConfigInvalidException ex)
            {
                // 配置类错误不阻断启动，用默认值继续（与原版的容错口径一致）
                _log.Warn("配置不合法，改用默认值：" + ex.UserMessage + "（" + ex.Message + "）");
                cfg = AppConfig.CreateDefault();
            }

            if (string.IsNullOrEmpty(cfg.WingetExe))
            {
                _log.Warn("winget 探测失败：" + cfg.WingetError);
            }
            else
            {
                _log.Info("winget 可执行文件：" + cfg.WingetExe);
            }

            if (!AcquireSingleInstance())
            {
                // 已有实例且已请它显示窗口：本进程没有创建任何 UI，直接退出。
                _log.Info("已唤起运行中的窗口，本实例退出。");
                Shutdown(0);
                return;
            }

            // 用完全限定名声明局部变量：Application 自带同名属性 MainWindow，
            // 虽然 C# 的「Color Color」规则能区分类型与成员，但显式写清楚更省心
            WingetManagerWpf.Views.MainWindow shell;
            try
            {
                shell = new WingetManagerWpf.Views.MainWindow(cfg, _log);
            }
            catch (Exception ex)
            {
                // 视图层构造失败（XAML 资源缺失等）属于打包错误：
                // 记全量堆栈后退出，而不是留一个空转的黑窗口。
                _log.Error("主窗口创建失败，程序退出。", ex);
                Shutdown(1);
                return;
            }

            MainWindow = shell;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            shell.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_instance != null)
            {
                try
                {
                    _instance.Dispose();
                }
                catch (Exception)
                {
                    // 互斥量/管道释放失败不影响退出码
                }

                _instance = null;
            }

            if (_log != null)
            {
                _log.Info("Winget 软件管家退出（exit=" + e.ApplicationExitCode + "）");
                _log.Dispose();
            }

            base.OnExit(e);
        }

        /// <summary>
        /// 抢占单实例。返回 false 表示「已有一个活着的实例并且已被叫醒」。
        /// <para>
        /// 管道没响应时**故意继续启动第二个窗口**：宁可短暂出现两个窗口，
        /// 也好过用户点了图标却什么都没发生（旧版本、管道名冲突、首个实例还在
        /// 初始化都可能让 SHOW 送不进去）。
        /// </para>
        /// </summary>
        private bool AcquireSingleInstance()
        {
            _instance = new SingleInstance();
            if (_instance.IsFirstInstance)
            {
                _instance.Activated += OnActivatedBySecondInstance;
                _instance.StartServer();
                return true;
            }

            bool sent = SingleInstance.TrySendShow(SingleInstance.DefaultPipeName, 1500);
            if (sent)
            {
                return false;
            }

            _log.Warn("未能唤起运行中的实例（管道无响应），继续启动新窗口。");
            try
            {
                _instance.Dispose();
            }
            catch (Exception)
            {
                // 忽略
            }

            _instance = null;
            return true;
        }

        /// <summary>
        /// 第二实例发来 SHOW：必须在 UI 线程上还原/激活窗口。
        /// 事件本身在管道线程触发，直接操作 Window 会抛 InvalidOperationException。
        /// </summary>
        private void OnActivatedBySecondInstance()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(BringMainWindowToFront));
        }

        private void BringMainWindowToFront()
        {
            Window window = MainWindow;
            if (window == null)
            {
                return;
            }

            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();

            // 从后台进程抢前台时 Activate() 常被 Windows 拦下（前台锁），
            // 借用一次 Topmost 切换是代价最小的绕过办法：窗口只在 1ms 内置顶。
            bool topmost = window.Topmost;
            if (!topmost)
            {
                window.Topmost = true;
                window.Topmost = false;
            }
        }

        /// <summary>
        /// 三个兜底入口全部只写日志、不弹框：兜底本身再抛异常会造成二次崩溃，
        /// 用户看到的是「闪退且没有任何线索」，比不弹框糟糕得多。
        /// </summary>
        private void HookGlobalExceptionHandlers()
        {
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnAppDomainUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        }

        private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (_log != null)
            {
                _log.Error("UI 线程未捕获异常", e.Exception);
            }

            // 标记为已处理，避免 WPF 直接终止进程（用户还能点「重新扫描」自救）
            e.Handled = true;
        }

        private void OnAppDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            if (_log == null)
            {
                return;
            }

            var ex = e.ExceptionObject as Exception;
            if (ex != null)
            {
                _log.Error("非 UI 线程未捕获异常（进程即将结束=" + e.IsTerminating + "）", ex);
            }
            else
            {
                _log.Error("未捕获的非异常错误对象："
                           + (e.ExceptionObject == null ? "null" : e.ExceptionObject.GetType().FullName));
            }
        }

        private void OnUnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            if (_log != null)
            {
                _log.Error("任务未观察异常（已吞掉，避免 ProcessExit 崩溃）", e.Exception);
            }

            e.SetObserved();
        }
    }
}
