using System;
using CommunityToolkit.Mvvm.ComponentModel;
using WingetManager.Models;

namespace WingetManager.ViewModels;

/// <summary>
/// 列表行 VM：包模型 + 勾选状态（对应 ARCHITECTURE §3 <c>PackageRow</c>）。
///
/// 行对象由 <see cref="MainViewModel.RebuildGroups"/> 整体重建，因此除
/// <see cref="IsChecked"/> 之外的展示属性都是「构造后不变」的只读计算属性；
/// 唯一会变的是勾选（用户点）与运行库改判（后台富化回来时整批重建）。
///
/// <see cref="IsChecked"/> 是<b>纯投影</b>：界面单向绑定（OneWay）它，用户点击由
/// 视图代码显式调 <see cref="MainViewModel.TrySetRowChecked"/> 上报，行自己不做任何
/// 回写。原因见该方法注释——TwoWay 绑定在虚拟化容器回收时可能误写 false，
/// 表现为「切换分组后勾选丢失」。
/// </summary>
public sealed partial class PackageRow : ObservableObject
{
    /// <summary>创建行 VM。</summary>
    /// <param name="model">包模型（不做拷贝，改判直接改模型上的 IsDependency）。</param>
    /// <param name="isChecked">初始勾选状态（来自勾选真相集合）。</param>
    public PackageRow(Package model, bool isChecked)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _isChecked = isChecked;
    }

    /// <summary>底层包模型（导出、勾选落盘都用它的 ID）。</summary>
    public Package Model { get; }

    /// <summary>包 ID（勾选集合的键）。</summary>
    public string PackageIdentifier => Model.PackageIdentifier;

    /// <summary>显示名称（Core 已保证非空，为空时回退成 ID）。</summary>
    public string Name => Model.Name;

    /// <summary>副标题：包 ID + 来源，例如 <c>Google.Chrome · winget</c>。</summary>
    public string Subtitle =>
        Model.Source.Length > 0
            ? Model.PackageIdentifier + "  ·  " + Model.Source
            : Model.PackageIdentifier;

    /// <summary>已安装版本（空则显示占位符，避免行高跳动）。</summary>
    public string VersionText => Model.Version.Length > 0 ? Model.Version : "—";

    /// <summary>是不是运行库/系统组件（运行库角标 + 分组依据）。</summary>
    public bool IsDependency => Model.IsDependency;

    /// <summary>运行库角标是否显示（非运行库不占位）。</summary>
    public bool ShowDependencyBadge => Model.IsDependency;

    /// <summary>
    /// 是否可勾选：只有能被 winget 重装的包才让勾。
    /// 「winget 不支持」组的行灰显、勾选框禁用，但照样列出来供核对。
    /// </summary>
    public bool IsSelectable => Model.Reinstallable;

    /// <summary>不可勾选行的整体透明度（灰显但不隐藏，P0-7）。</summary>
    public double RowOpacity => Model.Reinstallable ? 1.0 : 0.45;

    /// <summary>
    /// 勾选状态（界面 OneWay 绑定到 CheckBox；写入只来自 <see cref="MainViewModel"/>）。
    /// </summary>
    [ObservableProperty]
    private bool _isChecked;
}
