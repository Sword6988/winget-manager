using System;
using System.Collections.Generic;
using System.IO;
using WingetManager.Infra;
using WingetManager.Models;

namespace WingetManager.Persistence;

/// <summary>
/// 勾选状态持久化（<c>%LOCALAPPDATA%\WingetManager\selection.json</c>）。
///
/// 键是<b>小写包 ID</b>；未勾选的项不落盘（与 Python 版等价：Python 里存的是
/// dict，未勾选的键值为 False，语义上等于不存在的键）。
/// 原子写、损坏回退为空——勾选丢了远好过崩一次。
/// </summary>
public sealed class SelectionStore
{
    private readonly string _path;
    private readonly LogService _log;

    /// <summary>
    /// 创建勾选存储。
    /// </summary>
    /// <param name="path">文件路径；null 用 <see cref="AppPaths.SelectionFile"/>。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public SelectionStore(string? path = null, LogService? log = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.SelectionFile : path;
        _log = log ?? LogService.Shared;
    }

    /// <summary>文件路径。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 读取勾选集合（小写包 ID）。文件不存在/损坏一律返回空集合。
    /// </summary>
    /// <returns>已勾选的包 ID 集合。</returns>
    public HashSet<string> Load()
    {
        var map = SelectionFile.Load(_path);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pair in map)
        {
            if (pair.Value)
            {
                ids.Add(pair.Key);
            }
        }

        return ids;
    }

    /// <summary>
    /// 保存勾选：原子写 + 失败只记日志（勾选丢了不该影响关闭窗口）。
    /// </summary>
    /// <param name="ids">已勾选的包 ID 集合（小写）。</param>
    public void Save(IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);

        var map = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
            {
                map[id.ToLowerInvariant()] = true;
            }
        }

        try
        {
            SelectionFile.Save(_path, map);
        }
        catch (IOException ex)
        {
            _log.Warn("勾选状态保存失败: {0}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("勾选状态保存失败: {0}", ex.Message);
        }
    }
}
