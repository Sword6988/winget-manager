using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WingetManagerWpf.Core;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 一行已安装软件。负责三件纯展示的事：勾选框状态、标签、搜索命中高亮；
    /// 业务动作（勾选/复制）只抛事件，由 MainWindow 转给 ViewModel。
    /// </summary>
    public partial class PackageRowControl : UserControl
    {
        /// <summary>winget 是默认来源，行内不再逐行重复标注（95 行里九成都是它）。</summary>
        private const string DefaultSource = "winget";

        private string[] _keywords = new string[0];
        private bool _compact;

        /// <summary>本次焦点是否由鼠标点击带入（决定要不要画焦点环）。</summary>
        private bool _focusByMouse;

        public PackageRowControl()
        {
            InitializeComponent();

            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
            MouseLeftButtonUp += OnRowMouseLeftButtonUp;
            GotKeyboardFocus += OnGotKeyboardFocus;
            LostKeyboardFocus += OnLostKeyboardFocus;
            PreviewKeyDown += OnPreviewKeyDown;

            Check.CheckedChanged += OnCheckChanged;
        }

        /// <summary>用户点了行内复选框。</summary>
        public event EventHandler ToggleRequested;

        /// <summary>用户按 Enter（复制包 ID）。</summary>
        public event EventHandler CopyRequested;

        /// <summary>本行绑定的包。</summary>
        public Package Package { get; private set; }

        /// <summary>
        /// 绑定一个包。<paramref name="isChecked"/> 来自 ViewModel 的勾选表 —— 行控件自己
        /// **不**维护勾选状态，避免「界面勾着、内存里没勾」。
        /// </summary>
        public void Bind(Package package, bool isChecked)
        {
            Package = package;
            if (package == null)
            {
                NameText.Text = string.Empty;
                IdText.Text = string.Empty;
                VersionText.Text = string.Empty;
                TagPanel.Children.Clear();
                return;
            }

            bool reinstallable = package.Reinstallable;

            NameText.Text = string.IsNullOrEmpty(package.Name) ? package.PackageIdentifier : package.Name;
            IdText.Text = package.PackageIdentifier;

            // 960 窄窗下主名/包 ID 必然截断：截断而不给全称 = 信息丢失
            NameText.ToolTip = NameText.Text;
            IdText.ToolTip = package.PackageIdentifier;

            // 不可重装的行整体灰显：一眼能看出「这些只是给你核对，勾不了」
            NameText.Foreground = reinstallable
                ? ThemeLookup.GetBrush(this, "Brush.Text", Brushes.Black)
                : ThemeLookup.GetBrush(this, "Brush.NameOff", Brushes.Gray);
            IdText.Foreground = reinstallable
                ? ThemeLookup.GetBrush(this, "Brush.Muted2", Brushes.Gray)
                : ThemeLookup.GetBrush(this, "Brush.IdOff", Brushes.LightGray);

            // 不可勾选的行隐藏复选框但保留 31px 占位，名称列才不会错位
            Check.Visibility = reinstallable ? Visibility.Visible : Visibility.Collapsed;
            Check.IsHitTestVisible = reinstallable;
            Cursor = reinstallable ? Cursors.Hand : Cursors.Arrow;

            if (reinstallable)
            {
                ApplyCheckedState(isChecked);
            }
            else
            {
                // 行控件可能被复用（上一个包是勾选态）：换绑成不可重装包时必须清掉选中底色
                HoverBd.Background = Brushes.Transparent;
                SelBar.Visibility = Visibility.Collapsed;
            }

            VersionText.Text = package.Version ?? string.Empty;
            VersionText.ToolTip = string.IsNullOrEmpty(package.AvailableVersion)
                ? null
                : "可用版本：" + package.AvailableVersion;

            BuildTags(package);
            SetHighlight(_keywords);
            ApplyCompact();
        }

        /// <summary>同步勾选态（ViewModel 是唯一真相，改完要回写到这里）。</summary>
        public void ApplyCheckedState(bool isChecked)
        {
            if (Check.IsChecked != isChecked)
            {
                // 直接改 DP 会触发 CheckedChanged → 又回到 ToggleRequested，
                // 这里先摘掉订阅再改，避免「同步状态」被误当成「用户点击」
                Check.CheckedChanged -= OnCheckChanged;
                Check.IsChecked = isChecked;
                Check.CheckedChanged += OnCheckChanged;
            }

            // v2：勾选不只反映在复选框上，整行铺一层品牌浅蓝，
            // 批量操作后扫一眼列表就知道哪些会被导出
            ApplyRestingBackground();
        }

        /// <summary>静止态行背景：已勾选 → RowSelected，否则透明（hover 临时覆盖）。</summary>
        private void ApplyRestingBackground()
        {
            bool on = Package != null && Package.Reinstallable && Check.IsChecked == true;

            // 勾选态三重信号：底色 + 左竖条 + 复选填充（主名保持正文色，不再染蓝）
            SelBar.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

            if (!on)
            {
                HoverBd.Background = Brushes.Transparent;
                return;
            }

            HoverBd.Background = ThemeLookup.GetBrush(this, "Brush.RowSelected", Brushes.White);
        }

        /// <summary>设置搜索高亮的检索词（空数组 = 取消高亮）。</summary>
        public void SetHighlight(string[] keywords)
        {
            _keywords = keywords ?? new string[0];

            if (Package == null)
            {
                return;
            }

            string name = string.IsNullOrEmpty(Package.Name) ? Package.PackageIdentifier : Package.Name;
            FillHighlightedText(NameText, name, _keywords, NameText.Foreground);
            FillHighlightedText(IdText, Package.PackageIdentifier, _keywords, IdText.Foreground);
        }

        /// <summary>窄窗模式：隐藏标签列与版本列（不重建行，只改可见性）。</summary>
        public void SetCompact(bool compact)
        {
            _compact = compact;
            ApplyCompact();
        }

        private void ApplyCompact()
        {
            Visibility visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
            TagPanel.Visibility = visibility;
            VersionText.Visibility = visibility;
        }

        /// <summary>
        /// 拼行内标签。v3.4 起只保留「非默认来源」一种（见方法内的说明），
        /// 所以这里不再需要「是不是运行库 / 能不能重装」这两个入参。
        /// </summary>
        private void BuildTags(Package package)
        {
            TagPanel.Children.Clear();

            // v3.4：行内只留「非默认来源」这一个标签。
            // 原来每行固定挂两个 pill（来源 winget + 类型 运行库），95 行里九成相同，
            // 而「软件 / 运行库 / winget 不支持」本来就由分组头写着，等于同一句话说了两遍。
            // 现在只在来源确实不同（msstore 等）时才提示，列表安静下来，扫读更快。
            if (!string.IsNullOrEmpty(package.Source) &&
                !string.Equals(package.Source, DefaultSource, StringComparison.OrdinalIgnoreCase))
            {
                AddTag(package.Source,
                       ThemeLookup.GetBrush(this, "Brush.TagSrcFg", Brushes.Purple),
                       ThemeLookup.GetBrush(this, "Brush.TagSrcBg", Brushes.Lavender),
                       ThemeLookup.GetBrush(this, "Brush.TagSrcLine", Brushes.Lavender));
            }
        }

        private void AddTag(string text, Brush foreground, Brush background, Brush border)
        {
            var label = new TextBlock();
            label.Text = text;
            label.FontFamily = ThemeLookup.GetFontFamily(this, "FontFamily.Ui", new FontFamily("Microsoft YaHei UI"));
            label.FontSize = ThemeLookup.GetDouble(this, "FontSize.Tag", 10.7);
            label.Foreground = foreground;

            var chip = new Border();
            chip.CornerRadius = ThemeLookup.GetCornerRadius(this, "Corner.Tag", new CornerRadius(6));
            chip.Background = background;
            chip.BorderBrush = border;
            chip.BorderThickness = new Thickness(1);
            chip.Padding = new Thickness(7, 2, 7, 2);
            chip.Margin = new Thickness(0, 0, 6, 0);
            chip.VerticalAlignment = VerticalAlignment.Center;
            chip.Child = label;

            TagPanel.Children.Add(chip);
        }

        /// <summary>
        /// 把命中片段做成 <see cref="Run"/>（黄底深棕字 + SemiBold），其余部分保持原色。
        /// 用 Inlines 而不是给整块文字换色：搜 "chrome" 时只该亮那 6 个字母。
        /// </summary>
        private void FillHighlightedText(TextBlock target, string text, string[] keywords, Brush normalBrush)
        {
            string content = text ?? string.Empty;
            target.Inlines.Clear();

            if (content.Length == 0)
            {
                return;
            }

            if (keywords == null || keywords.Length == 0)
            {
                var plain = new Run(content);
                plain.Foreground = normalBrush;
                target.Inlines.Add(plain);
                return;
            }

            Brush highlightBg = ThemeLookup.GetBrush(this, "Brush.HighlightBg", Brushes.Khaki);
            Brush highlightFg = ThemeLookup.GetBrush(this, "Brush.HighlightFg", Brushes.SaddleBrown);

            int index = 0;
            var buffer = new System.Text.StringBuilder();
            while (index < content.Length)
            {
                int matchedLength = 0;
                for (int i = 0; i < keywords.Length; i++)
                {
                    string keyword = keywords[i];
                    if (string.IsNullOrEmpty(keyword) || keyword.Length <= matchedLength)
                    {
                        continue;
                    }

                    if (index + keyword.Length > content.Length)
                    {
                        continue;
                    }

                    if (string.Compare(content, index, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    {
                        matchedLength = keyword.Length;
                    }
                }

                if (matchedLength > 0)
                {
                    FlushPlain(target, buffer, normalBrush);

                    var hit = new Run(content.Substring(index, matchedLength));
                    hit.Background = highlightBg;
                    hit.Foreground = highlightFg;
                    hit.FontWeight = FontWeights.SemiBold;
                    target.Inlines.Add(hit);

                    index += matchedLength;
                }
                else
                {
                    buffer.Append(content[index]);
                    index++;
                }
            }

            FlushPlain(target, buffer, normalBrush);
        }

        private static void FlushPlain(TextBlock target, System.Text.StringBuilder buffer, Brush normalBrush)
        {
            if (buffer.Length == 0)
            {
                return;
            }

            var run = new Run(buffer.ToString());
            run.Foreground = normalBrush;
            target.Inlines.Add(run);
            buffer.Length = 0;
        }

        private void OnMouseEnter(object sender, MouseEventArgs e)
        {
            if (Package != null && !Package.Reinstallable)
            {
                return;
            }

            HoverBd.Background = ThemeLookup.GetBrush(this, "Brush.RowHover", Brushes.WhiteSmoke);
        }

        private void OnMouseLeave(object sender, MouseEventArgs e)
        {
            // 不能直接清成透明：如果这行是被勾选的，要回到 RowSelected 静止态
            ApplyRestingBackground();
        }

        /// <summary>
        /// 点击行内非复选框区域 = 切换勾选。
        /// 方法名不能叫 OnMouseLeftButtonUp：那会与 UIElement 的保护虚方法同名同签名（CS0114/CS0123）。
        /// </summary>
        private void OnRowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            // 点在复选框上时 CheckBoxEx 已把事件标记为 Handled，不会走到这里，
            // 所以此处只处理「点行内其它位置 = 勾选」这一种语义
            if (Package == null || !Package.Reinstallable)
            {
                return;
            }

            // 先把「这次焦点来自鼠标」的标记挂上，Focus() 内部会同步触发 GotKeyboardFocus，
            // 触发完再清掉，避免后续键盘移入被误判
            _focusByMouse = true;
            Focus();
            _focusByMouse = false;
            RaiseToggle();
        }

        private void OnCheckChanged(object sender, EventArgs e)
        {
            // 复选框自己翻转了 DP，这里只把「用户意图」抛给外层；
            // 外层会调 ViewModel.ToggleRow 再回写 ApplyCheckedState，两边终态一致
            RaiseToggle();
        }

        private void RaiseToggle()
        {
            EventHandler handler = ToggleRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // 鼠标点行时下方 OnRowMouseLeftButtonUp 会主动 Focus()，若这里无条件显示焦点环，
            // 每点一行都会多出一个蓝框，看起来像「选中了两次」。
            // 用调用方设置的标志位判断，比看 InputManager.MostRecentInputDevice 更确定
            // （后者在 UIA / 程序化 Focus 的时序里并不可靠）。
            FocusBd.Visibility = _focusByMouse ? Visibility.Collapsed : Visibility.Visible;
            _focusByMouse = false;
        }

        private void OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            FocusBd.Visibility = Visibility.Collapsed;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                if (Package != null && Package.Reinstallable)
                {
                    RaiseToggle();
                }

                e.Handled = true;
                return;
            }

            if (e.Key == Key.Enter)
            {
                EventHandler handler = CopyRequested;
                if (handler != null)
                {
                    handler(this, EventArgs.Empty);
                }

                e.Handled = true;
            }
        }
    }
}
