using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using WingetManager.Infra;

namespace WingetManager.Models;

/// <summary>
/// 运行库判定缓存文件 DTO：<c>{"schema":1,"entries":{"&lt;id 小写&gt;":{"dep":bool,"checkedAt":ISO}}}</c>。
/// schema=1 与 Python 版完全一致，两版本可互换读写（ARCHITECTURE §5 UNCLEAR#6）。
/// </summary>
public sealed class MetadataCacheFile
{
    /// <summary>当前 schema 版本；与 Python 版 SCHEMA_VERSION 相同。</summary>
    public const int CurrentSchema = 1;

    /// <summary>schema 版本；不等于 <see cref="CurrentSchema"/> 时整份缓存作废。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>条目表：key 为小写包 ID。</summary>
    [JsonPropertyName("entries")]
    public Dictionary<string, MetadataCacheEntry> Entries { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>缓存里的单条记录。</summary>
public sealed class MetadataCacheEntry
{
    /// <summary>true = 元数据确认是运行库（补漏，会覆盖关键词初判）；false = 已核查、无信号（不构成否定）。</summary>
    [JsonPropertyName("dep")]
    public bool Dep { get; set; }

    /// <summary>核查时间（ISO8601，带本地时区偏移，秒精度）。</summary>
    [JsonPropertyName("checkedAt")]
    public string CheckedAt { get; set; } = string.Empty;
}

/// <summary>
/// 勾选持久化 <c>selection.json</c> 的 <b>JSON 形态与读写</b>。
///
/// 文件形态是一个<b>裸的</b> <c>{"&lt;小写包 ID&gt;": bool}</c> 映射（Python 版写的就是
/// <c>dict</c>），因此不能包一层对象，否则两版本不互通。读入按「宽进」处理：
/// 老版本文件里可能出现 <c>"false"</c> / <c>0</c> 之类，<c>bool("false")</c> 在 Python 里是
/// True，所以这里也按白名单字符串判定，绝不直接把字符串转 bool。
/// </summary>
public static class SelectionFile
{
    /// <summary>数据目录下的文件名。</summary>
    public const string FileName = "selection.json";

    /// <summary>
    /// 读取勾选文件：文件不存在/损坏/不是对象，一律回退成空集合（对应 Python 的容错）。
    /// </summary>
    /// <param name="path">selection.json 路径。</param>
    /// <returns>包 ID（小写）→ 是否勾选。</returns>
    public static Dictionary<string, bool> Load(string path)
    {
        var result = new Dictionary<string, bool>(StringComparer.Ordinal);
        try
        {
            if (!System.IO.File.Exists(path))
            {
                return result;
            }

            var text = System.IO.File.ReadAllText(path, System.Text.Encoding.UTF8);
            if (string.IsNullOrWhiteSpace(text))
            {
                return result;
            }

            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var key = property.Name.ToLowerInvariant();
                result[key] = CoerceBool(property.Value);
            }
        }
        catch (JsonException)
        {
            // 半截 JSON：回退空状态而不是抛异常（勾选丢了远好过崩一次）
            result.Clear();
        }
        catch (System.IO.IOException)
        {
            result.Clear();
        }
        catch (UnauthorizedAccessException)
        {
            result.Clear();
        }

        return result;
    }

    /// <summary>原子保存勾选（UTF-8、两空格缩进、结尾换行，与 Python 版一致）。</summary>
    /// <param name="path">selection.json 路径。</param>
    /// <param name="checkedIds">包 ID（小写）→ 是否勾选。</param>
    public static void Save(string path, IReadOnlyDictionary<string, bool> checkedIds)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(checkedIds);

        var payload = new Dictionary<string, bool>(checkedIds.Count, StringComparer.Ordinal);
        foreach (var pair in checkedIds)
        {
            if (pair.Value)
            {
                // 只落 True：未勾选的键没有信息量，省略可让文件小一大截
                payload[pair.Key] = true;
            }
        }

        AtomicFile.WriteJson(path, payload, Json.AppJsonContext.Compat.DictionaryStringBoolean);
    }

    /// <summary>把 JSON 值宽松地折成 bool（"1"/"true"/"yes"/"on" 为真，数字非 0 为真）。</summary>
    private static bool CoerceBool(JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return false;
            case JsonValueKind.Number:
                return value.TryGetDouble(out var number) && Math.Abs(number) > double.Epsilon;
            case JsonValueKind.String:
                var text = (value.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                return text is "1" or "true" or "yes" or "on";
            default:
                return false;
        }
    }

    /// <summary>把时间折成 Python <c>datetime.now().astimezone().isoformat(timespec="seconds")</c> 的形态。</summary>
    public static string FormatTimestamp(DateTimeOffset value)
    {
        return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);
    }
}
