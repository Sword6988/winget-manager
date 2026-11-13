using System;
using System.Text;
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

    /// <summary>版本号（与 exe 属性保持一致，P2-3）。</summary>
    public const string Version = "3.0.0";

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

        // 后台 Task（扫描/富化）回到 UI 线程的唯一入口，必须在建窗口前绑定
        UiDispatch.Initialize(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());

        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
    }
}
