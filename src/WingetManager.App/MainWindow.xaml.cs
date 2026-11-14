using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WingetManager.Infra;
using WingetManager.Persistence;
using WingetManager.ViewModels;

namespace WingetManager;

/// <summary>
/// 主窗口：只做「渲染 + 拿路径 + 弹框」，业务编排全在 <see cref="MainViewModel"/>。
///
/// AOT 相关：
/// <list type="bullet">
/// <item>列表不用 <c>CollectionViewSource</c> 分组（AOT 下组内条目进不了视图，
///       microsoft-ui-xaml issue #10881），改为拍平的条目集合 + 模板选择器；</item>
/// <item>文件选择器必须用 <c>WindowNative</c> + <c>InitializeWithWindow</c> 传 hwnd
///       （未打包应用没有隐式窗口上下文），这条是 CsWinRT 源生成链路，AOT 安全；</item>
/// <item><see cref="ContentDialog"/> 在未打包场景必须显式给 <c>XamlRoot</c>，否则直接抛。</item>
/// </list>
/// </summary>
public sealed partial class MainWindow : Window, IMainView
{
    private readonly SettingsStore _settings;

    /// <summary>创建主窗口并完成装配。</summary>
    public MainWindow()
    {
        InitializeComponent();

        Title = App.DisplayName;

        // 便携（绿色）模式必须零注册表残留：设置改存 data\settings.json，
        // 否则「删掉文件夹就干净」不成立。安装版仍走注册表，与 Python 版互读。
        _settings = SettingsStore.CreateDefault(App.Log);

        ViewModel = new MainViewModel(this, App.Log);

        // 模板选择器在代码里装配（模板在 RootGrid.Resources；WinUI 3 的 Window 没有
        // Resources，把它写进 <Window.Resources> 会让 XamlCompiler 静默崩溃退出码 1）
        PackageList.ItemTemplateSelector = new AppServices.ListEntryTemplateSelector
        {
            HeaderTemplate = (DataTemplate)RootGrid.Resources["GroupHeaderTemplate"],
            RowTemplate = (DataTemplate)RootGrid.Resources["PackageRowTemplate"],
        };

        RestoreGroupSelection();
        ApplyWindowBounds();
        TrySetWindowIcon();
        TrySetMicaBackdrop();

        RootGrid.Loaded += OnRootLoaded;
        Closed += OnWindowClosed;
    }

    /// <summary>编排中枢（x:Bind 的唯一数据根）。</summary>
    public MainViewModel ViewModel { get; }

    // ───────────────────────────── 窗口图标（标题栏 / 任务栏 / Alt-Tab） ─────────────────────────────

    /// <summary>
    /// 设置窗口图标（标题栏、任务栏、Alt-Tab 三处共用）。
    /// 未打包应用不会从 MSIX 清单拿到图标，必须用 <see cref="AppWindow"/>（Microsoft.UI.Windowing）
    /// 的 SetIcon 按文件路径加载；ico 已配置随输出复制，故取 exe 同目录下的 Assets\app.ico。
    /// 加载失败不影响启动，只记日志兜底（此时标题栏为默认图标）。
    /// </summary>
    private void TrySetWindowIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
            if (!File.Exists(iconPath))
            {
                App.Log.Warn("窗口图标缺失，跳过设置：{0}", iconPath);
                return;
            }

