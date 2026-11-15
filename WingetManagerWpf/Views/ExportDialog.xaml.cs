using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WingetManagerWpf.Controls;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;
using WingetManagerWpf.Services;

namespace WingetManagerWpf.Views
{
    /// <summary>
    /// 导出对话框。表单页负责「写哪儿」，成功页负责「写了什么、之后怎么用」。
    /// <para>
    /// 职责边界：本类只做 UI 与落盘的**编排**——三产物的字节内容全部来自
    /// <see cref="ExportProfiles"/>（编码规则都在那边，别在这里二次处理）。
    /// 导出成功后把 <c>settings.ExportPath</c> 写回并落盘，下次打开默认用它。
    /// </para>
    /// <para>
    /// 模态窗口 + <see cref="Window.ShowDialog"/>：导出过程中主窗口必须不可交互，
    /// 否则用户能在写盘途中再点一次「导出配置」，两条链同时改同一个 bat。
    /// </para>
    /// </summary>
    public partial class ExportDialog : Window
    {
        /// <summary>文件清单行字号（8.5pt * 96 / 72）。</summary>
        private const double FileRowFontSize = 11.3;

        /// <summary>文件清单里「清单文件 / 恢复脚本 / 待装页面」列的固定宽。</summary>
        private const double FileKindColumnWidth = 66;

