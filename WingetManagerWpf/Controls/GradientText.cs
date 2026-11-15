using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 渐变文字：用 <see cref="FormattedText"/> 自绘一行文字，画刷取
    /// <c>Brush.AccentGradient</c>（紫蓝渐变），用作标题与底栏的勾选数字。
    /// <para>
    /// 为什么不用 <c>TextBlock.Foreground</c> + <c>LinearGradientBrush</c>：
    /// 那条路只在文字**一个字符都不能换行、且宽度固定**时才对齐；标题文字长度一变，
    /// 渐变的起止点就跟着变，颜色会「跳」。自绘时把渐变按字宽锁定，视觉才稳定。
    /// </para>
    /// </summary>
    public sealed class GradientText : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            "Text", typeof(string), typeof(GradientText),
            new FrameworkPropertyMetadata(string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TextFontSizeProperty = DependencyProperty.Register(
            "TextFontSize", typeof(double), typeof(GradientText),
            new FrameworkPropertyMetadata(25.3,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty BoldProperty = DependencyProperty.Register(
            "Bold", typeof(bool), typeof(GradientText),
            new FrameworkPropertyMetadata(true,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
            "Foreground", typeof(Brush), typeof(GradientText),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.AffectsRender));

        public GradientText()
        {
            SnapsToDevicePixels = true;
        }

        /// <summary>要显示的文字。</summary>
        public string Text
        {
            get { return (string)GetValue(TextProperty); }
            set { SetValue(TextProperty, value); }
        }

        /// <summary>字号（DIP）。默认取标题字号 25.3（19pt 换算）。</summary>
        public double TextFontSize
        {
            get { return (double)GetValue(TextFontSizeProperty); }
            set { SetValue(TextFontSizeProperty, value); }
        }

        public bool Bold
        {
            get { return (bool)GetValue(BoldProperty); }
            set { SetValue(BoldProperty, value); }
        }

        /// <summary>文字画刷；为 null 时回落到 <c>Brush.AccentGradient</c>。</summary>
        public Brush Foreground
        {
            get { return (Brush)GetValue(ForegroundProperty); }
            set { SetValue(ForegroundProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText text = BuildText();
            if (text == null)
            {
                return new Size(0, 0);
            }

            return new Size(text.WidthIncludingTrailingWhitespace, text.Height);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                return;
            }

            FormattedText text = BuildText();
            if (text == null)
            {
                return;
            }

            Brush brush = Foreground;
            if (brush == null)
            {
                brush = ThemeLookup.GetBrush(this, "Brush.AccentGradient", SystemColors.ControlTextBrush);
            }

            text.SetForegroundBrush(brush);
            drawingContext.DrawText(text, new Point(0, 0));
        }

        private FormattedText BuildText()
        {
            string content = Text ?? string.Empty;
            if (content.Length == 0)
            {
                return null;
            }

            FontFamily family = ThemeLookup.GetFontFamily(
                this, "FontFamily.Ui", new FontFamily("Microsoft YaHei UI"));
            FontWeight weight = Bold ? FontWeights.Bold : FontWeights.Normal;
            var typeface = new Typeface(family, FontStyles.Normal, weight, FontStretches.Normal);

            double size = TextFontSize > 0 ? TextFontSize : 25.3;

            // 用 7 参数重载并显式传 PixelsPerDip（.NET 4.6.2+ 才有该重载，本项目目标 4.8）。
            // 旧注释说「用 6 参数重载以兼容」是错的：6 参数重载已被标记 Obsolete，
            // 且它按 1.0 量字，在 150% 缩放下标题会被裁掉最后一两个字。
            return new FormattedText(
                content,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                SystemColors.ControlTextBrush,
                ThemeLookup.GetPixelsPerDip(this));
        }
    }
}
