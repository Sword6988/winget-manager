using System;
using System.Collections.Generic;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 搜索 / 分组 / 「仅看已选」的纯函数谓词（原 Python 版在 MainWindow 里的
/// <c>_search_tokens</c> / <c>_pkg_matches</c> / <c>_group_of</c>）。
///
/// 放在 Core 是为了可 xUnit 直测：界面层只负责把结果喂给列表。
/// </summary>
public static class FilterEngine
{
    /// <summary>搜索词字段类型。</summary>
    public enum TermField
    {
        /// <summary>名称 + 包 ID 模糊匹配。</summary>
        Any,

        /// <summary>只匹配包 ID（<c>id:</c> 前缀）。</summary>
        Id,

        /// <summary>只匹配名称（<c>name:</c> 前缀）。</summary>
        Name,
    }

    /// <summary>一个搜索词。</summary>
    /// <param name="Field">匹配的字段。</param>
    /// <param name="Value">已小写化的匹配串。</param>
    public sealed record SearchTerm(TermField Field, string Value);

    /// <summary>
    /// 把搜索框拆成词表：空格分隔多词，天然是 AND 关系。
    /// 支持 <c>id:</c> 只匹配包 ID、<c>name:</c> 只匹配名称，其余按「名称 + 包 ID」模糊匹配。
    /// </summary>
    /// <param name="query">搜索框文本。</param>
    /// <returns>词表（空查询返回空表 = 不过滤）。</returns>
    public static IReadOnlyList<SearchTerm> ParseQuery(string? query)
    {
        var terms = new List<SearchTerm>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return terms;
        }

        foreach (var raw in query.Trim().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            if (raw.StartsWith("id:", StringComparison.Ordinal) && raw.Length > 3)
            {
                terms.Add(new SearchTerm(TermField.Id, raw[3..]));
            }
            else if (raw.StartsWith("name:", StringComparison.Ordinal) && raw.Length > 5)
            {
                terms.Add(new SearchTerm(TermField.Name, raw[5..]));
            }
            else if (raw.Length > 0)
            {
                terms.Add(new SearchTerm(TermField.Any, raw));
            }
        }

        return terms;
    }

    /// <summary>包是否满足全部搜索词（AND 语义）。</summary>
    /// <param name="package">目录包。</param>
    /// <param name="terms">搜索词表。</param>
    /// <returns>是否匹配。</returns>
    public static bool Matches(Package package, IReadOnlyList<SearchTerm> terms)
    {
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(terms);

        if (terms.Count == 0)
        {
            return true;
        }

        var name = package.Name.ToLowerInvariant();
        var packageId = package.PackageIdentifier.ToLowerInvariant();

        foreach (var term in terms)
        {
            var hit = term.Field switch
            {
                TermField.Id => packageId.Contains(term.Value, StringComparison.Ordinal),
                TermField.Name => name.Contains(term.Value, StringComparison.Ordinal),
                _ => (name + " " + packageId).Contains(term.Value, StringComparison.Ordinal),
            };

            if (!hit)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 组合出「当前视图」的谓词：分组胶囊 + 搜索词 + 仅看已选，三者 AND。
    /// </summary>
    /// <param name="query">搜索框文本。</param>
    /// <param name="group">分组胶囊；null 或 <see cref="PackageGroups.All"/> 表示不过滤。</param>
    /// <param name="onlySelected">是否只显示已勾选项。</param>
    /// <param name="isChecked">勾选查询（仅 <paramref name="onlySelected"/> 为 true 时使用）。</param>
    /// <returns>谓词。</returns>
    public static Func<Package, bool> BuildPredicate(string? query, string? group, bool onlySelected,
                                                     Func<Package, bool>? isChecked = null)
    {
        var terms = ParseQuery(query);
        var activeGroup = string.IsNullOrEmpty(group) || string.Equals(group, PackageGroups.All, StringComparison.Ordinal)
            ? null
            : group;

        return package =>
        {
            if (!Matches(package, terms))
            {
                return false;
            }

            if (activeGroup is not null && !string.Equals(PackageGroups.Of(package), activeGroup, StringComparison.Ordinal))
            {
                return false;
            }

            if (onlySelected && isChecked is not null && !isChecked(package))
            {
                return false;
            }

            return true;
        };
    }

    /// <summary>
    /// 当前视图下「可见且可勾选」的包：批量操作（全选/反选/Ctrl+A）的作用域。
    ///
    /// 语义是「对我正在看的这批生效」：搜了 chrome 再按 Ctrl+A，用户期望勾上搜出来的
    /// 那些，而不是屏幕外的几百个。
    /// </summary>
    /// <param name="packages">全量包列表。</param>
    /// <param name="query">搜索词。</param>
    /// <param name="group">分组胶囊。</param>
    /// <param name="onlySelected">仅看已选。</param>
    /// <param name="isChecked">勾选查询。</param>
    /// <returns>可作用域内的包。</returns>
    public static IReadOnlyList<Package> ScopedPackages(IReadOnlyList<Package> packages, string? query,
                                                        string? group, bool onlySelected,
                                                        Func<Package, bool>? isChecked = null)
    {
        ArgumentNullException.ThrowIfNull(packages);

        var hasFilter = !string.IsNullOrWhiteSpace(query)
                        || (!string.IsNullOrEmpty(group) && !string.Equals(group, PackageGroups.All, StringComparison.Ordinal))
                        || onlySelected;

        if (!hasFilter)
        {
            // 无筛选：作用域就是全部可重装项（「winget 不支持」永远不可勾选）
            var all = new List<Package>(packages.Count);
            foreach (var package in packages)
            {
                if (package.Reinstallable)
                {
                    all.Add(package);
                }
            }

            return all;
        }

        var predicate = BuildPredicate(query, group, onlySelected, isChecked);
        var scoped = new List<Package>();
        foreach (var package in packages)
        {
            if (package.Reinstallable && predicate(package))
            {
                scoped.Add(package);
            }
        }

        return scoped;
    }
}