        /// <summary>
        /// 文件名里额外要拦的字符。取 <see cref="Path.GetInvalidFileNameChars"/> 的并集是保守做法：
        /// 不同 Windows 版本返回的集合不完全一致（例如 ':' 在部分 API 上不返回），
        /// 而 winget 清单是要拷到别的机器上用的，宁严不宽。
        /// </summary>
        private static readonly char[] StrictInvalidNameChars =
            new char[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|' };

        private readonly IList<Package> _selected;
        private readonly IList<Package> _allPackages;
        private readonly string _wingetVersion;
        private readonly AppLogger _log;
        private readonly AppSettings _settings;
        private readonly SettingsStore _settingsStore;

        /// <summary>勾选项里来自 msstore 的包数（hint 与成功页提示都要用）。</summary>
        private readonly int _msStoreCount;

        private string _destPath = string.Empty;
        private bool _exporting;

        /// <summary>
        /// 构造导出对话框。
        /// </summary>
        /// <param name="selected">已勾选的可重装包（写进清单的那批）。</param>
        /// <param name="allPackages">全部包（生成「无法自动重装清单」用）。</param>
        /// <param name="wingetVersion">当前 winget 版本号，写进清单的 WinGetVersion 字段。</param>
        /// <param name="initialPath">初始导出路径（通常是「上次导出路径」推导出来的）。</param>
        /// <param name="log">全局日志（界面与文件同源）。</param>
        /// <param name="settings">设置对象，导出成功后回写 ExportPath。</param>
        /// <param name="settingsStore">设置的持久化入口，负责把 ExportPath 落盘。</param>
        public ExportDialog(IList<Package> selected, IList<Package> allPackages, string wingetVersion,
                            string initialPath, AppLogger log, AppSettings settings, SettingsStore settingsStore)
        {
            InitializeComponent();

            _selected = selected ?? new List<Package>();
            _allPackages = allPackages ?? new List<Package>();
            _wingetVersion = string.IsNullOrEmpty(wingetVersion) ? "1.10.0" : wingetVersion;
            _log = log;
            _settings = settings;
            _settingsStore = settingsStore;
            _msStoreCount = ExportProfiles.SourceBreakdown(_selected).MsStore;

            Title = Strings.ExportTitle;
            TitleText.Text = Strings.ExportTitle;
            FormTitle.Text = Strings.ExportTitle;
            FormDesc.Text = Strings.ExportDescription(_selected.Count);

            ChangeButton.Content = Strings.ExportButtonChange;
            CancelButton.Content = Strings.ExportButtonCancel;
            PrimaryButton.Content = Strings.ExportButtonNow;
            CloseButton.Content = Strings.ExportButtonClose;
            OpenFolderLabel.Text = Strings.ExportOpenFolder;
            BatTipText.Text = Strings.ExportSuccessBatTip;
            ListTipText.Text = Strings.ExportSuccessListTip;

            // 「导出后打开文件夹」默认勾选：用户导出完最想干的事就是去看看文件在不在
            OpenFolderCheck.IsChecked = true;

            ChangeButton.Click += OnChangeClick;
            CancelButton.Click += delegate { Close(); };
            TitleCloseButton.Click += delegate { Close(); };
            PrimaryButton.Click += OnPrimaryClick;
            CloseButton.Click += OnCloseClick;
            PreviewKeyDown += OnPreviewKeyDown;

            // 首帧量准高度（SizeToContent=Manual）：无边框透明窗用 SizeToContent=Height
            // 会先按 XAML 初值显示一帧、再跳到内容高度
            Loaded += delegate { DialogSizer.FitToContent(this, RootBorder); };

            string start = initialPath;
            if (string.IsNullOrEmpty(start))
            {
                start = AppPaths.DefaultExportPath(_settings != null ? _settings.ExportPath : string.Empty);
            }

            SetDestination(start);
        }

        /// <summary>导出结果；未导出成功时为 null。</summary>
        public ExportResult Result { get; private set; }

        // ══════════════════════════ 表单页 ══════════════════════════

        private void SetDestination(string path)
        {
            _destPath = path ?? string.Empty;
            PathBox.Text = _destPath;
            PathBox.ToolTip = _destPath;
            PathBox.CaretIndex = _destPath.Length;
            UpdateHint();
        }

        /// <summary>按「目标是否存在 / 文件名是否合法」刷新 hint 与主按钮文案。</summary>
        private void UpdateHint()
        {
            if (_exporting)
            {
                return;
            }

            if (HasInvalidFileName(_destPath))
            {
                HintText.Foreground = BrushOf("Brush.Err", Brushes.IndianRed);
                HintText.Text = Strings.ExportInvalidFileName;
                PrimaryButton.IsEnabled = false;
                PrimaryButton.Content = Strings.ExportButtonNow;
                return;
            }

            bool exists = false;
            try
            {
                exists = File.Exists(_destPath);
            }
            catch (Exception)
            {
                // 路径长到 API 都不接受时按「不存在」处理，写盘阶段会给出真正的错误
                exists = false;
            }

            var builder = new StringBuilder();
            builder.Append(exists ? Strings.ExportHintOverwrite : Strings.ExportHintNormal);
            if (_msStoreCount > 0)
            {
                builder.Append(Strings.ExportMsStoreSuffix(_msStoreCount));
            }

            builder.Append(Strings.ExportSameDirSuffix(StemOf(_destPath)));

            HintText.Text = builder.ToString();
            HintText.Foreground = exists
                ? BrushOf("Brush.Warn", Brushes.DarkGoldenrod)
                : BrushOf("Brush.Muted2", Brushes.Gray);
            PrimaryButton.Content = exists ? Strings.ExportButtonOverwrite : Strings.ExportButtonNow;
            PrimaryButton.IsEnabled = true;
        }

        private void OnChangeClick(object sender, RoutedEventArgs e)
        {
            var picker = new Microsoft.Win32.SaveFileDialog();
            picker.Title = Strings.ExportPickTitle;
            picker.Filter = Strings.ExportPickFilter + "|*.json";
            picker.DefaultExt = ".json";
            picker.AddExtension = true;

            // 覆盖确认由本对话框自己的 hint +「覆盖导出」按钮承担，系统再弹一次会重复
            picker.OverwritePrompt = false;

            try
            {
                string full = Path.GetFullPath(_destPath);
                picker.FileName = Path.GetFileName(full);
                string directory = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    picker.InitialDirectory = directory;
                }
            }
            catch (Exception)
            {
                // 上次的路径含非法字符或已不可解析：交给对话框用系统默认目录
            }

            if (picker.ShowDialog(this) != true)
            {
                return;
            }

            string chosen = picker.FileName;
            if (string.IsNullOrEmpty(chosen))
            {
                return;
            }

            // 用户可能手输成 apps.txt：强制回 .json，否则 winget import -i 会拒收
            if (!string.Equals(Path.GetExtension(chosen), ".json", StringComparison.OrdinalIgnoreCase))
            {
                chosen = Path.ChangeExtension(chosen, ".json");
            }

            SetDestination(chosen);
        }

        private void OnPrimaryClick(object sender, RoutedEventArgs e)
        {
            if (_exporting)
            {
                return;
            }

            if (HasInvalidFileName(_destPath))
            {
                UpdateHint();
                return;
            }

            BeginExport();
        }

