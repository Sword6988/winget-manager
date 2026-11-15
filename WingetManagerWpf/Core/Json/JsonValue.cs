using System;
using System.Collections.Generic;

namespace WingetManagerWpf.Core.Json
{
    /// <summary>JSON 值类型。</summary>
    public enum JsonKind
    {
        Null = 0,
        Bool = 1,
        Number = 2,
        String = 3,
        Array = 4,
        Object = 5
    }

    /// <summary>
    /// 轻量 JSON 树（零 NuGet）。为什么不用现成的序列化器：
    /// <list type="bullet">
    /// <item><c>DataContractJsonSerializer</c> 无法控制键序与缩进；</item>
    /// <item><c>JavaScriptSerializer</c> 对斜杠/日期的转义有怪癖，且会改写键序；</item>
    /// <item><c>System.Text.Json</c> 在 .NET Framework 4.8 上要靠 NuGet。</item>
    /// </list>
    /// 本类的对象成员**保持插入顺序**（selection.json / metadata-cache.json 依赖这一点
    /// 才能与原版逐字节可读），并且严格区分 Bool 与 String —— 「dep 必须是 JSON bool」
    /// 的判定就靠 <see cref="TryGetBool"/>，字符串 "true" 不会被误判为真。
    /// </summary>
    public sealed class JsonValue
    {
        private static readonly JsonValue NullInstance = new JsonValue(JsonKind.Null);

        private readonly JsonKind _kind;
        private readonly bool _boolValue;
        private readonly double _numberValue;
        private readonly long _longValue;
        private readonly bool _isIntegral;
        private readonly string _stringValue;
        private readonly List<JsonValue> _items;
        private readonly List<KeyValuePair<string, JsonValue>> _members;

        private JsonValue(JsonKind kind)
        {
            _kind = kind;
        }

        private JsonValue(bool value)
        {
            _kind = JsonKind.Bool;
            _boolValue = value;
        }

        private JsonValue(long value)
        {
            _kind = JsonKind.Number;
            _longValue = value;
            _numberValue = value;
            _isIntegral = true;
        }

        private JsonValue(double value)
        {
            _kind = JsonKind.Number;
            _numberValue = value;
            _isIntegral = false;
        }

        private JsonValue(string value)
        {
            _kind = JsonKind.String;
            _stringValue = value ?? string.Empty;
        }

        private JsonValue(List<JsonValue> items)
        {
            _kind = JsonKind.Array;
            _items = items ?? new List<JsonValue>();
        }

        private JsonValue(List<KeyValuePair<string, JsonValue>> members)
        {
            _kind = JsonKind.Object;
            _members = members ?? new List<KeyValuePair<string, JsonValue>>();
        }

        /// <summary>JSON null 单例。</summary>
        public static JsonValue Null
        {
            get { return NullInstance; }
        }

        public static JsonValue From(bool value)
        {
            return new JsonValue(value);
        }

        public static JsonValue From(long value)
        {
            return new JsonValue(value);
        }

        public static JsonValue From(int value)
        {
            return new JsonValue((long)value);
        }

        public static JsonValue From(double value)
        {
            return new JsonValue(value);
        }

        /// <summary>字符串值；<paramref name="value"/> 为 null 时返回 <see cref="Null"/>。</summary>
        public static JsonValue From(string value)
        {
            if (value == null)
            {
                return NullInstance;
            }

            return new JsonValue(value);
        }

        public static JsonValue NewArray()
        {
            return new JsonValue(new List<JsonValue>());
        }

        public static JsonValue NewArray(IEnumerable<JsonValue> items)
        {
            var list = new List<JsonValue>();
            if (items != null)
            {
                foreach (JsonValue item in items)
                {
                    list.Add(item ?? NullInstance);
                }
            }

            return new JsonValue(list);
        }

        public static JsonValue NewObject()
        {
            return new JsonValue(new List<KeyValuePair<string, JsonValue>>());
        }

        public JsonKind Kind
        {
            get { return _kind; }
        }

        public bool IsNull
        {
            get { return _kind == JsonKind.Null; }
        }

        public bool IsBool
        {
            get { return _kind == JsonKind.Bool; }
        }

        public bool IsNumber
        {
            get { return _kind == JsonKind.Number; }
        }

        public bool IsString
        {
            get { return _kind == JsonKind.String; }
        }

        public bool IsArray
        {
            get { return _kind == JsonKind.Array; }
        }

        public bool IsObject
        {
            get { return _kind == JsonKind.Object; }
        }

