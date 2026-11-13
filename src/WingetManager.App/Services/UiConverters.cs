using Microsoft.UI.Xaml;

namespace WingetManager.AppServices;

/// <summary>
/// XAML 里用的编译期函数绑定（<c>{x:Bind app:UiConverters.VisibleIf(...)}</c>）。
///
/// 为什么不用 <c>IValueConverter</c>：转换器要靠 <c>StaticResource</c> 在运行时按名查找实例，
/// 属于反射型资源查找，AOT + <c>TrimMode=full</c> 下的高风险面；<c>x:Bind</c> 的函数绑定
/// 在编译期直接生成调用代码，零查找零反射。
/// </summary>
public static class UiConverters
{
    /// <summary>true → 可见，false → 折叠（不占布局空间）。</summary>
    public static Visibility VisibleIf(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>true → 折叠，false → 可见。</summary>
    public static Visibility CollapsedIf(bool value) => value ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>非空字符串 → 可见（用于提示文案这类「有内容才显示」的元素）。</summary>
    public static Visibility VisibleIfAny(string? text) =>
        string.IsNullOrWhiteSpace(text) ? Visibility.Collapsed : Visibility.Visible;
}
