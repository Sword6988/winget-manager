using System;
using System.Windows;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 无边框透明对话框的尺寸收敛工具。
    /// <para>
    /// 为什么不用 <c>SizeToContent="Height"</c>：这个属性由布局系统在**首帧之后**才把窗口撑到内容高度，
    /// 而 <c>WindowStyle=None + AllowsTransparency=True</c> 的窗口在第一帧没有非客户区可借，
    /// 于是会先以 XAML 里的初值（或 0）显示一帧，再跳到正确高度 —— 慢机器/高 DPI 下能明显看到「抖一下」。
    /// 改成 <c>SizeToContent=Manual</c> + XAML 给一个接近的初值，再在 <c>Loaded</c> 里精确量一次，
    /// 窗口从第一帧起就是最终尺寸。
    /// </para>
    /// <para>
    /// 量法：对根元素（外层带 18px 阴影留边的 Border）用无限高度测量，
    /// <see cref="FrameworkElement.DesiredSize"/> 已含 Margin，等于整窗高度 ——
    /// <c>AllowsTransparency=True</c> 时客户区就是整个窗口，不需要再减边框/标题栏。
    /// </para>
    /// </summary>
    public static class DialogSizer
    {
        /// <summary>兜底最小高度：内容为空时也不至于缩成一条线，用户还能拖动/关闭。</summary>
        public const double MinHeight = 120;

        /// <summary>
        /// 按内容把窗口高度收到精确值，并把窗口重新相对 Owner 居中。
        /// 内容发生变化（例如导出对话框从表单页切到成功页）后可重复调用。
        /// </summary>
        /// <param name="window">目标窗口（宽度应已在 XAML 里固定）。</param>
        /// <param name="root">内容根元素，通常是那层带 Margin 的外框 Border。</param>
        public static void FitToContent(Window window, FrameworkElement root)
        {
            if (window == null || root == null)
            {
                return;
            }

            try
            {
                double width = window.Width;
                if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                {
                    width = window.ActualWidth > 0 ? window.ActualWidth : 560;
                }

                // Margin 会算进 DesiredSize，所以这里量出来的就是整窗高度
                root.Measure(new Size(width, double.PositiveInfinity));
                double height = Math.Ceiling(root.DesiredSize.Height);
                if (height < MinHeight)
                {
                    height = MinHeight;
                }

                if (Math.Abs(window.Height - height) > 0.5)
                {
                    window.Height = height;
                }

                // 高度改完必须让布局跑一轮，否则 ActualHeight 还是旧值，居中会算偏
                root.UpdateLayout();
            }
            catch (Exception)
            {
                // 尺寸收敛失败不该拦着对话框显示：初值本身是合理高度
            }

            CenterOnOwner(window);
        }

        /// <summary>
        /// 相对 Owner 重新居中。WPF 只在首次 Show 时按当时的 Height 居中一次，
        /// 之后改高度窗口会「往上长」，底边压到任务栏上，所以必须手工再算一次。
        /// </summary>
        public static void CenterOnOwner(Window window)
        {
            if (window == null)
            {
                return;
            }

            Window owner = window.Owner;
            if (owner == null)
            {
                return;
            }

            try
            {
                // 优先用 Width/Height 属性：刚改完高度时 ActualHeight 可能还是上一轮的旧值
                double width = window.Width;
                if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
                {
                    width = window.ActualWidth;
                }

                double height = window.Height;
                if (double.IsNaN(height) || double.IsInfinity(height) || height <= 0)
                {
                    height = window.ActualHeight;
                }

                double left = owner.Left + (owner.ActualWidth - width) / 2.0;
                double top = owner.Top + (owner.ActualHeight - height) / 2.0;

                // Owner 贴边或最大化时可能算出屏外坐标：夹回虚拟屏内
                double virtualLeft = SystemParameters.VirtualScreenLeft;
                double virtualTop = SystemParameters.VirtualScreenTop;
                double virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
                double virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;

                if (left + width > virtualRight)
                {
                    left = virtualRight - width;
                }

                if (left < virtualLeft)
                {
                    left = virtualLeft;
                }

                if (top + height > virtualBottom)
                {
                    top = virtualBottom - height;
                }

                if (top < virtualTop)
                {
                    top = virtualTop;
                }

                window.Left = left;
                window.Top = top;
            }
            catch (Exception)
            {
                // 居中失败就退回系统给的位置，不影响功能
            }
        }
    }
}
