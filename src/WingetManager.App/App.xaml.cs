using System.Text;
using Microsoft.UI.Xaml;

namespace WingetManager;

/// <summary>
/// 应用入口。T01 只做三件事：注册代码页编码提供程序（GBK 依赖）、
/// 建立主窗口、激活窗口。日志/服务装配在后续任务接入。
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>进程内唯一主窗口（供 FileSavePicker 的 hwnd 初始化使用，T06 用）。</summary>
    public static Window? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // .NET 默认不含 cp936/gbk 编码表：必须先注册，之后 Encoding.GetEncoding(936) 才可用。
        // AOT 兼容（PRD Q4 已裁定）。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        _window = new MainWindow();
        MainWindowInstance = _window;
        _window.Activate();
    }
}
