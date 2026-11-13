using System;
using System.Collections.Generic;
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
            if (payload is null || payload.Schema != CatalogCacheFile.CurrentSchema)
            {
                // 口径说明：schema 字段<b>缺失</b>时按当前版本接受（DTO 属性初始化器兜底），
                // 属于有意的宽松向前兼容；只要显式写出的值 ≠ 当前版本，整份作废。
                _log.Warn("目录快照缓存 schema 不符（期望 {0}，实际 {1}），本次启动回退为完整扫描",
                    CatalogCacheFile.CurrentSchema,
                    payload?.Schema.ToString() ?? "缺失");
                return null;
            }

            var snapshot = payload.Snapshot;
            if (snapshot is null || snapshot.Packages is null || string.IsNullOrEmpty(snapshot.GeneratedAt))
            {
                // 显式 null 会覆盖 DTO 的属性初始化器（如 "packages": null），不能只判 Count
                _log.Warn("目录快照缓存结构不完整（snapshot/packages/generatedAt 缺失或为 null），本次启动回退为完整扫描");
                return null;
            }

            // 逐条清洗：null 元素、关键字段为 null 的条目直接剔除（手改文件 / 未来写入方
            // 变更都可能产生）。调用方（MainViewModel 渲染、HashSet 比较器）会解引用
            // PackageIdentifier / Name，这里保证交付出去的快照字段齐备且非 null。
            var packages = new List<Package>(snapshot.Packages.Count);
            foreach (var package in snapshot.Packages)
            {
                if (package is null || package.PackageIdentifier is null || package.Name is null)
                {
                    continue;
                }

                packages.Add(package);
            }

            if (packages.Count == 0)
            {
                _log.Warn("目录快照缓存没有有效包条目，本次启动回退为完整扫描");
                return null;
            }

            snapshot.Packages = packages;

            _log.Info(
                "已加载落盘快照：{0} 项（生成于 {1}），等待后台刷新",
                packages.Count,
                snapshot.GeneratedAt);
            return snapshot;
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
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
        {
            // 兜底：缓存读取链路上任何意外异常都只降级为「无缓存」，绝不打断启动
            _log.Warn("目录快照缓存读取出现意外异常，回退为完整扫描：{0}", ex);
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
