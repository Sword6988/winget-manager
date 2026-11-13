using System;

namespace WingetManager.ViewModels;

/// <summary>
/// 列表条目基类：组头与行拍平后放在同一个 <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/>
/// 里交给 <c>ListView</c>，用 <see cref="AppServices.ListEntryTemplateSelector"/> 区分模板。
///
/// 为什么不用 <c>CollectionViewSource</c> 分组：WinUI 3 在 Native AOT 下 CVS 只创建组头、
/// 组内条目进不了视图（microsoft-ui-xaml issue #10881，官方 Backlog、暂无修复）。
/// 扁平 ObservableCollection 绑定不经过 CVS 分组管道，不受影响；代价是失去组头吸顶
/// （本来就是 P1），后续做吸顶时再换 ItemsRepeater 自定义布局。
/// </summary>
public abstract class ListEntry
{
    protected ListEntry()
    {
    }
}

/// <summary>组头条目：包着 <see cref="PackageGroup"/>，模板直接绑它的属性。</summary>
public sealed class GroupHeaderEntry : ListEntry
{
    /// <summary>创建组头条目。</summary>
    public GroupHeaderEntry(PackageGroup group)
    {
        Group = group ?? throw new ArgumentNullException(nameof(group));
    }

    /// <summary>组头数据（标题 / 计数 / 全选本组命令都在它身上）。</summary>
    public PackageGroup Group { get; }
}

/// <summary>行条目：包着 <see cref="PackageRow"/>，模板直接绑它的属性。</summary>
public sealed class PackageRowEntry : ListEntry
{
    /// <summary>创建行条目。</summary>
    public PackageRowEntry(PackageRow row)
    {
        Row = row ?? throw new ArgumentNullException(nameof(row));
    }

    /// <summary>行数据（勾选框 / 名称 / 版本 / 角标都在它身上）。</summary>
    public PackageRow Row { get; }
}
