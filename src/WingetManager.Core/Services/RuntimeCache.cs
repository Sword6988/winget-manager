using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using WingetManager.Infra;
using WingetManager.Json;
using WingetManager.Models;

namespace WingetManager.Services;

/// <summary>
/// 运行库判定的磁盘缓存（对应 <c>metadata.RuntimeCache</c>）。
///
/// 结构：<c>{"schema":1,"entries":{"&lt;id 小写&gt;":{"dep":bool,"checkedAt":ISO}}}</c>
/// 语义：<c>dep=true</c> → 元数据确认是运行库（只补漏，会覆盖关键词初判）；
/// <c>dep=false</c> → 已核查过、描述/标记里没有运行库信号，仅用于避免下一轮重复
/// <c>winget show</c>，<b>不是对「它是软件」的权威否定</b>。命中任一值即不再发起 show；
/// 查询失败不写缓存。文件损坏/结构不符一律回退空缓存（重建只是多花几分钟）。
/// </summary>
public sealed class RuntimeCache
{
    /// <summary>缓存 schema 版本（与 Python 版 SCHEMA_VERSION 一致，两版本可互换）。</summary>
    public const int SchemaVersion = MetadataCacheFile.CurrentSchema;

    private readonly object _gate = new();
    private readonly string _path;
    private readonly LogService _log;
    private Dictionary<string, MetadataCacheEntry> _entries;

    /// <summary>
    /// 打开（或新建）缓存。
    /// </summary>
    /// <param name="path">metadata-cache.json 路径；null 用 <see cref="AppPaths.MetadataCacheFile"/>。</param>
    /// <param name="log">日志服务；null 用共享实例。</param>
    public RuntimeCache(string? path = null, LogService? log = null)
    {
        _path = string.IsNullOrWhiteSpace(path) ? AppPaths.MetadataCacheFile : path;
        _log = log ?? LogService.Shared;
        _entries = Load();
    }

    /// <summary>缓存文件路径。</summary>
    public string FilePath => _path;

    /// <summary>缓存条目数。</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _entries.Count;
            }
        }
    }

    /// <summary>
    /// 取某个包的判定结果：True/False 为已核查，null 为未命中（要发起 show）。
    /// </summary>
    /// <param name="packageId">包 ID（大小写不敏感）。</param>
    /// <returns>缓存值或 null。</returns>
    public bool? Get(string packageId)
    {
        if (string.IsNullOrEmpty(packageId))
        {
            return null;
        }

        lock (_gate)
        {
            return _entries.TryGetValue(packageId.ToLowerInvariant(), out var entry) ? entry.Dep : null;
        }
    }

    /// <summary>
    /// 写入并<b>立即落盘</b>：富化任务随时可能被取消，不能攒着不写。
    /// </summary>
    /// <param name="packageId">包 ID。</param>
    /// <param name="isDependency">判定结果。</param>
    public void Put(string packageId, bool isDependency)
    {
        if (string.IsNullOrEmpty(packageId))
        {
            return;
        }

        var entry = new MetadataCacheEntry
        {
            Dep = isDependency,
            CheckedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
        };

        lock (_gate)
        {
            _entries[packageId.ToLowerInvariant()] = entry;
            Save();
        }
    }

    /// <summary>当前全部条目的快照（诊断/测试用）。</summary>
    /// <returns>键为小写包 ID。</returns>
    public IReadOnlyDictionary<string, MetadataCacheEntry> Snapshot()
    {
        lock (_gate)
        {
            return new Dictionary<string, MetadataCacheEntry>(_entries, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// 读盘：文件不存在 → 空；解析失败/结构不符/schema 不匹配 → 空（等价 Python 版容错）。
    ///
    /// 这里用手写 <see cref="JsonDocument"/> 而不是源生成的强类型反序列化，是为了复刻
    /// Python 的「宽进」行为：单条 entry 坏掉只跳过那一条，不连坐整份缓存。
    /// 写盘仍然强制走源生成（<see cref="AppJsonContext"/>）。
    /// </summary>
    private Dictionary<string, MetadataCacheEntry> Load()
    {
        var entries = new Dictionary<string, MetadataCacheEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return entries;
            }

            var text = File.ReadAllText(_path, TextEncoding.Utf8NoBom);
            if (string.IsNullOrWhiteSpace(text))
            {
                return entries;
            }

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            if (!root.TryGetProperty("schema", out var schemaElement)
                || schemaElement.ValueKind != JsonValueKind.Number
                || !schemaElement.TryGetInt32(out var schema)
                || schema != SchemaVersion)
            {
                _log.Warn("元数据缓存 schema 不符（期望 {0}），回退空缓存", SchemaVersion);
                return entries;
            }

            if (!root.TryGetProperty("entries", out var entriesElement)
                || entriesElement.ValueKind != JsonValueKind.Object)
            {
                return entries;
            }

            foreach (var property in entriesElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (!property.Value.TryGetProperty("dep", out var depElement)
                    || depElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                var checkedAt = string.Empty;
                if (property.Value.TryGetProperty("checkedAt", out var checkedElement)
                    && checkedElement.ValueKind == JsonValueKind.String)
                {
                    checkedAt = checkedElement.GetString() ?? string.Empty;
                }

                entries[property.Name.ToLowerInvariant()] = new MetadataCacheEntry
                {
                    Dep = depElement.GetBoolean(),
                    CheckedAt = checkedAt,
                };
            }
        }
        catch (JsonException ex)
        {
            _log.Warn("元数据缓存损坏，回退空缓存：{0}", ex.Message);
            entries.Clear();
        }
        catch (IOException ex)
        {
            _log.Warn("元数据缓存读取失败，回退空缓存：{0}", ex.Message);
            entries.Clear();
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("元数据缓存读取失败，回退空缓存：{0}", ex.Message);
            entries.Clear();
        }

        return entries;
    }

    /// <summary>落盘（原子写）；失败只记日志——缓存丢了只是下次多查一遍。</summary>
    private void Save()
    {
        try
        {
            var payload = new MetadataCacheFile
            {
                Schema = SchemaVersion,
                Entries = new Dictionary<string, MetadataCacheEntry>(_entries, StringComparer.Ordinal),
            };
            AtomicFile.WriteJson(_path, payload, AppJsonContext.Compat.MetadataCacheFile);
        }
        catch (IOException ex)
        {
            _log.Warn("元数据缓存写盘失败（下次扫描会重查）：{0}", ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Warn("元数据缓存写盘失败（下次扫描会重查）：{0}", ex.Message);
        }
    }
}
