using System;
using System.Windows;
using System.Windows.Media;

namespace WingetManagerWpf.Controls
{
    /// <summary>
    /// 自绘控件 / 代码构建控件用的令牌查找工具。
    /// <para>
    /// 自绘控件的画刷、字号没法在 XAML 里写 <c>{StaticResource}</c>，只能在代码里查资源字典。
    /// 但 <see cref="FrameworkElement.FindResource"/> 在**控件还没加入可视树**时会抛
    /// <c>ResourceReferenceKeyNotFoundException</c>（行的 Bind 就发生在加入容器之前），
    /// 所以统一走 <c>TryFindResource</c> + <see cref="Application"/> 兜底，
    /// 查不到就退化成硬编码兜底色 —— 少一层颜色比整个列表建不出来好得多。
    /// </para>
    /// </summary>
    internal static class ThemeLookup
    {
        /// <summary>取画刷；查不到返回 <paramref name="fallback"/>（保证不是 null）。</summary>
        public static Brush GetBrush(FrameworkElement owner, string key, Brush fallback)
        {
            var brush = Find(owner, key) as Brush;
            return brush ?? fallback;
        }

        /// <summary>取 double 令牌（字号等）。</summary>
        public static double GetDouble(FrameworkElement owner, string key, double fallback)
        {
            object value = Find(owner, key);
            if (value is double)
            {
                return (double)value;
            }

            return fallback;
        }

        /// <summary>取字体令牌。</summary>
        public static FontFamily GetFontFamily(FrameworkElement owner, string key, FontFamily fallback)
        {
            var family = Find(owner, key) as FontFamily;
            return family ?? fallback;
        }

        /// <summary>取圆角令牌。</summary>
        public static CornerRadius GetCornerRadius(FrameworkElement owner, string key, CornerRadius fallback)
        {
            object value = Find(owner, key);
            if (value is CornerRadius)
            {
                return (CornerRadius)value;
            }

            return fallback;
        }

        /// <summary>取颜色令牌（GradientStop 等只吃 Color 的场合）。</summary>
        public static Color GetColor(FrameworkElement owner, string key, Color fallback)
        {
            object value = Find(owner, key);
            if (value is Color)
            {
                return (Color)value;
            }

            return fallback;
        }

        /// <summary>
        /// 当前 DPI 缩放比（1.0 = 96 DPI）。自绘文字**必须**把它传给
        /// <c>FormattedText</c> 的 <c>pixelsPerDip</c> 参数：
        /// <list type="bullet">
        /// <item>不传时 WPF 按 1.0 处理，在 125%/150% 缩放下量出来的宽高偏小 ——
        /// 数字胶囊会被裁掉半截、统计卡的第二行会压到第一行上；</item>
        /// <item>程序清单已声明 PerMonitorV2，同一窗口被拖到另一块缩放不同的屏上时
        /// 该值会变，所以每次自绘都要现取，不能缓存成常量。</item>
        /// </list>
        /// 控件尚未接入可视树时 <see cref="VisualTreeHelper.GetDpi"/> 可能抛异常，
        /// 此时退回 1.0（等同于旧行为，不会更糟）。
        /// </summary>
        public static double GetPixelsPerDip(Visual visual)
        {
            if (visual != null)
            {
                try
                {
                    return VisualTreeHelper.GetDpi(visual).PixelsPerDip;
                }
                catch (Exception)
                {
                    // 未接入可视树 / 正在卸载：退回 1.0
                }
            }

            return 1.0;
        }

        /// <summary>先查自身（含祖先与 App 合并字典），再查一次 <see cref="Application"/>。</summary>
        private static object Find(FrameworkElement owner, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                return null;
            }

            object value = null;
            if (owner != null)
            {
                value = owner.TryFindResource(key);
            }

            if (value == null)
            {
                Application app = Application.Current;
                if (app != null)
                {
                    value = app.TryFindResource(key);
                }
            }

            return value;
        }
    }
}