        /// <summary>是不是「整数值」（Writer 据此决定是否输出小数部分）。</summary>
        public bool IsIntegral
        {
            get { return _kind == JsonKind.Number && _isIntegral; }
        }

        /// <summary>对象成员数 / 数组元素数；其他类型返回 0。</summary>
        public int Count
        {
            get
            {
                if (_kind == JsonKind.Object)
                {
                    return _members.Count;
                }

                if (_kind == JsonKind.Array)
                {
                    return _items.Count;
                }

                return 0;
            }
        }

        /// <summary>对象成员（保持插入顺序）。</summary>
        public IEnumerable<KeyValuePair<string, JsonValue>> Members
        {
            get
            {
                if (_kind != JsonKind.Object)
                {
                    return new KeyValuePair<string, JsonValue>[0];
                }

                return _members;
            }
        }

        /// <summary>数组元素。</summary>
        public IEnumerable<JsonValue> Items
        {
            get
            {
                if (_kind != JsonKind.Array)
                {
                    return new JsonValue[0];
                }

                return _items;
            }
        }

        /// <summary>字符串值（非字符串返回空串，绝不返回 null）。</summary>
        public string AsString()
        {
            return _kind == JsonKind.String ? _stringValue : string.Empty;
        }

        /// <summary>数值（非数值返回 0）。</summary>
        public double AsNumber()
        {
            return _kind == JsonKind.Number ? _numberValue : 0d;
        }

        /// <summary>整数值（非整数返回 0）。</summary>
        public long AsLong()
        {
            return IsIntegral ? _longValue : 0L;
        }

        /// <summary>
        /// 严格布尔：只有真正的 JSON bool 才返回 true 并输出值。
        /// 字符串 "true" / 数字 1 一律不算 —— 原版用 <c>isinstance(x, bool)</c>，语义必须一致。
        /// </summary>
        public bool TryGetBool(out bool value)
        {
            if (_kind == JsonKind.Bool)
            {
                value = _boolValue;
                return true;
            }

            value = false;
            return false;
        }

        public bool TryGetInt(out int value)
        {
            if (IsIntegral && _longValue >= int.MinValue && _longValue <= int.MaxValue)
            {
                value = (int)_longValue;
                return true;
            }

            value = 0;
            return false;
        }

        public bool TryGetString(out string value)
        {
            if (_kind == JsonKind.String)
            {
                value = _stringValue;
                return true;
            }

            value = null;
            return false;
        }

        public bool ContainsKey(string key)
        {
            return IndexOfMember(key) >= 0;
        }

        /// <summary>取对象成员；不存在或类型不对返回 null（调用方需自行判空）。</summary>
        public JsonValue Get(string key)
        {
            int index = IndexOfMember(key);
            return index >= 0 ? _members[index].Value : null;
        }

        /// <summary>取对象成员；不存在返回 <see cref="Null"/>（链式取值不用判空）。</summary>
        public JsonValue GetOrNull(string key)
        {
            JsonValue found = Get(key);
            return found ?? NullInstance;
        }

        /// <summary>
        /// 设置对象成员：已存在则**原位替换**（保证键序稳定），否则追加到末尾。
        /// </summary>
        public void Set(string key, JsonValue value)
        {
            if (_kind != JsonKind.Object)
            {
                throw new InvalidOperationException("只有 JSON 对象才能设置成员。");
            }

            if (key == null)
            {
                throw new ArgumentNullException("key");
            }

            JsonValue safe = value ?? NullInstance;
            int index = IndexOfMember(key);
            if (index >= 0)
            {
                _members[index] = new KeyValuePair<string, JsonValue>(_members[index].Key, safe);
                return;
            }

            _members.Add(new KeyValuePair<string, JsonValue>(key, safe));
        }

        /// <summary>向数组追加元素。</summary>
        public void Add(JsonValue item)
        {
            if (_kind != JsonKind.Array)
            {
                throw new InvalidOperationException("只有 JSON 数组才能追加元素。");
            }

            _items.Add(item ?? NullInstance);
        }

        private int IndexOfMember(string key)
        {
            if (_kind != JsonKind.Object || key == null)
            {
                return -1;
            }

            for (int i = 0; i < _members.Count; i++)
            {
                if (string.Equals(_members[i].Key, key, StringComparison.Ordinal))
                {
                    return i;
                }
            }

            return -1;
        }

        public override string ToString()
        {
            return JsonWriter.Serialize(this);
        }
    }
}