            AppWindow.SetIcon(iconPath);
        }
        catch (Exception ex)
        {
            App.Log.Warn("设置窗口图标失败：{0}", ex.Message);
        }
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

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        RootGrid.Loaded -= OnRootLoaded;

        // 首屏自动扫描（约 2 秒）
        await ViewModel.InitialScanAsync();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        SaveWindowBounds();

        // 取消扫描与后台核查；缓存是逐条原子写的，未完成的那一条要么旧要么新，不会写坏
        ViewModel.Dispose();
    }

    // ───────────────────────────── 窗口尺寸 / 位置（P0-18、P1-3） ─────────────────────────────

    private void ApplyWindowBounds()
    {
        var bounds = _settings.GetWindowBounds();
        var width = Math.Clamp(bounds.Width, 720, 6000);
        var height = Math.Clamp(bounds.Height, 520, 4000);

        var x = bounds.X;
        var y = bounds.Y;
        if (IsOffScreen(x, y, width, height))
        {
            // 记录值来自已断开或缩小的显示器（拔掉副屏、改了分辨率、投影模式切换），
            // 回到默认位置，别把窗口开到看不见的地方（审计 F3：此前只防 x/y < -10000
            // 这一种极端情况，拔掉右侧副屏这种常见场景防不住）。
            x = WindowBounds.Default.X;
            y = WindowBounds.Default.Y;
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));

        if (bounds.IsMaximized && AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.Maximize();
        }
    }

    /// <summary>
    /// 窗口矩形与最近显示器的可见区域<b>完全没有交集</b>时视为离屏（审计 F3）。
    /// 探测失败保守返回 false（保留用户记录的位置），绝不让恢复尺寸这件事崩掉启动。
    /// </summary>
    private bool IsOffScreen(int x, int y, int width, int height)
    {
        if (x < -10000 || y < -10000)
        {
            // 记录值明显来自已断开的显示器，回到默认位置，别把窗口开到看不到的地方
            return true;
        }

        try
        {
            var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
            return x >= work.X + work.Width
                   || y >= work.Y + work.Height
                   || x + width <= work.X
                   || y + height <= work.Y;
        }
        catch (Exception ex)
        {
            App.Log.Warn("检测窗口是否离屏失败，按在屏处理：{0}", ex.Message);
            return false;
        }
    }

    private void SaveWindowBounds()
    {
        try
        {
            var stored = _settings.GetWindowBounds();
            var presenter = AppWindow.Presenter as OverlappedPresenter;
            if (presenter?.State == OverlappedPresenterState.Maximized)
            {
                // 最大化时不要把「最大化尺寸」当成下次的还原尺寸存下去
                _settings.SetWindowBounds(stored with { IsMaximized = true });
                return;
            }

            _settings.SetWindowBounds(new WindowBounds(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                AppWindow.Size.Height,
                false));
        }
        catch (Exception ex)
        {
            App.Log.Warn("保存窗口尺寸失败：{0}", ex.Message);
        }
    }

    // ───────────────────────────── 分组胶囊（P0-9） ─────────────────────────────

    private void RestoreGroupSelection()
    {
        var target = ViewModel.ActiveGroup;
        foreach (var item in GroupSelector.Items)
        {
            if (item is SelectorBarItem candidate && string.Equals(candidate.Text, target, StringComparison.Ordinal))
            {
                GroupSelector.SelectedItem = candidate;
                return;
            }
        }

        if (GroupSelector.Items.Count > 0)
        {
            GroupSelector.SelectedItem = GroupSelector.Items[0];
        }
    }

    private void OnGroupSelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        var text = sender.SelectedItem?.Text;
        if (!string.IsNullOrEmpty(text))
        {
            ViewModel.ActiveGroup = text;
        }
    }

    // ───────────────────────────── 快捷键（P1-4） ─────────────────────────────

    private void OnFocusSearchAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    private void OnSelectAllAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // 搜索框里 Ctrl+A 应该是「选中输入的文字」，不能抢
        if (IsTextInputFocused())
        {
            return;
        }

        args.Handled = true;
        ViewModel.SelectAllCommand.Execute(null);
    }

    private void OnExportAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (IsTextInputFocused() || !ViewModel.CanExport)
        {
            return;
        }

        args.Handled = true;
        ViewModel.ExportCommand.Execute(null);
    }

    private void OnRescanAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ViewModel.RescanOrCancelCommand.Execute(null);
    }

    private void OnEscapeAccelerator(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (ViewModel.IsScanning)
        {
            args.Handled = true;
            ViewModel.CancelScan();
            return;
        }

        if (!string.IsNullOrEmpty(ViewModel.SearchText))
        {
            args.Handled = true;
            ViewModel.SearchText = string.Empty;
        }
    }

    private bool IsTextInputFocused()
    {
        return FocusManager.GetFocusedElement(RootGrid.XamlRoot) is TextBox;
    }

    // ───────────────────────────── IMainView：导出取路径 + 弹框 ─────────────────────────────

    /// <inheritdoc />
    public async Task<string?> PickExportPathAsync(string suggestedPath)
    {
        var suggestedName = Path.GetFileNameWithoutExtension(suggestedPath);
        if (string.IsNullOrWhiteSpace(suggestedName))
        {
            suggestedName = "apps";
        }

        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = suggestedName,
                DefaultFileExtension = ".json",
            };
            picker.FileTypeChoices.Add("winget 导入清单", new List<string> { ".json" });

            // 未打包应用必须显式把窗口句柄交给 picker，否则 PickSaveFileAsync 直接抛
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSaveFileAsync();
            return file?.Path;
        }
        catch (Exception ex)
        {
            App.Log.Error("打开「另存为」失败：{0}", ex);

            // 系统策略禁用文件选择器时不让用户卡死：确认后用默认路径直接导出
            var confirmed = await ConfirmAsync(
                "无法打开文件选择器",
                "系统没有允许打开「另存为」窗口。是否直接导出到默认位置？\n\n" + suggestedPath,
                "导出到默认位置",
                "取消").ConfigureAwait(true);

            return confirmed ? suggestedPath : null;
        }
    }

    /// <inheritdoc />
    public Task ShowMessageAsync(string title, string message)
    {
        return ShowDialogAsync(new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close,
        });
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(string title, string message, string primaryButtonText, string closeButtonText)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = primaryButtonText,
            CloseButtonText = closeButtonText,
            DefaultButton = ContentDialogButton.Primary,
        };

        return await ShowDialogAsync(dialog).ConfigureAwait(true) == ContentDialogResult.Primary;
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        try
        {
            // 未打包 WinUI 3 必须显式给 XamlRoot，否则 ShowAsync 抛「This element is not associated with a XamlRoot」。
            // 赋值也放进 try（审计 F2）：窗口正在关闭时读 XamlRoot 可能为 null/抛异常，
            // 不能让弹框失败反过来炸掉调用方（导出流程的收尾提示就在这条链上）。
            dialog.XamlRoot = RootGrid.XamlRoot;
            return await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            App.Log.Error("弹窗失败：{0}", ex);
            return ContentDialogResult.None;
        }
    }
}
