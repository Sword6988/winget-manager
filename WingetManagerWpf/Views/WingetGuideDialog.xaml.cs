using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WingetManagerWpf.Controls;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// 「未检测到 winget」引导框。三个出口：打开商店、复制下载地址、知道了。
    /// </summary>
    public partial class WingetGuideDialog : Window
    {
        public WingetGuideDialog(string detail)
        {
            InitializeComponent();

            Title = Strings.GuideWindowTitle;
            IntroText.Text = Strings.GuideText;
            DetailText.Text = Strings.GuideDetail(detail);

            StoreButton.Content = Strings.GuideOpenStore;
            CopyLinkButton.Content = Strings.GuideCopyLink;
            OkButton.Content = Strings.GuideOk;
            CopyHint.Text = Strings.AboutCopied;

            StoreButton.Click += OnOpenStore;
            CopyLinkButton.Click += OnCopyLink;
            OkButton.Click += delegate { Close(); };
            CloseButton.Click += delegate { Close(); };

            // 首帧就把高度量准（SizeToContent=Manual）：DetailText 里带着 winget 的报错摘要，
            // 长度不定，写死高度会裁字，靠 SizeToContent=Height 又会先闪一帧
            Loaded += delegate { DialogSizer.FitToContent(this, RootBorder); };
        }

        /// <summary>
        /// 弹出引导框。父窗口还没显示时退回屏幕居中，
        /// 否则 <c>CenterOwner</c> 会算出 (0,0) 这种诡异的左上角位置。
        /// </summary>
        public static void ShowGuide(Window owner, string detail)
        {
            var dialog = new WingetGuideDialog(detail);

            if (owner != null && owner.IsVisible)
            {
                dialog.Owner = owner;
                dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dialog.ShowDialog();
        }

        private void OnOpenStore(object sender, RoutedEventArgs e)
        {
            // ms-windows-store:// 是 URI 协议，必须交给 shell（UseShellExecute=true），
            // 直接 Process.Start(uri) 在 .NET Framework 下会报「找不到文件」
            try
            {
                var startInfo = new ProcessStartInfo();
                startInfo.FileName = Strings.GuideStoreUri;
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception)
            {
                // 应用商店被卸载/被策略禁用时会失败：退化成「复制下载地址」的引导
                CopyLink();
                ShowCopiedHint();
            }
        }

        private void OnCopyLink(object sender, RoutedEventArgs e)
        {
            CopyLink();
            ShowCopiedHint();
        }

        private static void CopyLink()
        {
            try
            {
                Clipboard.SetText(Strings.GuideDownloadUrl);
            }
            catch (Exception)
            {
                // 剪贴板被独占：不影响用户手动打开链接
            }
        }

        private void ShowCopiedHint()
        {
            CopyHint.Visibility = Visibility.Visible;
            CopyHint.ToolTip = Strings.GuideDownloadUrl;
        }

        private void OnDragArea(object sender, MouseButtonEventArgs e)
        {
            // 无边框窗口必须自己实现拖动；标题栏区域没有窗口按钮冲突，直接 DragMove
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
