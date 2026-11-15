using System;
using System.Collections.Generic;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.ViewModels
{
    /// <summary>一条搜索条件。<see cref="Field"/> ∈ {"id","name","any"}。</summary>
    public struct SearchToken
    {
        public SearchToken(string field, string value)
        {
            Field = string.IsNullOrEmpty(field) ? "any" : field;
            Value = value ?? string.Empty;
        }

        /// <summary>"id" / "name" / "any"。</summary>
        public string Field;

        /// <summary>已转小写的检索词。</summary>
        public string Value;
    }

    /// <summary>
    /// 搜索语法与匹配规则（逐行移植 <c>_ref/qt_app.py</c> 的 <c>_search_tokens</c> /
    /// <c>_pkg_matches</c>，行为必须一模一样）：
    /// <list type="bullet">
    /// <item>按空白切词，多个词之间是 AND；</item>
    /// <item><c>id:xxx</c> 只匹配包 ID，<c>name:xxx</c> 只匹配名称，其余匹配「名称 + 包 ID」；</item>
    /// <item>前缀后面必须还有内容（<c>"id:"</c> 单独出现不算限定，退回普通词）；</item>
    /// <item>全部小写匹配（大小写不敏感）。</item>
    /// </list>
    /// 单独成文件是因为 ViewModel（批量勾选要算「当前可见」）与 ListPresenter（渲染）
    /// 都要用这套规则，两边各写一遍必然走样。
    /// </summary>
    public static class SearchQuery
    {
        /// <summary>把搜索框文本切成条件列表；空文本返回空列表（= 全部命中）。</summary>
        public static IList<SearchToken> Tokenize(string text)
        {
            var tokens = new List<SearchToken>();
            string raw = (text ?? string.Empty).Trim().ToLowerInvariant();
            if (raw.Length == 0)
            {
                return tokens;
            }

            string[] parts = raw.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length; i++)
            {
                string word = parts[i];
                if (word.Length == 0)
                {
                    continue;
                }

                if (word.StartsWith("id:", StringComparison.Ordinal) && word.Length > 3)
                {
                    tokens.Add(new SearchToken("id", word.Substring(3)));
                }
                else if (word.StartsWith("name:", StringComparison.Ordinal) && word.Length > 5)
                {
                    tokens.Add(new SearchToken("name", word.Substring(5)));
                }
                else
                {
                    tokens.Add(new SearchToken("any", word));
                }
            }

            return tokens;
        }

        /// <summary>包是否命中全部条件（空条件列表恒为 true）。</summary>
        public static bool Matches(Package package, IList<SearchToken> tokens)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return true;
            }

            if (package == null)
            {
                return false;
            }

            string name = (package.Name ?? string.Empty).ToLowerInvariant();
            string id = (package.PackageIdentifier ?? string.Empty).ToLowerInvariant();

            for (int i = 0; i < tokens.Count; i++)
            {
                SearchToken token = tokens[i];
                if (token.Value.Length == 0)
                {
                    continue;
                }

                if (string.Equals(token.Field, "id", StringComparison.Ordinal))
                {
                    if (id.IndexOf(token.Value, StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }
                }
                else if (string.Equals(token.Field, "name", StringComparison.Ordinal))
                {
                    if (name.IndexOf(token.Value, StringComparison.Ordinal) < 0)
                    {
                        return false;
                    }
                }
                else if ((name + " " + id).IndexOf(token.Value, StringComparison.Ordinal) < 0)
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// 行内高亮用的关键词：去掉 <c>id:</c>/<c>name:</c> 前缀后的检索词（去重、保序）。
        /// 前缀本身不参与高亮，否则每个词都会多出一段莫名的高亮底色。
        /// </summary>
        public static string[] HighlightKeywords(IList<SearchToken> tokens)
        {
            if (tokens == null || tokens.Count == 0)
            {
                return new string[0];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<string>();
            for (int i = 0; i < tokens.Count; i++)
            {
                string value = tokens[i].Value;
                if (value.Length == 0 || !seen.Add(value))
                {
                    continue;
                }

                result.Add(value);
            }

            return result.ToArray();
        }
    }
}
