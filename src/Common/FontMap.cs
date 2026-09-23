using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Web.Script.Serialization;

namespace TomatoBiquga
{
    /// <summary>
    /// 番茄正文的反爬字体还原表：私用区码点（U+E000–U+F8FF）-> 真字。
    /// 映射表由字形比对离线生成（scripts/solve_font.py），随 exe 一起分发。
    /// </summary>
    public static class FontMap
    {
        private static readonly object Lock = new object();
        private static Dictionary<int, string> _map;

        public static int Count
        {
            get { EnsureLoaded(); return _map.Count; }
        }

        public static string SourcePath { get; private set; }

        private static void EnsureLoaded()
        {
            if (_map != null) return;
            lock (Lock)
            {
                if (_map != null) return;
                var map = new Dictionary<int, string>();

                // 1) 先试 exe 同目录 / fonts 子目录下的 font-map.json
                var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                foreach (var candidate in new[]
                {
                    Path.Combine(exeDir ?? ".", "font-map.json"),
                    Path.Combine(exeDir ?? ".", "fonts", "font-map.json"),
                })
                {
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            LoadInto(map, File.ReadAllText(candidate, Encoding.UTF8));
                            SourcePath = candidate;
                            break;
                        }
                        catch { /* 试下一个 */ }
                    }
                }

                // 2) 再试内嵌资源
                if (map.Count == 0)
                {
                    try
                    {
                        var asm = Assembly.GetExecutingAssembly();
                        foreach (var name in asm.GetManifestResourceNames())
                        {
                            if (!name.EndsWith("font-map.json", StringComparison.OrdinalIgnoreCase)) continue;
                            using (var s = asm.GetManifestResourceStream(name))
                            using (var r = new StreamReader(s, Encoding.UTF8))
                            {
                                LoadInto(map, r.ReadToEnd());
                                SourcePath = "内嵌资源:" + name;
                            }
                            break;
                        }
                    }
                    catch { /* 忽略 */ }
                }

                _map = map;
            }
        }

        private static void LoadInto(Dictionary<int, string> map, string json)
        {
            var ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            var data = ser.Deserialize<Dictionary<string, object>>(json);
            if (data == null) throw new Exception("映射表格式不对");
            foreach (var kv in data)
            {
                var code = Convert.ToInt32(kv.Key, 16);
                var entry = kv.Value as Dictionary<string, object>;
                if (entry == null) continue;
                object ch;
                if (!entry.TryGetValue("char", out ch) || ch == null) continue;
                var s = Convert.ToString(ch);
                if (s.Length == 0) continue;
                map[code] = s;
            }
            if (map.Count == 0) throw new Exception("映射表里没有条目");
        }

        /// <summary>把正文里的私用区字符换成真字</summary>
        public static string Decode(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            EnsureLoaded();
            if (_map.Count == 0) return text;   // 没有映射表就原样返回

            bool needed = false;
            foreach (var c in text)
            {
                if (c >= '\uE000' && c <= '\uF8FF') { needed = true; break; }
            }
            if (!needed) return text;

            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                string rep;
                if (c >= '\uE000' && c <= '\uF8FF' && _map.TryGetValue(c, out rep)) sb.Append(rep);
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
