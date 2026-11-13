using System;
using System.Collections.Generic;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 三个固定分组（顺序固定：软件 → 运行库 → winget 不支持，后者沉底）。
/// 分组<b>只由</b> <see cref="Package.Reinstallable"/> 与 <see cref="Package.IsDependency"/> 决定，
/// 不含任何关键词分类（对应 Python 版 qt_common 的 SOFTWARE_GROUP 等常量）。
/// </summary>
public static class PackageGroups
{
    /// <summary>分组一：winget 可装的普通软件，可勾选导出。</summary>
    public const string Software = "软件";

    /// <summary>分组二：winget 可装的运行库/系统组件，同样可勾选。</summary>
    public const string Dependency = "运行库";

    /// <summary>分组三：winget 装不上，灰显仅作核对，固定垫底。</summary>
    public const string Unsupported = "winget 不支持";

    /// <summary>「全部」胶囊的文案（不是分组，只是筛选器的空值占位）。</summary>
    public const string All = "全部";

    /// <summary>分组的固定顺序。</summary>
    public static readonly IReadOnlyList<string> Order = new[] { Software, Dependency, Unsupported };

    /// <summary>分组排序权重（未知分组沉到最末）。</summary>
    /// <param name="groupName">分组名。</param>
    /// <returns>排序权重。</returns>
    public static int Rank(string? groupName)
    {
        for (var i = 0; i < Order.Count; i++)
        {
            if (string.Equals(Order[i], groupName, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return Order.Count;
    }

    /// <summary>
    /// 包的分组归属：先看能不能被 winget 重装，再看是不是运行库。
    /// </summary>
    /// <param name="package">目录包。</param>
    /// <returns>三个分组名之一。</returns>
    public static string Of(Package package)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!package.Reinstallable)
        {
            return Unsupported;
        }

        return package.IsDependency ? Dependency : Software;
    }
}
