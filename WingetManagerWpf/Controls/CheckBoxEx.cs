using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 自绘复选框（19×19，圆角 5）。未选：<c>RowAlt</c> 底 + 1px <c>Line</c> 边；
    /// 选中：<c>AccentGradient</c> 底 + 2px 白勾 + 内高光。
    /// <para>
    /// 为什么不用 <c>CheckBox</c>：默认模板里带着 20×20 的方框图标和 4px 的 margin，
    /// 行内的 46 高布局会被它顶歪；重写模板又要塞一堆 <c>ToggleButton</c> 触发器。
    /// 自绘只画两笔，尺寸完全可控，也方便做「不可勾选行留 31 位占位」。
    /// </para>
    /// </summary>
    public sealed class CheckBoxEx : FrameworkElement
    {
        public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
            "IsChecked", typeof(bool), typeof(CheckBoxEx),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.AffectsRender,
                OnIsCheckedChanged));

        private bool _hover;

        /// <summary>勾选状态变化（用户点击或代码改 DP 都会触发）。</summary>
        public event EventHandler CheckedChanged;

        public CheckBoxEx()
        {
            Width = Metrics.CheckBoxSize - 1;
            Height = Metrics.CheckBoxSize - 1;
            SnapsToDevicePixels = true;
            Cursor = Cursors.Hand;
            Focusable = false;
            IsHitTestVisible = true;

            MouseEnter += delegate { _hover = true; InvalidateVisual(); };
            MouseLeave += delegate { _hover = false; InvalidateVisual(); };
        }

        public bool IsChecked
        {
            get { return (bool)GetValue(IsCheckedProperty); }
            set { SetValue(IsCheckedProperty, value); }
        }

        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            base.OnMouseLeftButtonUp(e);

            // 只认「按下与抬起都在自己身上」的点击：拖选文本时抬起落在复选框上不该改状态。
            // MouseLeftButtonUp 已经隐含这一点（捕获在按下时被夺走就不会送到这里）
            IsChecked = !IsChecked;
            e.Handled = true;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            if (drawingContext == null)
            {
                return;
            }

            double size = Math.Min(ActualWidth, ActualHeight);
            if (size <= 0)
            {
                size = Metrics.CheckBoxSize - 1;
            }

            double radius = 5;
            var rect = new Rect(0, 0, size, size);

            Brush offFill = ThemeLookup.GetBrush(this, "Brush.CheckFill", Brushes.White);
            Brush offLine = ThemeLookup.GetBrush(this, "Brush.CheckLine", Brushes.Gray);
            Brush hoverFill = ThemeLookup.GetBrush(this, "Brush.AccentSoft", Brushes.AliceBlue);
            Brush hoverLine = ThemeLookup.GetBrush(this, "Brush.Accent", Brushes.MediumPurple);
            Brush onFill = ThemeLookup.GetBrush(this, "Brush.AccentGradient", Brushes.MediumPurple);

            if (!IsChecked)
            {
                // 1.5px 描边收在内侧（0.75 内缩）：贴边画会把一半线宽画到控件外面，
                // 19px 的框看起会像 18px，和右侧 31px 占位对不齐
                var offRect = new Rect(0.75, 0.75, size - 1.5, size - 1.5);
                var pen = new Pen(_hover ? hoverLine : offLine, 1.5);
                drawingContext.DrawRoundedRectangle(_hover ? hoverFill : offFill, pen, offRect, radius, radius);
                return;
            }

            drawingContext.DrawRoundedRectangle(onFill, null, rect, radius, radius);

            // 内高光：4.8 没有 BitmapEffect，做不出真正的外发光，
            // 用一圈半透明白描边模拟「点亮」的立体感，视觉上足够
            var sheen = new SolidColorBrush(Color.FromArgb(56, 255, 255, 255));
            sheen.Freeze();
            drawingContext.DrawRoundedRectangle(null, new Pen(sheen, 1),
                new Rect(0.75, 0.75, size - 1.5, size - 1.5), radius - 0.5, radius - 0.5);

            // 白色 2px 勾：按比例取点，尺寸变化时不用重算常量
            double left = size * 0.26;
            double midX = size * 0.43;
            double right = size * 0.75;
            double midY = size * 0.70;
            double topY = size * 0.32;

            var geometry = new StreamGeometry();
            using (StreamGeometryContext context = geometry.Open())
            {
                context.BeginFigure(new Point(left, size * 0.52), false, false);
                context.LineTo(new Point(midX, midY), true, false);
                context.LineTo(new Point(right, topY), true, false);
            }

            geometry.Freeze();

            var checkPen = new Pen(Brushes.White, 2);
            checkPen.StartLineCap = PenLineCap.Round;
            checkPen.EndLineCap = PenLineCap.Round;
            checkPen.LineJoin = PenLineJoin.Round;
            drawingContext.DrawGeometry(null, checkPen, geometry);
        }

        private static void OnIsCheckedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var box = d as CheckBoxEx;
            if (box == null)
            {
                return;
            }

            box.InvalidateVisual();

            EventHandler handler = box.CheckedChanged;
            if (handler != null)
            {
                handler(box, EventArgs.Empty);
            }
        }
    }
}
