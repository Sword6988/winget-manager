using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WingetManager;

/// <summary>主窗口（T01 最小形态：仅验证 WinUI 3 + AOT 能启动并渲染）。</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Winget 软件管家";
        TrySetMicaBackdrop();
    }

    /// <summary>Mica 背景（P1-6 深浅色跟随系统；不支持的系统静默跳过）。</summary>
    private void TrySetMicaBackdrop()
    {
        if (!MicaController.IsSupported())
        {
            return;
        }

        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.BaseAlt };
    }
}
