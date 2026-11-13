using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WingetManager.ViewModels;

namespace WingetManager.AppServices;

/// <summary>
/// 列表条目模板选择器：组头条目 vs 行条目（AOT 安全——只有精确类型判断，
/// 不走反射型 DataType 查找；两个模板都在 XAML 资源里显式声明）。
/// </summary>
public sealed partial class ListEntryTemplateSelector : DataTemplateSelector
{
    /// <summary>组头条目模板。</summary>
    public DataTemplate? HeaderTemplate { get; set; }

    /// <summary>行条目模板。</summary>
    public DataTemplate? RowTemplate { get; set; }

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item)
        => item switch
        {
            GroupHeaderEntry => HeaderTemplate,
            PackageRowEntry => RowTemplate,
            _ => null,
        };

    /// <inheritdoc />
    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}
