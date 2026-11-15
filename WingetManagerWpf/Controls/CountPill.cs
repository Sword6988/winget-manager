using System.Globalization;
using System.Windows;
using System.Windows.Media;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 分组头的计数胶囊：20 高、按文字自适应宽。
    /// 左侧按「已勾选 / 可勾选」比例填一层淡紫，勾满时整颗换成渐变底 + 白字。
    /// <para>
    /// 为什么不用 TextBlock + Border 拼：比例填充是按**像素宽度**算的，
    /// XAML 里拿不到「文字实际宽度」，只能自绘。
    /// </para>
    /// </summary>
    public sealed class CountPill : FrameworkElement
    {
        public static readonly DependencyProperty CountProperty = DependencyProperty.Register(
            "Count", typeof(int), typeof(CountPill),
            new FrameworkPropertyMetadata(0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TotalProperty = DependencyProperty.Register(
            "Total", typeof(int), typeof(CountPill),
            new FrameworkPropertyMetadata(0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

        private const double HorizontalPadding = 7;

        public CountPill()
        {
            Height = Metrics.PillHeight;
            SnapsToDevicePixels = true;
            VerticalAlignment = VerticalAlignment.Center;
            IsHitTestVisible = false;
        }

        /// <summary>已勾选数（<see cref="Total"/> 为 0 时显示成总数）。</summary>
        public int Count
        {
            get { return (int)GetValue(CountProperty); }
            set { SetValue(CountProperty, value); }
        }

        /// <summary>可勾选数；0 表示这一组不可勾选（例如「winget 不支持」）。</summary>
        public int Total
        {
            get { return (int)GetValue(TotalProperty); }
            set { SetValue(TotalProperty, value); }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            FormattedText text = BuildText();
            double width = text.WidthIncludingTrailingWhitespace + HorizontalPadding * 2;
            return new Size(width < 26 ? 26 : width, Metrics.PillHeight);
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

            FormattedText text = BuildText();
            double radius = height / 2;
            var rect = new Rect(0, 0, width, height);

            Brush ring = ThemeLookup.GetBrush(this, "Brush.PillRing", Brushes.WhiteSmoke);
            Brush ringFull = ThemeLookup.GetBrush(this, "Brush.PillRingFull", Brushes.MediumPurple);
            Brush soft = ThemeLookup.GetBrush(this, "Brush.AccentSoft", Brushes.Lavender);
            Brush gradient = ThemeLookup.GetBrush(this, "Brush.AccentGradient", Brushes.MediumPurple);
            Brush muted = ThemeLookup.GetBrush(this, "Brush.Muted2", Brushes.Gray);

            bool full = Total > 0 && Count >= Total;

            if (Total <= 0)
            {
                // 不可勾选的分组：只显示一个中性计数，没有比例可画
                drawingContext.DrawRoundedRectangle(ring, null, rect, radius, radius);
                text.SetForegroundBrush(muted);
                DrawCentered(drawingContext, text, width);
                return;
            }

            if (full)
            {
                drawingContext.DrawRoundedRectangle(gradient, null, rect, radius, radius);
                text.SetForegroundBrush(Brushes.White);
            }
            else
            {
                drawingContext.DrawRoundedRectangle(ring, new Pen(ringFull, Count > 0 ? 1 : 0), rect, radius, radius);

                double fraction = (double)Count / Total;
                if (fraction > 0)
                {
                    drawingContext.PushClip(new RectangleGeometry(rect, radius, radius));
                    drawingContext.DrawRectangle(soft, null, new Rect(0, 0, width * fraction, height));
                    drawingContext.Pop();
                }

                text.SetForegroundBrush(muted);
            }

            DrawCentered(drawingContext, text, width);
        }

        private void DrawCentered(DrawingContext drawingContext, FormattedText text, double width)
        {
            double x = (width - text.WidthIncludingTrailingWhitespace) / 2;
            if (x < 0)
            {
                x = 0;
            }

            double y = (ActualHeight - text.Height) / 2;
            if (y < 0)
            {
                y = 0;
            }

            drawingContext.DrawText(text, new Point(x, y));
        }

        private FormattedText BuildText()
        {
            string content = Total > 0
                ? Count.ToString(CultureInfo.InvariantCulture) + "/" + Total.ToString(CultureInfo.InvariantCulture)
                : Count.ToString(CultureInfo.InvariantCulture);

            FontFamily family = ThemeLookup.GetFontFamily(
                this, "FontFamily.Ui", new FontFamily("Microsoft YaHei UI"));
            double size = ThemeLookup.GetDouble(this, "FontSize.Chip", 11.7);
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

            return new FormattedText(
                content,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                Brushes.Gray,
                // 必须带 PixelsPerDip：不带的话 WPF 按 1.0 量字，高缩放屏上胶囊会裁字
                ThemeLookup.GetPixelsPerDip(this));
        }
    }
}
