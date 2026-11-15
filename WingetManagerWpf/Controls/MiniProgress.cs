using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 3px 高的不确定进度条（扫描期间显示）：
    /// 一段 140px 的渐变滑块在轨道上循环右移，让用户知道「还在动」。
    /// <para>
    /// 为什么不用 <see cref="System.Windows.Controls.ProgressBar"/>：默认模板自带边框、
    /// 有确定值才动，而且高度受模板控制压不到 3px；扫描的剩余时间无法估算，
    /// 只能做不确定态（indeterminate），自绘最省事。
    /// </para>
    /// 停止时立刻隐藏（Collapsed）而不是留一条空轨：空轨会被误读成「0%」。
    /// </summary>
    public sealed class MiniProgress : FrameworkElement
    {
        public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
            "IsActive", typeof(bool), typeof(MiniProgress),
            new FrameworkPropertyMetadata(false,
                FrameworkPropertyMetadataOptions.AffectsRender, OnIsActiveChanged));

        private const double SliderWidth = 140;
        private const int FrameMs = 16;
        private const double StepPerFrame = 6;

        private readonly DispatcherTimer _timer;
        private double _offset;

        public MiniProgress()
        {
            Height = 3;
            SnapsToDevicePixels = true;
            HorizontalAlignment = HorizontalAlignment.Stretch;
            IsHitTestVisible = false;

            // DispatcherTimer 是唯一能在 UI 线程安全的「帧驱动」定时器：
            // 每帧 16ms 约等于 60fps，比 CompositionTarget.Rendering 便宜且可控
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = TimeSpan.FromMilliseconds(FrameMs);
            _timer.Tick += OnTick;

            Loaded += delegate { SyncTimer(); };
            Unloaded += delegate { _timer.Stop(); };
        }

        /// <summary>是否正在滚动；false 时控件隐藏。</summary>
        public bool IsActive
        {
            get { return (bool)GetValue(IsActiveProperty); }
            set { SetValue(IsActiveProperty, value); }
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

            Brush track = ThemeLookup.GetBrush(this, "Brush.PillRing", Brushes.WhiteSmoke);
            Brush slider = ThemeLookup.GetBrush(this, "Brush.AccentGradient", Brushes.MediumPurple);

            drawingContext.DrawRoundedRectangle(track, null, new Rect(0, 0, width, height), 1.5, 1.5);

            if (!IsActive)
            {
                return;
            }

            // 滑块从左外侧滑到右外侧，循环距离 = 轨道宽 + 滑块宽
            double x = _offset - SliderWidth;
            drawingContext.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), 1.5, 1.5));
            drawingContext.DrawRoundedRectangle(slider, null,
                new Rect(x, 0, SliderWidth, height), 1.5, 1.5);
            drawingContext.Pop();
        }

        private void OnTick(object sender, EventArgs e)
        {
            double width = ActualWidth;
            if (width <= 0)
            {
                return;
            }

            _offset += StepPerFrame;
            if (_offset > width + SliderWidth)
            {
                _offset = 0;
            }

            InvalidateVisual();
        }

        private void SyncTimer()
        {
            if (IsActive && IsLoaded)
            {
                if (!_timer.IsEnabled)
                {
                    _offset = 0;
                    _timer.Start();
                }
            }
            else if (_timer.IsEnabled)
            {
                _timer.Stop();
            }
        }

        private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var bar = d as MiniProgress;
            if (bar == null)
            {
                return;
            }

            bar.Visibility = bar.IsActive ? Visibility.Visible : Visibility.Collapsed;
            bar.SyncTimer();
            bar.InvalidateVisual();
        }
    }
}
