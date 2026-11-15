using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using WingetManagerWpf.Core;
using WingetManagerWpf.Core.Json;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// 运行库判定的磁盘缓存（<c>metadata-cache.json</c>）。结构：
    /// <code>{"schema":1,"entries":{"&lt;id 小写&gt;":{"dep":true,"checkedAt":"2024-05-01T12:00:00+08:00"}}}</code>
    /// 语义（与原版一致，非常容易理解错，见 §10-8）：
    /// <list type="bullet">
    /// <item><c>dep=true</c>：元数据确认是运行库 → 用于**补漏**，会覆盖关键词初判（True）；</item>
    /// <item><c>dep=false</c>：已核查过、描述/标记里没有运行库信号 → **不是否定**，
    /// 只用于避免下一轮重复 <c>winget show</c>；关键词已判运行库的包不会被移出该组；</item>
    /// <item>未命中：沿用关键词初判，等后台富化补齐；</item>
    /// <item><c>dep</c> 必须是 **JSON bool**，字符串 "true" / 数字 1 一律视为未命中；</item>
    /// <item>文件损坏 / schema 不符 → 回退空缓存（重建只是多花几分钟，report 比崩掉好）。</item>
    /// </list>
    /// <see cref="Put"/> 立即落盘：富化线程随时可能被取消，不能攒着不写。
    /// <para>
    /// 过期（TTL）：条目按 <c>checkedAt</c> 计算年龄，超过 <c>ttlDays</c> 天就当未命中返回，
    /// 下一轮扫描会重新 <c>winget show</c> 并把新结论写回。为什么不在 <c>Get</c> 里顺手删除条目：
    /// 过期只是「本次不采信」，删掉会让缓存文件在扫描期间频繁重写（<see cref="Put"/> 才是唯一的写入者），
    /// 而且用户把 <c>WPM_META_TTL_DAYS</c> 调回大值时期望历史结论仍可用。
    /// </para>
    /// </summary>
    public sealed class RuntimeCache
    {
        /// <summary>当前缓存结构版本；不匹配即视为整体失效。</summary>
        public const int SchemaVersion = 1;

        /// <summary>默认判定有效期（天），直接复用配置层常量，避免两处 90 各自漂移。</summary>
        public const int DefaultTtlDays = AppConfig.DefaultMetadataTtlDays;

        private readonly object _sync = new object();
        private readonly string _path;
        private readonly AppLogger _log;
        private readonly int _ttlDays;
        private readonly Dictionary<string, CacheEntry> _map = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        private readonly List<CacheEntry> _ordered = new List<CacheEntry>();

        /// <summary>构造并立刻从磁盘载入；<paramref name="ttlDays"/> 可省略（默认 90 天）。</summary>
        /// <param name="ttlDays">
        /// 判定有效期（天）；<c>&lt;= 0</c> 表示永不过期。默认 90，声明为可选参数是为了
        /// 让既有两参调用点（含自测）无需改动即可继续编译。
        /// </param>
        public RuntimeCache(string path, AppLogger log, int ttlDays = DefaultTtlDays)
        {
            _path = path;
            _log = log;
            _ttlDays = ttlDays;
            Load();
        }

        /// <summary>已缓存的条目数。</summary>
        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _ordered.Count;
                }
            }
        }

        /// <summary>
        /// 取缓存值：键小写；<c>dep</c> 非 JSON bool 视为未命中（返回 null）；
        /// 条目年龄超过 TTL 也视为未命中（返回 null），但**不删除**条目 —— 见类注释。
        /// </summary>
        public bool? Get(string packageId)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return null;
            }

            string key = packageId.ToLowerInvariant();
            lock (_sync)
            {
                CacheEntry entry;
                if (!_map.TryGetValue(key, out entry))
                {
                    return null;
                }

                // ttlDays <= 0：显式关闭过期（用户明确要求「结论永久有效」）
                if (_ttlDays <= 0)
                {
                    return entry.Dep;
                }

                DateTimeOffset checkedAt;
                if (!TryParseCheckedAt(entry.CheckedAt, out checkedAt))
                {
                    // 旧版/手改文件里 checkedAt 可能是空串或乱码：解析不了就当过期重查一次，
                    // 比永久采信一条来历不明的时间戳安全。
                    return null;
                }

                double ageDays = (DateTimeOffset.Now - checkedAt).TotalDays;
                if (ageDays < 0)
                {
                    // 时钟回拨 / 文件来自「未来」：判新鲜，否则每次扫描都白查一遍
                    return entry.Dep;
                }

                return ageDays > _ttlDays ? (bool?)null : entry.Dep;
            }
        }

        /// <summary>写入并立即落盘（原子写；失败只记日志）。</summary>
        public void Put(string packageId, bool dep)
        {
            if (string.IsNullOrEmpty(packageId))
            {
                return;
            }

            string key = packageId.ToLowerInvariant();
            string checkedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

            lock (_sync)
            {
                CacheEntry entry;
                if (_map.TryGetValue(key, out entry))
                {
                    entry.Dep = dep;
                    entry.CheckedAt = checkedAt;
                }
                else
                {
                    entry = new CacheEntry(key, dep, checkedAt);
                    _map[key] = entry;
                    _ordered.Add(entry);
                }

                Save();
            }
        }

        /// <summary>
        /// 解析 <c>checkedAt</c>。写入格式为 <c>yyyy-MM-dd'T'HH:mm:sszzz</c>（如
        /// <c>2024-05-01T12:00:00+08:00</c>），但历史文件可能来自原版 Python 或用户手改，
        /// 因此用宽松的 <see cref="DateTimeOffset.TryParse(string, out DateTimeOffset)"/> 重载族
        /// 而不是 TryParseExact：解析成功即可用来算年龄，解析失败一律当过期。
        /// </summary>
        private static bool TryParseCheckedAt(string text, out DateTimeOffset value)
        {
            value = default(DateTimeOffset);
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
        }

        private void Load()
        {
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                return;
            }

            string text;
            try
            {
                text = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Warn("元数据缓存读取失败，回退空缓存：" + ex.Message);
                return;
            }

            JsonValue root;
            if (!JsonReader.TryParse(text, out root) || root == null || !root.IsObject)
            {
                Warn("元数据缓存损坏，回退空缓存。");
                return;
            }

            JsonValue schema = root.Get("schema");
            int schemaValue;
            if (schema == null || !schema.TryGetInt(out schemaValue) || schemaValue != SchemaVersion)
            {
                Warn("元数据缓存 schema 不是 " + SchemaVersion.ToString(CultureInfo.InvariantCulture)
                     + "，回退空缓存。");
                return;
            }

            JsonValue entries = root.Get("entries");
            if (entries == null || !entries.IsObject)
            {
                return;
            }

            lock (_sync)
            {
                foreach (var member in entries.Members)
                {
                    if (member.Value == null || !member.Value.IsObject)
                    {
                        continue;
                    }

                    bool dep;
                    if (!member.Value.GetOrNull("dep").TryGetBool(out dep))
                    {
                        // 严格 bool：字符串 "true" / 数字 1 都不算命中
                        continue;
                    }

                    string checkedAt = member.Value.GetOrNull("checkedAt").AsString();
                    string key = (member.Key ?? string.Empty).ToLowerInvariant();
                    if (key.Length == 0 || _map.ContainsKey(key))
                    {
                        continue;
                    }

                    var entry = new CacheEntry(key, dep, checkedAt);
                    _map[key] = entry;
                    _ordered.Add(entry);
                }
            }
        }

        private void Save()
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            try
            {
                JsonValue root = JsonValue.NewObject();
                root.Set("schema", JsonValue.From(SchemaVersion));

                JsonValue entries = JsonValue.NewObject();
                for (int i = 0; i < _ordered.Count; i++)
                {
                    CacheEntry entry = _ordered[i];
                    JsonValue item = JsonValue.NewObject();
                    // dep 必须是 JSON bool（写字符串会导致下次读取视为未命中，白查一遍）
                    item.Set("dep", JsonValue.From(entry.Dep));
                    item.Set("checkedAt", JsonValue.From(entry.CheckedAt ?? string.Empty));
                    entries.Set(entry.Id, item);
                }

                root.Set("entries", entries);
                AtomicFile.WriteJsonAtomic(_path, root);
            }
            catch (Exception ex)
            {
                Warn("元数据缓存写盘失败（下次扫描会重查）：" + ex.Message);
            }
        }

        private void Warn(string message)
        {
            if (_log != null)
            {
                _log.Warn(message);
            }
        }

        private sealed class CacheEntry
        {
            public CacheEntry(string id, bool dep, string checkedAt)
            {
                Id = id;
                Dep = dep;
                CheckedAt = checkedAt ?? string.Empty;
            }

            public string Id { get; private set; }

            public bool Dep { get; set; }

            public string CheckedAt { get; set; }
        }
    }
}
