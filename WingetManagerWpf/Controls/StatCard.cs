using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 头部统计卡：104×70，白底圆角 12 + 1px 描边 + 顶部 3px 渐变条 + 大数字 + 说明文字。
    /// <para>
    /// 为什么自绘而不拼 4 个 Border/TextBlock：卡片要做的三件事（顶部渐变条被圆角裁剪、
    /// 数字用渐变画刷、hover 时描边换色）在 XAML 里都要靠嵌套 + Clip，节点数会翻三倍，
    /// 而且 4 张卡片要复制 4 遍。自绘后每张卡在 XAML 里就是一个标签。
    /// </para>
    /// </summary>
    public sealed class StatCard : FrameworkElement
    {
        public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(
            "Caption", typeof(string), typeof(StatCard),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            "Value", typeof(string), typeof(StatCard),
            new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty NumberBrushProperty = DependencyProperty.Register(
            "NumberBrush", typeof(Brush), typeof(StatCard),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
            "BarBrush", typeof(Brush), typeof(StatCard),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        private bool _hover;
        private readonly TranslateTransform _lift = new TranslateTransform(0, 0);

        public StatCard()
        {
            Width = Metrics.StatCardWidth;
            Height = Metrics.StatCardHeight;
            SnapsToDevicePixels = true;
            RenderTransform = _lift;
            Cursor = System.Windows.Input.Cursors.Arrow;

            MouseEnter += OnMouseEnter;
            MouseLeave += OnMouseLeave;
        }

        /// <summary>说明文字（如「可重装」）。</summary>
        public string Caption
        {
            get { return (string)GetValue(CaptionProperty); }
            set { SetValue(CaptionProperty, value); }
        }

        /// <summary>数字（字符串形式，ViewModel 已格式化好）。</summary>
        public string Value
        {
            get { return (string)GetValue(ValueProperty); }
            set { SetValue(ValueProperty, value); }
        }

        /// <summary>数字画刷；为 null 时用 <c>Brush.AccentGradient</c>。</summary>
        public Brush NumberBrush
        {
            get { return (Brush)GetValue(NumberBrushProperty); }
            set { SetValue(NumberBrushProperty, value); }
        }

        /// <summary>顶部渐变条画刷；为 null 时用 <c>Brush.AccentGradient</c>。</summary>
        public Brush BarBrush
        {
            get { return (Brush)GetValue(BarBrushProperty); }
            set { SetValue(BarBrushProperty, value); }
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                return;
            }

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            double radius = Metrics.CornerInner;
            var rect = new Rect(0, 0, width, height);

            Brush card = ThemeLookup.GetBrush(this, "Brush.Card", Brushes.White);
            Brush line = ThemeLookup.GetBrush(this, "Brush.Line", Brushes.LightGray);
            Brush lineHot = ThemeLookup.GetBrush(this, "Brush.AccentLine", Brushes.MediumPurple);
            Brush muted = ThemeLookup.GetBrush(this, "Brush.Muted", Brushes.Gray);
            Brush number = NumberBrush ?? ThemeLookup.GetBrush(this, "Brush.AccentGradient", Brushes.MediumPurple);
            Brush bar = BarBrush ?? ThemeLookup.GetBrush(this, "Brush.AccentGradient", Brushes.MediumPurple);

            var borderPen = new Pen(_hover ? lineHot : line, 1);
            drawingContext.DrawRoundedRectangle(card, borderPen, rect, radius, radius);

            // 顶部 3px 渐变条：先按卡片轮廓裁剪，再画整条矩形，
            // 这样左右上角自然跟着圆角走，不需要手算四段圆弧
            drawingContext.PushClip(new RectangleGeometry(rect, radius, radius));
            drawingContext.DrawRectangle(bar, null, new Rect(0, 0, width, 3));
            drawingContext.Pop();

            FontFamily family = ThemeLookup.GetFontFamily(
                this, "FontFamily.Ui", new FontFamily("Microsoft YaHei UI"));
            double numberSize = ThemeLookup.GetDouble(this, "FontSize.StatsNumber", 23);
            double captionSize = ThemeLookup.GetDouble(this, "FontSize.Small", 11.7);

            FormattedText numberText = Build(Value ?? "0", family, numberSize, FontWeights.Bold,
                                             number, width - 24, ThemeLookup.GetPixelsPerDip(this));
            FormattedText captionText = Build(Caption ?? string.Empty, family, captionSize, FontWeights.Normal,
                                              muted, width - 24, ThemeLookup.GetPixelsPerDip(this));

            // 数字与说明按「数字在下、说明紧跟」排；卡片只有 70 高，
            // 把光标位从数字基线开始累加最省心（不依赖字体度量差异）
            double y = 12;
            drawingContext.DrawText(numberText, new Point(14, y));
            y += numberText.Height + 1;
            drawingContext.DrawText(captionText, new Point(14, y));
        }

        private static FormattedText Build(string text, FontFamily family, double size, FontWeight weight,
                                           Brush brush, double maxWidth, double pixelsPerDip)
        {
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);
            var formatted = new FormattedText(
                string.IsNullOrEmpty(text) ? string.Empty : text,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                brush,
                // 带 PixelsPerDip：不传时按 1.0 量字，高缩放屏上「数字 + 说明」会挤在一起
                pixelsPerDip);

            if (maxWidth > 0)
            {
                formatted.MaxTextWidth = maxWidth;
                formatted.MaxLineCount = 1;
                formatted.Trimming = TextTrimming.CharacterEllipsis;
            }

            return formatted;
        }

        private void OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _hover = true;
            // 上浮 2px：用 RenderTransform 而不是 Margin/TranslatePoint，
            // 布局不参与，鼠标移动时不会反复触发重新测量
            _lift.Y = -2;
            InvalidateVisual();
        }

        private void OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
        {
            _hover = false;
            _lift.Y = 0;
            InvalidateVisual();
        }
    }
}
