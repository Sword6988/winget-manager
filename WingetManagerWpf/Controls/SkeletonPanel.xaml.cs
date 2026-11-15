using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WingetManagerWpf.Core;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 骨架屏。6 行占位块 + 整体呼吸动画（透明度 1.0 ↔ 0.45，900ms 往返）。
    /// <para>
    /// 只在「首扫期间且没有任何数据」时显示：已经有上一次的扫描结果时，
    /// 重新扫描应该继续显示旧列表（用户还能继续勾选），而不是把列表换成骨架。
    /// </para>
    /// </summary>
    public partial class SkeletonPanel : UserControl
    {
        private const int RowCount = 6;

        public SkeletonPanel()
        {
            InitializeComponent();
            BuildRows();

            Loaded += delegate { StartBreathing(); };
            Unloaded += delegate { StopBreathing(); };
        }

        private void BuildRows()
        {
            Brush block1 = ThemeLookup.GetBrush(this, "Brush.Skel1", Brushes.Gainsboro);
            Brush block2 = ThemeLookup.GetBrush(this, "Brush.Skel2", Brushes.WhiteSmoke);

            for (int i = 0; i < RowCount; i++)
            {
                var row = new Grid();
                row.Height = Metrics.RowHeight;
                row.Margin = new Thickness(0);

                var circle = new Border();
                circle.Width = 38;
                circle.Height = 38;
                circle.CornerRadius = new CornerRadius(11);
                circle.Background = block1;
                circle.HorizontalAlignment = HorizontalAlignment.Left;
                circle.VerticalAlignment = VerticalAlignment.Center;
                circle.Margin = new Thickness(16, 0, 0, 0);
                row.Children.Add(circle);

                var stack = new StackPanel();
                stack.VerticalAlignment = VerticalAlignment.Center;
                stack.Margin = new Thickness(67, 0, 0, 0);

                var nameBar = new Border();
                nameBar.Height = 11;
                nameBar.Width = 230 + (i % 3) * 40;
                nameBar.CornerRadius = new CornerRadius(4);
                nameBar.Background = block1;
                nameBar.HorizontalAlignment = HorizontalAlignment.Left;
                stack.Children.Add(nameBar);

                var idBar = new Border();
                idBar.Height = 9;
                idBar.Width = 140 + (i % 2) * 30;
                idBar.CornerRadius = new CornerRadius(4);
                idBar.Background = block2;
                idBar.HorizontalAlignment = HorizontalAlignment.Left;
                idBar.Margin = new Thickness(0, 7, 0, 0);
                stack.Children.Add(idBar);

                row.Children.Add(stack);
                Host.Children.Add(row);
            }
        }

        private void StartBreathing()
        {
            // 用 DoubleAnimation 而不是 DispatcherTimer 改 Opacity：
            // 动画跑在合成线程上，即使 UI 线程正忙着建列表也不会卡顿
            var animation = new DoubleAnimation();
            animation.From = 1.0;
            animation.To = 0.45;
            animation.Duration = new Duration(TimeSpan.FromMilliseconds(900));
            animation.AutoReverse = true;
            animation.RepeatBehavior = RepeatBehavior.Forever;
            animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };

            BeginAnimation(OpacityProperty, animation);
        }

        private void StopBreathing()
        {
            // 隐藏时必须停掉：Forever 动画会让控件一直被动画系统持有，窗口关闭时排不干净
            BeginAnimation(OpacityProperty, null);
            Opacity = 1.0;
        }
    }
}
