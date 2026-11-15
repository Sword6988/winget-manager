using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using WingetManagerWpf.Core;
using WingetManagerWpf.Core.Json;

namespace WingetManagerWpf.Services
{
    /// <summary>
    /// <c>selection.json</c> 读写（与原版同路径同格式，可双向互通）：
    /// <c>{"包id小写": true, ...}</c>，UTF-8 无 BOM、缩进 2、尾随换行。
    /// <para>
    /// 读入必须**严格布尔**（易错点 7）：只有 JSON <c>true</c> 或字符串
    /// ∈ {1,true,yes,on} 才算勾选。老文件里可能存成 <c>"false"</c> / <c>0</c>，
    /// 用 <c>bool.Parse</c> / <c>Convert.ToBoolean</c> 会把 <c>"false"</c> 判成 true，
    /// 出现「升级后所有软件莫名被勾上」的灾难。
    /// </para>
    /// </summary>
    public sealed class SelectionStore
    {
        private readonly string _path;
        private readonly AppLogger _log;

        public SelectionStore(string path, AppLogger log)
        {
            _path = path;
            _log = log;
        }

        /// <summary>读取勾选状态；文件缺失/损坏返回空字典（并记日志）。</summary>
        public IDictionary<string, bool> Load()
        {
            var result = new Dictionary<string, bool>(StringComparer.Ordinal);

            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                return result;
            }

            string text;
            try
            {
                text = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Warn("勾选状态读取失败：" + ex.Message);
                return result;
            }

            JsonValue root;
            if (!JsonReader.TryParse(text, out root) || root == null || !root.IsObject)
            {
                Warn("勾选状态文件格式不正确，已回退为空。");
                return result;
            }

            foreach (var member in root.Members)
            {
                string key = (member.Key ?? string.Empty).ToLowerInvariant();
                if (key.Length == 0)
                {
                    continue;
                }

                result[key] = IsTruthy(member.Value);
            }

            return result;
        }

        /// <summary>保存勾选状态（原子写；失败只记日志，不影响关闭流程）。</summary>
        public void Save(IDictionary<string, bool> selection)
        {
            if (string.IsNullOrEmpty(_path))
            {
                return;
            }

            JsonValue root = JsonValue.NewObject();
            if (selection != null)
            {
                foreach (var pair in selection)
                {
                    if (string.IsNullOrEmpty(pair.Key))
                    {
                        continue;
                    }

                    // 值一律写 JSON bool：字符串 "true" 会让下一版读到 false
                    root.Set(pair.Key.ToLowerInvariant(), JsonValue.From(pair.Value));
                }
            }

            try
            {
                AtomicFile.WriteJsonAtomic(_path, root);
            }
            catch (Exception ex)
            {
                Warn("勾选状态保存失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 严格布尔判定：JSON bool 直接用；字符串只认 {1,true,yes,on}；其余（含数字 0/1、
        /// 字符串 "false"/"0"）一律 false。
        /// </summary>
        private static bool IsTruthy(JsonValue value)
        {
            if (value == null)
            {
                return false;
            }

            bool boolValue;
            if (value.TryGetBool(out boolValue))
            {
                return boolValue;
            }

            string text;
            if (value.TryGetString(out text) && text != null)
            {
                string lowered = text.Trim().ToLowerInvariant();
                return lowered == "1" || lowered == "true" || lowered == "yes" || lowered == "on";
            }

            return false;
        }

        private void Warn(string message)
        {
            if (_log != null)
            {
                _log.Warn(message);
            }
        }
    }
}
