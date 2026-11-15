using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using WingetManagerWpf.Controls;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// 「关于」窗口。除了版本信息，重点是「复制诊断信息」——
    /// 用户报问题时能一键贴出版本、路径和最近日志，比来回追问省几轮。
    /// </summary>
    public partial class AboutDialog : Window
    {
        private readonly string _diagnostic;

        public AboutDialog(string exportPath, string selectionPath, string logPath, string diagnostic)
        {
            InitializeComponent();

            Title = Strings.AboutWindowTitle;
            VersionText.Text = Strings.AboutVersionLine();

            // 便携模式要把「数据跟着 exe 走」讲清楚：用户可以整个文件夹拷走，
            // 也可以直接把 exe 删掉完成卸载（不留任何用户目录残留）。
            IntroText.Text = AppPaths.IsPortable
                ? Strings.AboutIntro + "\n" + Strings.AboutPortableHint
                : Strings.AboutIntro;

            ExportPathText.Text = Display(exportPath);
            SelectionPathText.Text = DisplayDataFile(selectionPath);
            LogPathText.Text = DisplayDataFile(logPath);

            // 相对路径是给人看的，完整路径放进 ToolTip 备查
            SelectionPathText.ToolTip = selectionPath;
            LogPathText.ToolTip = logPath;

            CopyDiagButton.Content = Strings.AboutCopyDiag;
            OpenDataDirButton.Content = Strings.AboutOpenDataDir;
            OkButton.Content = Strings.AboutClose;
            CopyHint.Text = Strings.AboutCopied;

            _diagnostic = diagnostic ?? string.Empty;

            CopyDiagButton.Click += OnCopyDiagnostic;
            OpenDataDirButton.Click += OnOpenDataDir;
            OkButton.Click += delegate { Close(); };
            CloseButton.Click += delegate { Close(); };

            // 首帧就把高度量准（SizeToContent=Manual），避免无边框透明窗先闪一帧错误高度
            Loaded += delegate { DialogSizer.FitToContent(this, RootBorder); };
        }

        public static void ShowAbout(Window owner, string exportPath, string selectionPath, string logPath,
                                     string diagnostic)
        {
            var dialog = new AboutDialog(exportPath, selectionPath, logPath, diagnostic);
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

        private static string Display(string path)
        {
            return string.IsNullOrEmpty(path) ? Strings.AboutLogNotCreated : path;
        }

        /// <summary>
        /// 便携模式下把数据文件压成「Data\…」相对形式。
        /// 绝对路径前缀太长（D:\Program Files\...\dist\Data\），末尾的 TextTrimming
        /// 会把文件名本身截掉，用户反而看不出是哪个文件；「打开数据目录」按钮
        /// 能直接跳到该目录，所以相对路径不损失任何可操作性。
        /// </summary>
        private static string DisplayDataFile(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return Strings.AboutLogNotCreated;
            }

            if (AppPaths.IsPortable)
            {
                string root = AppPaths.DataDir.TrimEnd(System.IO.Path.DirectorySeparatorChar);
                if (path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return "Data" + path.Substring(root.Length);
                }
            }

            return path;
        }

        private void OnCopyDiagnostic(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_diagnostic);
                CopyHint.Visibility = Visibility.Visible;
            }
            catch (Exception)
            {
                // 剪贴板被别的进程占用：不弹框，用户再点一次通常就好了
            }
        }

        private void OnOpenDataDir(object sender, RoutedEventArgs e)
        {
            try
            {
                AppPaths.EnsureDataDir();

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                // 目录可能还不存在（从未导出过）：explorer 会自己打开最近的可访问上级
                startInfo.Arguments = "\"" + AppPaths.DataDir + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception)
            {
                // 打不开资源管理器不该让「关于」框崩掉
            }
        }

        private void OnDragArea(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }
    }
}
