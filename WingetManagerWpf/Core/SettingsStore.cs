using System;
using System.IO;
using System.Text;
using WingetManagerWpf.Core.Json;
using WingetManagerWpf.Models;

namespace WingetManagerWpf.Core
{
    /// <summary>
    /// settings.json 读写。格式（§8.8）：
    /// <code>
    /// {"window":{"x":..,"y":..,"w":..,"h":..,"maximized":false},
    ///  "log":{"height":150,"expanded":true},
    ///  "view":{"category":"软件"},
    ///  "export":{"path":"D:\\apps.json"}}
    /// </code>
    /// 任何读取失败（文件缺失、半截 JSON、字段类型不对）都静默回退默认值：
    /// 丢一次窗口位置无伤大雅，为它弹错框才是打扰。
    /// </summary>
    public sealed class SettingsStore
    {
        private readonly string _path;
        private readonly AppLogger _log;

        public SettingsStore(string path, AppLogger log)
        {
            _path = path;
            _log = log;
        }

        /// <summary>加载设置；缺失/损坏一律返回默认值。</summary>
        public AppSettings Load()
        {
            var defaults = new AppSettings();
            if (string.IsNullOrEmpty(_path) || !File.Exists(_path))
            {
                return defaults;
            }

            string text;
            try
            {
                // File.ReadAllText 会自动吃掉 UTF-8 BOM（老文件可能带）
                text = File.ReadAllText(_path, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Warn("设置读取失败，改用默认值：" + ex.Message);
                return defaults;
            }

            JsonValue root;
            if (!JsonReader.TryParse(text, out root) || root == null || !root.IsObject)
            {
                Warn("设置文件格式不正确，改用默认值。");
                return defaults;
            }

            var settings = new AppSettings();
            JsonValue window = root.Get("window");
            if (window != null && window.IsObject)
            {
                int value;
                if (window.Get("x") != null && window.Get("x").TryGetInt(out value))
                {
                    settings.WindowX = value;
                }

                if (window.Get("y") != null && window.Get("y").TryGetInt(out value))
                {
                    settings.WindowY = value;
                }

                if (window.Get("w") != null && window.Get("w").TryGetInt(out value))
                {
                    settings.WindowWidth = Math.Max((int)Metrics.MinWidth, value);
                }

                if (window.Get("h") != null && window.Get("h").TryGetInt(out value))
                {
                    settings.WindowHeight = Math.Max((int)Metrics.MinHeight, value);
                }

                bool flag;
                if (window.Get("maximized") != null && window.Get("maximized").TryGetBool(out flag))
                {
                    settings.Maximized = flag;
                }
            }

            JsonValue log = root.Get("log");
            if (log != null && log.IsObject)
            {
                int value;
                if (log.Get("height") != null && log.Get("height").TryGetInt(out value))
                {
                    settings.LogHeight = Clamp(value, (int)Metrics.LogMin, (int)Metrics.LogMax);
                }

                bool flag;
                if (log.Get("expanded") != null && log.Get("expanded").TryGetBool(out flag))
                {
                    settings.LogExpanded = flag;
                }
            }

            JsonValue view = root.Get("view");
            if (view != null && view.IsObject)
            {
                string text2;
                if (view.Get("category") != null && view.Get("category").TryGetString(out text2))
                {
                    settings.Category = text2 ?? string.Empty;
                }
            }

            JsonValue export = root.Get("export");
            if (export != null && export.IsObject)
            {
                string text3;
                if (export.Get("path") != null && export.Get("path").TryGetString(out text3))
                {
                    settings.ExportPath = text3 ?? string.Empty;
                }
            }

            return settings;
        }

        /// <summary>保存设置（原子写；失败只记日志，绝不影响关闭流程）。</summary>
        public void Save(AppSettings settings)
        {
            if (settings == null || string.IsNullOrEmpty(_path))
            {
                return;
            }

            JsonValue root = JsonValue.NewObject();

            JsonValue window = JsonValue.NewObject();
            window.Set("x", JsonValue.From(settings.WindowX));
            window.Set("y", JsonValue.From(settings.WindowY));
            window.Set("w", JsonValue.From(settings.WindowWidth));
            window.Set("h", JsonValue.From(settings.WindowHeight));
            window.Set("maximized", JsonValue.From(settings.Maximized));
            root.Set("window", window);

            JsonValue log = JsonValue.NewObject();
            log.Set("height", JsonValue.From(Clamp(settings.LogHeight, (int)Metrics.LogMin, (int)Metrics.LogMax)));
            log.Set("expanded", JsonValue.From(settings.LogExpanded));
            root.Set("log", log);

            JsonValue view = JsonValue.NewObject();
            view.Set("category", JsonValue.From(settings.Category ?? string.Empty));
            root.Set("view", view);

            JsonValue export = JsonValue.NewObject();
            export.Set("path", JsonValue.From(settings.ExportPath ?? string.Empty));
            root.Set("export", export);

            try
            {
                AtomicFile.WriteJsonAtomic(_path, root);
            }
            catch (Exception ex)
            {
                Warn("设置保存失败（下次启动会回到默认窗口状态）：" + ex.Message);
            }
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
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
