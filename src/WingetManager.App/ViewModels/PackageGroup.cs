using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;

namespace WingetManager.ViewModels;

/// <summary>
/// 分组 VM（对应 ARCHITECTURE §3 <c>PackageGroup</c>）。
///
/// 现在它只承担两个职责（不再直接进视图——AOT 下 CVS 分组条目丢失，
/// 见 <see cref="ListEntry"/> 注释）：
/// <list type="number">
/// <item>它是「行的集合」——组内行的持有者，供全选本组 / 统计等编排逻辑枚举；</item>
/// <item>它是「一个组头的数据源」——<see cref="GroupHeaderEntry"/> 包着它进列表，
///       组头模板用 <c>x:Bind</c> 直接绑属性。</item>
/// </list>
/// 顺序由 <see cref="MainViewModel"/> 按「软件 → 运行库 → winget 不支持」固定给出。
/// </summary>
public sealed class PackageGroup : ObservableCollection<PackageRow>, IGrouping<string, PackageRow>
{
    /// <summary>创建分组。</summary>
    /// <param name="title">组名（同时也是 <see cref="Key"/>）。</param>
    /// <param name="rows">该组当前可见的行。</param>
    /// <param name="toggleGroup">「全选本组 / 全不选」回调（由 VM 实现）。</param>
    public PackageGroup(string title, IEnumerable<PackageRow> rows, Action<string> toggleGroup)
    {
        Title = title ?? throw new ArgumentNullException(nameof(title));
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(toggleGroup);

        foreach (var row in rows)
        {
            Add(row);
        }

        ToggleGroupCommand = new RelayCommand(() => toggleGroup(Title));
    }

    /// <summary>组名（软件 / 运行库 / winget 不支持）。</summary>
    public string Title { get; }

    /// <summary>分组键（<see cref="IGrouping{TKey,TElement}"/> 要求）。</summary>
    public string Key => Title;

    /// <summary>组头计数文案。</summary>
    public string CountText => Count + " 项";

    /// <summary>组头按钮文案：组内全勾时提示「全不选」。</summary>
    public string ToggleGroupText => AllChecked ? "全不选" : "全选本组";

    /// <summary>本组是否已经全被勾选（只看可勾选行）。</summary>
    public bool AllChecked
    {
        get
        {
            var selectable = this.Where(row => row.IsSelectable).ToList();
            return selectable.Count > 0 && selectable.All(row => row.IsChecked);
        }
    }

    /// <summary>组头「全选本组 / 全不选」命令（只作用于本组可见行）。</summary>
    public RelayCommand ToggleGroupCommand { get; }

    /// <summary>
    /// 勾选变化后刷新组头文案（<see cref="ToggleGroupText"/>）。
    /// 组头模板用 OneWay 绑定，靠这条通知把按钮文案在「全选本组 / 全不选」之间切换。
    /// </summary>
    public void NotifySelectionChanged()
    {
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(ToggleGroupText)));
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(AllChecked)));
    }
}
