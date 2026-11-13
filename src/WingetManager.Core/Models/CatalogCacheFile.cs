using System.Text.Json.Serialization;

namespace WingetManager.Models;

/// <summary>
/// 目录快照落盘缓存 DTO：<c>%LOCALAPPDATA%\WingetManager\catalog-cache.json</c>。
///
/// 用途：应用冷启动「秒开」——上次扫描完成的快照写入这份文件，下次启动先解析并直接
/// 渲染（毫秒级），再在后台静默刷新（stale-while-revalidate）。文件形态：
/// <c>{"schema":1,"snapshot":{…CatalogSnapshot…}}</c>，schema 版本不符一律当损坏回退
/// 为完整扫描，绝不让启动失败。这是 .NET 版新增的文件，Python 版没有对应物。
/// </summary>
public sealed class CatalogCacheFile
{
    /// <summary>当前 schema 版本；未来字段结构变化时递增，旧文件自动作废。</summary>
    public const int CurrentSchema = 1;

    /// <summary>schema 版本；不等于 <see cref="CurrentSchema"/> 时整份缓存作废。</summary>
    [JsonPropertyName("schema")]
    public int Schema { get; set; } = CurrentSchema;

    /// <summary>缓存的快照内容（<see langword="null"/> / 空包列表同样视为无效）。</summary>
    [JsonPropertyName("snapshot")]
    public CatalogSnapshot? Snapshot { get; set; }
}
