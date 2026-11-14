using System;
using System.Collections.Generic;
using System.Linq;

namespace WingetManager.Services;

/// <summary>
/// 勾选真相集合（<c>packageId</c> 集合）。
///
/// 存在的理由：<b>勾选状态只能有一个来源</b>。界面上的复选框、组头计数、
/// 「已选 N 个」、导出清单全部从这里派生，行对象（<c>PackageRow</c>）只是它的
/// 可丢弃投影。
///
/// 历史教训（v3.0.0 的 bug）：批量勾选走的是「先抑制行事件 → 改一堆行 → 落盘」，
/// 而真相集合只在行事件回调里更新，结果<b>批量勾选从未写进集合</b>——界面看着勾上了，
/// 一切换分组重建列表就全没了（重建时按集合判断初始状态）。
/// 因此这里把「改集合」做成唯一入口：<see cref="SetMany"/> / <see cref="Toggle"/>
/// 既返回变更数、也保证集合已更新，调用方没有「忘了同步」的机会。
///
/// 大小写不敏感（winget 的包 ID 大小写不保证稳定，落盘时统一小写）。
/// </summary>
public sealed class SelectionBook
{
    private readonly HashSet<string> _ids;

    /// <summary>创建空集合。</summary>
    public SelectionBook()
    {
        _ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>从已有 ID 创建（自动忽略空白项）。</summary>
    /// <param name="initial">初始 ID 集合（可为 null）。</param>
    public SelectionBook(IEnumerable<string?>? initial)
        : this()
    {
        ReplaceAll(initial);
    }

    /// <summary>当前已勾选数量。</summary>
    public int Count => _ids.Count;

    /// <summary>是否为空。</summary>
    public bool IsEmpty => _ids.Count == 0;

    /// <summary>是否已勾选。</summary>
    /// <param name="packageId">包 ID（null/空白一律视为未勾选）。</param>
    /// <returns>是否在集合内。</returns>
    public bool Contains(string? packageId)
    {
        return !string.IsNullOrWhiteSpace(packageId) && _ids.Contains(packageId);
    }

    /// <summary>勾选单个。</summary>
    /// <param name="packageId">包 ID。</param>
    /// <returns>集合是否真的变了（已存在 → false）。</returns>
    public bool Add(string? packageId)
    {
        return !string.IsNullOrWhiteSpace(packageId) && _ids.Add(packageId);
    }

    /// <summary>取消单个勾选。</summary>
    /// <param name="packageId">包 ID。</param>
    /// <returns>集合是否真的变了（本就不在 → false）。</returns>
    public bool Remove(string? packageId)
    {
        return !string.IsNullOrWhiteSpace(packageId) && _ids.Remove(packageId);
    }

    /// <summary>
    /// 批量设置勾选（全选 / 全不选 / 组头按钮的唯一入口）。
    /// </summary>
    /// <param name="packageIds">目标包 ID 序列。</param>
    /// <param name="selected">true = 全勾，false = 全不勾。</param>
    /// <returns>实际发生变化的条数。</returns>
    public int SetMany(IEnumerable<string?> packageIds, bool selected)
    {
        ArgumentNullException.ThrowIfNull(packageIds);

        var changed = 0;
        foreach (var id in packageIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            if (selected ? _ids.Add(id) : _ids.Remove(id))
            {
                changed++;
            }
        }

        return changed;
    }

    /// <summary>
    /// 批量取反（「反选」的唯一入口）。重复 ID 只取反一次。
    /// </summary>
    /// <param name="packageIds">目标包 ID 序列。</param>
    /// <returns>实际发生变化的条数。</returns>
    public int Toggle(IEnumerable<string?> packageIds)
    {
        ArgumentNullException.ThrowIfNull(packageIds);

        var changed = 0;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in packageIds)
        {
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id))
            {
                continue;
            }

            if (!_ids.Remove(id))
            {
                _ids.Add(id);
            }

            changed++;
        }

        return changed;
    }

    /// <summary>
    /// 按「仍然存在且可重装」的 ID 剪枝（重扫后调用，卸掉的软件不该留在导出清单里）。
    /// </summary>
    /// <param name="keepIds">允许保留的 ID 集合。</param>
    /// <returns>被剪掉的条数。</returns>
    public int PruneTo(IEnumerable<string?> keepIds)
    {
        ArgumentNullException.ThrowIfNull(keepIds);

        var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in keepIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                keep.Add(id);
            }
        }

        return _ids.RemoveWhere(id => !keep.Contains(id));
    }

    /// <summary>清空全部勾选。</summary>
    public void Clear()
    {
        _ids.Clear();
    }

    /// <summary>用给定集合整体替换（从 selection.json 恢复时用）。</summary>
    /// <param name="packageIds">新集合（可为 null）。</param>
    public void ReplaceAll(IEnumerable<string?>? packageIds)
    {
        _ids.Clear();
        if (packageIds is null)
        {
            return;
        }

        foreach (var id in packageIds)
        {
            if (!string.IsNullOrWhiteSpace(id))
            {
                _ids.Add(id);
            }
        }
    }

    /// <summary>导出当前集合（落盘用）。</summary>
    /// <returns>ID 数组。</returns>
    public string[] ToArray()
    {
        return _ids.ToArray();
    }

    /// <summary>只读视图（诊断 / 日志用）。</summary>
    public IReadOnlyCollection<string> Items => _ids;
}
