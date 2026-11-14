using System;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using WingetManager.AppServices;
using WingetManager.Infra;

namespace WingetManager;

/// <summary>
/// 应用入口：注册代码页编码提供程序（GBK 依赖）、初始化日志、装配主窗口。
/// </summary>
public partial class App : Application
{
    /// <summary>应用显示名（窗口标题 / 关于对话框单一来源）。</summary>
    public const string DisplayName = "Winget 软件管家";

    /// <summary>
    /// 版本号（进日志 / 关于信息）。改版本时同步 <c>WingetManager.App.csproj</c> 的
    /// <c>&lt;Version&gt;</c>——那个写进 PE 的 FileVersion，资源管理器里看到的是它。
    /// </summary>
    public const string Version = "3.0.1";

    private Window? _window;

    /// <summary>进程内唯一主窗口（供 FileSavePicker 的 hwnd 初始化使用，T06 用）。</summary>
    public static Window? MainWindowInstance { get; private set; }

    /// <summary>全局唯一的日志服务实例（等价 Python 版 logging_setup）。</summary>
    public static LogService Log { get; private set; } = null!;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // .NET 默认不含 cp936/gbk 编码表：必须先注册，之后 Encoding.GetEncoding(936) 才可用。
        // AOT 兼容（PRD Q4 已裁定）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Log = new LogService();
        LogService.SetShared(Log);
        Log.Info("启动 {0} {1} · .NET {2}", DisplayName, Version, Environment.Version);

        // 全局异常兜底（审计 F1）：任何漏网的 UI 异常与后台任务异常都必须留下日志——
        // 「静默死亡且没有日志」比崩溃本身更难排查。各业务链路（扫描/导出/弹窗/日志写入）
        // 内部都已有各自的 try/catch，这里只接真正漏网的部分。
        UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // 后台 Task（扫描/富化）回到 UI 线程的唯一入口，必须在建窗口前绑定
        UiDispatch.Initialize(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
    }

    /// <summary>
    /// UI 线程未处理异常（审计 F1）：记日志后标记已处理让应用继续跑。
    /// 事件里的异常多来自单个事件处理器，整体退出反而放大故障面；
    /// 应用各链路都有局部兜底，状态损坏面有限。
    /// </summary>
    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log?.Error("UI 线程未处理异常：{0}", e.Exception);
        e.Handled = true;
    }

    /// <summary>
    /// 未观察的后台任务异常（审计 F1）：在 finalizer 线程触发，记日志并标记已观察。
    /// 例如 EnrichmentWorker/AsyncRelayCommand 之外被遗忘的 Task 抛出的异常。
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log?.Error("未观察的后台任务异常：{0}", e.Exception);
        e.SetObserved();
    }
}