        private void BeginExport()
        {
            string dest = _destPath;

            _exporting = true;
            PrimaryButton.IsEnabled = false;
            ChangeButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            HintText.Foreground = BrushOf("Brush.Muted2", Brushes.Gray);
            HintText.Text = Strings.ExportBusy;
            Mouse.OverrideCursor = Cursors.Wait;

            try
            {
                if (_log != null)
                {
                    _log.Info(Strings.LogExportStart(_selected.Count, dest));
                }

                ExportResult result = ExportProfiles.WriteAll(
                    dest, _selected, _allPackages, _wingetVersion, _log);

                Result = result;

                if (_settings != null)
                {
                    _settings.ExportPath = result.JsonPath;
                    if (_settingsStore != null)
                    {
                        _settingsStore.Save(_settings);
                    }
                }

                LogExportResult(result);
                ShowSuccess(result);
            }
            catch (AppException ex)
            {
                FailExport(ex.UserMessage, ex);
            }
            catch (Exception ex)
            {
                FailExport(ex.Message, ex);
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }

        /// <summary>导出成功的三段日志：标题行 → 逐文件路径（缩进两格）→ msstore 提示。</summary>
        private void LogExportResult(ExportResult result)
        {
            if (_log == null || result == null)
            {
                return;
            }

            _log.Success(Strings.ExportDoneTitle(result.PackageCount) + Strings.LogExportDoneSuffix);

            for (int i = 0; i < result.Files.Count; i++)
            {
                _log.Info("  " + result.Files[i]);
            }

            if (result.Breakdown != null && result.Breakdown.MsStore > 0)
            {
                // 界面上这行是「跳过」语义（紫色）。AppLogger 没有 skip 级别，
                // 用 Warn 写文件（面板同色）——比新增一个级别更省事，也更容易被用户注意到
                _log.Warn(Strings.LogExportMsStoreSkip(result.Breakdown.MsStore));
            }
        }

        private void FailExport(string userMessage, Exception ex)
        {
            _exporting = false;
            PrimaryButton.IsEnabled = true;
            ChangeButton.IsEnabled = true;
            CancelButton.IsEnabled = true;
            HintText.Foreground = BrushOf("Brush.Err", Brushes.IndianRed);
            HintText.Text = userMessage;

            bool exists = false;
            try
            {
                exists = File.Exists(_destPath);
            }
            catch (Exception)
            {
                exists = false;
            }

            PrimaryButton.Content = exists ? Strings.ExportButtonOverwrite : Strings.ExportButtonNow;

            if (_log != null)
            {
                string detail = ex != null ? ex.Message : (userMessage ?? string.Empty);
                _log.Error(Strings.LogExportFailedPrefix + detail, ex);
            }
        }

        // ══════════════════════════ 成功页 ══════════════════════════

        private void ShowSuccess(ExportResult result)
        {
            FormPage.Visibility = Visibility.Collapsed;
            SuccessPage.Visibility = Visibility.Visible;

            SuccessTitle.Text = Strings.ExportDoneTitle(result.PackageCount);
            BuildFileRows(result);

            bool hasBat = false;
            bool hasList = false;
            for (int i = 0; i < result.Files.Count; i++)
            {
                string extension = string.Empty;
                try
                {
                    extension = Path.GetExtension(result.Files[i] ?? string.Empty);
                }
                catch (ArgumentException)
                {
                    extension = string.Empty;
                }

                if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
                {
                    hasBat = true;
                }
                else if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase))
                {
                    hasList = true;
                }
            }

            // 没生成 bat（GBK 编码失败）或没有不可重装包时不摆对应提示，免得指路指空
            BatTipText.Visibility = hasBat ? Visibility.Visible : Visibility.Collapsed;
            ListTipText.Visibility = hasList ? Visibility.Visible : Visibility.Collapsed;

            // 成功页通常比表单页高（多出徽章 + 文件清单 + 双提示）：
            // 不重算高度会把底部的「关闭」按钮裁掉，用户就没法收尾了
            DialogSizer.FitToContent(this, RootBorder);
        }

