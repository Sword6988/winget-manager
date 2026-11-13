using System;
using System.IO;
using System.Text.Json;
using WingetManager.Infra;
using WingetManager.Json;
using WingetManager.Models;

namespace WingetManager.Persistence;

/// <summary>
/// 目录快照落盘缓存（<c>%LOCALAPPDATA%\WingetManager\catalog-cache.json</c>）。
///
/// 解决的痛点：内存 TTL 快照（60s）应用重启即失效，用户每次启动都要等
/// <c>winget list</c> 的 10 秒+ 固定开销。有了这份缓存，冷启动先把上次扫描结果
/// 渲染出来（毫秒级），再在后台静默刷新——stale-while-revalidate。
///
/// 容错口径与 <see cref="RuntimeCache"/>/SelectionStore 一致：
/// 文件不存在/损坏/schema 不符/包列表为空一律返回 <see langword="null"/>（回退完整扫描），
/// 读写失败只记 WARN 日志，<b>绝不让启动或扫描失败</b>。
/// 读写全部走 <see cref="AppJsonContext"/> 源生成（AOT 硬约束）。
/// </summary>
public sealed class CatalogSnapshotStore
{
    private readonly string _path;
    private readonly LogService _log;

    /// <summary>
    /// 创建快照存储。
    /// </summary>
    /// <param name="path">缓存文件路径；null 用 <see cref="AppPaths.CatalogCacheFile"/>。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public CatalogSnapshotStore(string? path = null, LogService? log = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.CatalogCacheFile : path;
        _log = log ?? LogService.Shared;
    }

    /// <summary>缓存文件路径。</summary>
    public string FilePath => _path;

    /// <summary>
    /// 读取落盘快照：文件不存在/损坏/schema 不符/快照为空一律返回 <see langword="null"/>。
    /// </summary>
    /// <returns>有效快照；无效时 null（调用方回退完整扫描）。</returns>
    public CatalogSnapshot? Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return null;
            }

            var text = File.ReadAllText(_path, TextEncoding.Utf8NoBom);
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var payload = JsonSerializer.Deserialize(text, AppJsonContext.Default.CatalogCacheFile);
            if (payload is null
                || payload.Schema != CatalogCacheFile.CurrentSchema
                || payload.Snapshot is null
                || payload.Snapshot.Packages.Count == 0)
            {
                _log.Warn("目录快照缓存不可用（schema={0}），本次启动回退为完整扫描",
                    payload?.Schema.ToString() ?? "缺失");
                return null;
            }

            _log.Info(
                "已加载落盘快照：{0} 项（生成于 {1}），等待后台刷新",
                payload.Snapshot.Packages.Count,
                payload.Snapshot.GeneratedAt);
            return payload.Snapshot;
        }
        catch (JsonException ex)
        {
            _log.Warn("目录快照缓存损坏，回退为完整扫描：{0}", ex.Message);
            return null;
        }
        catch (IOException ex)
        {
            _log.Warn("目录快照缓存读取失败，回退为完整扫描：{0}", ex.Message);
            return null;
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("目录快照缓存读取失败，回退为完整扫描：{0}", ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 原子写盘（临时文件 + fsync + 替换）；失败只记日志——缓存丢了只是下次启动多等一次扫描。
    /// </summary>
    /// <param name="snapshot">扫描产出的快照。</param>
    public void Save(CatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        try
        {
            var payload = new CatalogCacheFile { Snapshot = snapshot };
            AtomicFile.WriteJson(_path, payload, AppJsonContext.Compat.CatalogCacheFile);
        }
        catch (IOException ex)
        {
            _log.Warn("目录快照缓存写盘失败（下次启动将完整扫描）：{0}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("目录快照缓存写盘失败（下次启动将完整扫描）：{0}", ex.Message);
        }
    }
}