        private void BuildFileRows(ExportResult result)
        {
            FileListHost.Children.Clear();
            if (result == null || result.Files == null)
            {
                return;
            }

            FontFamily mono = Res<FontFamily>("FontFamily.Mono", new FontFamily("Consolas"));
            Brush kindBrush = BrushOf("Brush.Muted2", Brushes.Gray);
            Brush nameBrush = BrushOf("Brush.Muted", Brushes.DimGray);

            for (int i = 0; i < result.Files.Count; i++)
            {
                string full = result.Files[i] ?? string.Empty;

                var row = new Grid();
                row.Margin = new Thickness(0, i == 0 ? 0 : 5, 0, 0);
                row.ToolTip = full;
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions.Add(new ColumnDefinition());
                row.ColumnDefinitions[0].Width = new GridLength(FileKindColumnWidth);
                row.ColumnDefinitions[1].Width = new GridLength(1, GridUnitType.Star);

                var kind = new TextBlock();
                kind.Text = KindOf(full);
                kind.FontFamily = mono;
                kind.FontSize = FileRowFontSize;
                kind.Foreground = kindBrush;
                Grid.SetColumn(kind, 0);

                var name = new TextBlock();
                name.Text = FileNameOf(full);
                name.FontFamily = mono;
                name.FontSize = FileRowFontSize;
                name.Foreground = nameBrush;
                name.TextTrimming = TextTrimming.CharacterEllipsis;
                Grid.SetColumn(name, 1);

                row.Children.Add(kind);
                row.Children.Add(name);
                FileListHost.Children.Add(row);
            }
        }

        private static string KindOf(string path)
        {
            string extension = ExtensionOf(path);
            if (string.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase))
            {
                return Strings.ExportKindScript;
            }

            if (string.Equals(extension, ".html", StringComparison.OrdinalIgnoreCase))
            {
                return Strings.ExportKindChecklist;
            }

            return Strings.ExportKindManifest;
        }

        private static string ExtensionOf(string path)
        {
            try
            {
                return Path.GetExtension(path ?? string.Empty) ?? string.Empty;
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
        }

        private static string FileNameOf(string path)
        {
            try
            {
                string name = Path.GetFileName(path ?? string.Empty);
                return string.IsNullOrEmpty(name) ? (path ?? string.Empty) : name;
            }
            catch (ArgumentException)
            {
                return path ?? string.Empty;
            }
        }

        private void OnToggleOpenFolder(object sender, MouseButtonEventArgs e)
        {
            OpenFolderCheck.IsChecked = !OpenFolderCheck.IsChecked;
            e.Handled = true;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            if (OpenFolderCheck.IsChecked)
            {
                OpenExportFolder();
            }

            CloseWithResult(true);
        }

        private void OpenExportFolder()
        {
            string path = Result != null ? Result.JsonPath : _destPath;
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(directory))
                {
                    return;
                }

                var startInfo = new ProcessStartInfo();
                startInfo.FileName = "explorer.exe";
                startInfo.Arguments = "\"" + directory + "\"";
                startInfo.UseShellExecute = true;
                Process.Start(startInfo);
            }
            catch (Exception)
            {
                // 打不开资源管理器不该让「导出成功」这个事实变成一次异常弹窗
            }
        }

        // ══════════════════════════ 窗口行为 ══════════════════════════

        private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经抬起（快速点击）时 DragMove 会抛，忽略即可
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape && !_exporting)
            {
                Close();
                e.Handled = true;
            }
        }

        private void CloseWithResult(bool result)
        {
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                // 非模态打开（例如将来做成非模态）时不能设 DialogResult，退化为关闭
                Close();
            }
        }

        // ══════════════════════════ 工具 ══════════════════════════

        /// <summary>
        /// 文件名是否含非法字符。只查**文件名**部分：路径里的 <c>\</c> 是分隔符，必须放行。
        /// </summary>
        private static bool HasInvalidFileName(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return true;
            }

            string name;
            try
            {
                name = Path.GetFileName(path);
            }
            catch (ArgumentException)
            {
                return true;
            }

            if (string.IsNullOrEmpty(name))
            {
                return true;
            }

            char[] platformInvalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < platformInvalid.Length; i++)
            {
                if (name.IndexOf(platformInvalid[i]) >= 0)
                {
                    return true;
                }
            }

            for (int i = 0; i < StrictInvalidNameChars.Length; i++)
            {
                if (name.IndexOf(StrictInvalidNameChars[i]) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>清单文件的 stem（不带扩展名）；推不出来时回落 apps。</summary>
        private static string StemOf(string path)
        {
            try
            {
                string stem = Path.GetFileNameWithoutExtension(path ?? string.Empty);
                return string.IsNullOrEmpty(stem) ? "apps" : stem;
            }
            catch (ArgumentException)
            {
                return "apps";
            }
        }

        private Brush BrushOf(string resourceKey, Brush fallback)
        {
            object value = TryFindResource(resourceKey);
            Brush brush = value as Brush;
            return brush != null ? brush : fallback;
        }

        private T Res<T>(string resourceKey, T fallback) where T : class
        {
            object value = TryFindResource(resourceKey);
            T typed = value as T;
            return typed != null ? typed : fallback;
        }
    }
}
