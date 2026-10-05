using System;
using System.Collections.Generic;
using System.Text;

namespace TomatoBiquga
{
    /// <summary>
    /// AI 裁决错字：把**双源比对的差异点**交给大模型判断哪个写法对。
    ///
    /// 为什么只做"裁决"而不是"通读校对"：
    ///
    ///   1. **补的正是双源比对的死结**。`TypoFinder` 能算出"哪里两边不一样"，
    ///      但解不了两种情况：两个源都错成同一个字（它连差异都看不到），
    ///      以及两边写法不同但哪个对不知道（只能把两个都摆给用户猜）。
    ///      AI 正好能对后者给出判断。
    ///   2. **成本与隐私都压到最小**。候选位置已经算好了，所以只发
    ///      **差异点前后各十几个字**，不是整章、更不是整本。
    ///      一本百万字的书差异几百处，也就几万 token。
    ///      而且发出去的是零散片段，不是全文 —— 这对"不把内容交给第三方"
    ///      这条项目姿态来说是伤害最小的做法。
    ///   3. 让模型通读全文找错字，成本和幻觉风险都不可控，
    ///      而且它会把**正确的**字也"改"掉。
    ///
    /// 设计上的克制：
    ///   · **默认关闭**，必须用户显式开启（settings.ini 的 ai-enabled）。
    ///   · **只发高可疑的差异**：先用规则筛（`IsHighSuspicion`），
    ///     明显的站点广告差异、长度差过大的，根本不送 AI。
    ///   · **先估成本再发**：界面会告诉用户"预计多少处、约多少 token"。
    ///   · **AI 不可用绝不影响主流程**：所有异常都被吞掉并降级成"未裁决"，
    ///     错字检测报告照常生成。
    /// </summary>
    public static class AiAdjudicator
    {
        /// <summary>裁决结果</summary>
        public enum Verdict
        {
            /// <summary>还没送 AI（默认）</summary>
            None = 0,
            /// <summary>AI 认为主源对</summary>
            PrimaryRight,
            /// <summary>AI 认为对照源对（主源这个字是错的）</summary>
            OtherRight,
            /// <summary>AI 认为两边都不对，并给出了建议写法</summary>
            BothWrong,
            /// <summary>AI 判断不了 / 上下文不足以判断</summary>
            Unsure,
        }

        /// <summary>一条差异的裁决</summary>
        public class VerdictResult
        {
            public Verdict Verdict = Verdict.None;
            /// <summary>AI 给出的正确写法（BothWrong 时有值；其他情况为空）</summary>
            public string Suggestion = "";
            /// <summary>一句话理由（给报告用，可能为空）</summary>
            public string Reason = "";
            /// <summary>失败原因（调用出错时填，Verdict 仍是 None）</summary>
            public string Error = "";

            public string VerdictText()
            {
                switch (Verdict)
                {
                    case Verdict.PrimaryRight: return "主源正确";
                    case Verdict.OtherRight: return "主源可能错（正确写法见报告里的对照源列）";
                    case Verdict.BothWrong: return "两边都不对" + (Suggestion.Length > 0 ? "，建议：" + Suggestion : "");
                    case Verdict.Unsure: return "判断不了";
                    default: return "未裁决";
                }
            }
        }

        /// <summary>
        /// 一次 AI 配置。空 ApiKey + 本地地址 = 用本地 Ollama（完全离线、不花钱）。
        /// </summary>
        public class Config
        {
            /// <summary>
            /// 后端类型：ollama（本地）/ openai（云端，兼容 OpenAI 格式的那一类：
            /// DeepSeek、Kimi、通义、硅基流动、OpenAI 本身…）
            /// </summary>
            public string Backend = "ollama";

            /// <summary>服务地址。Ollama 默认 http://127.0.0.1:11434；云端填到 /v1 为止</summary>
            public string BaseUrl = "http://127.0.0.1:11434";

            /// <summary>模型名，例如 qwen2.5:7b（Ollama）或 deepseek-chat（云端）</summary>
            public string Model = "";

            /// <summary>云端才需要。**本地 Ollama 留空**</summary>
            public string ApiKey = "";

            /// <summary>单次请求超时（秒）。本地小模型在慢机器上可能要好几十秒</summary>
            public int TimeoutSeconds = 120;

            /// <summary>一次请求里塞几条差异（批量能显著减少请求数）</summary>
            public int BatchSize = 10;

            public bool IsCloud
            {
                get { return string.Equals(Backend, "openai", StringComparison.OrdinalIgnoreCase); }
            }

            /// <summary>配置是否齐全（不全就不该发请求）</summary>
            public string Validate()
            {
                if (string.IsNullOrEmpty(BaseUrl)) return "没填服务地址。";
                if (string.IsNullOrEmpty(Model)) return "没填模型名。";
                if (IsCloud && string.IsNullOrEmpty(ApiKey))
                    return "云端接口需要 API Key。如果不想用云端，把后端改成「本地 Ollama」。";
                if (BaseUrl.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase) == false &&
                    BaseUrl.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase) == false &&
                    !IsCloud)
                    return "后端选了「本地 Ollama」，但地址不是本机 —— 请确认是不是想用云端（那样要把后端改成 openai）。";
                return null;
            }

            public string Describe()
            {
                return string.Format("{0} / {1} / {2}", Backend, Model,
                    IsCloud ? "云端（会发送差异片段到服务商）" : "本地（不联网到外部）");
            }
        }

        // ============================================================
        //  可疑度筛选：只把"值得问 AI"的送出去
        // ============================================================

        /// <summary>
        /// 这条差异值不值得问 AI。
        ///
        /// 为什么要筛：站点广告差异、长度差过大的站点差异，占了报告里的大头，
        /// 但它们**根本不是错字**，送 AI 既浪费钱又会干扰判断。
        /// 只留下"长度接近、且差异是汉字"的那些 —— 它们才真是"可能有一个错字"。
        /// </summary>
        public static bool IsHighSuspicion(TypoFinder.Diff d)
        {
            if (d == null) return false;

            // 长度差过大的是"站点差异"（TypoFinder 已经标过），不送
            if (d.Context != null && d.Context.IndexOf("疑似站点差异", StringComparison.Ordinal) >= 0)
                return false;

            // 两边都空 → 没有可判断的内容
            if (string.IsNullOrEmpty(d.Primary) && string.IsNullOrEmpty(d.Other)) return false;

            // 只裁决"用字不同"这一种。
            // 多字/漏字我会让用户自己看 —— 那多半是站点排版差异，
            // 而且 AI 对"该不该补一个词"的判断很不稳（容易顺手改文风）。
            if (d.Kind != TypoFinder.DiffKind.Replace) return false;

            // 差异长度要接近：一边 1 字一边 8 字，那不是错字，是排版
            int lp = d.Primary == null ? 0 : d.Primary.Length;
            int lo = d.Other == null ? 0 : d.Other.Length;
            if (lp == 0 || lo == 0) return false;
            if (Math.Abs(lp - lo) > 1) return false;
            if (lp > 4 || lo > 4) return false;      // 短差异才是"字"的问题

            // 至少有一边含汉字（纯标点/数字差异不值得问）
            if (!HasHan(d.Primary) && !HasHan(d.Other)) return false;

            return true;
        }

        private static bool HasHan(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var c in s) if (BookStats.IsHan(c)) return true;
            return false;
        }

        /// <summary>按可疑度筛出要送 AI 的差异下标</summary>
        public static List<int> PickCandidates(List<TypoFinder.Diff> diffs)
        {
            var list = new List<int>();
            if (diffs == null) return list;
            for (int i = 0; i < diffs.Count; i++)
                if (IsHighSuspicion(diffs[i])) list.Add(i);
            return list;
        }

        /// <summary>估算要发出去多少字符 / 大约多少 token（给"先估成本再发"用）</summary>
        public static void Estimate(List<TypoFinder.Diff> diffs, List<int> candidates,
            out int chars, out int approxTokens)
        {
            chars = 0;
            if (candidates != null)
            {
                foreach (var i in candidates)
                {
                    if (i < 0 || i >= diffs.Count) continue;
                    var d = diffs[i];
                    chars += Len(d.Context) + Len(d.Primary) + Len(d.Other) + 40;  // 40 ≈ 每条的结构开销
                }
            }
            // 中文大致 1 字 ≈ 1 token（保守估；实际多数模型略低于此）
            approxTokens = chars;
        }

        private static int Len(string s) { return s == null ? 0 : s.Length; }

        // ============================================================
        //  提示词
        // ============================================================

        /// <summary>
        /// 系统提示词。刻意写得很死：**只准在给出的两个写法里选，不准改写原文**。
        /// 不这样约束的话，模型很容易"顺手把句子改通顺"——
        /// 那对"校错字"来说是灾难（它会改掉作者原本的用词）。
        /// </summary>
        internal const string SystemPrompt =
            "你是一个中文网络小说的校对助手。用户会给你若干条【差异点】，" +
            "每条包含：上下文（原文片段，差异处用【】标出）、写法A、写法B。\n" +
            "你的任务：判断这一处到底哪个写法正确，或者两个都不对。\n" +
            "严格规则：\n" +
            "1. 只判断差异处的那几个字，**绝对不要改写上下文的其它部分**。\n" +
            "2. 只允许输出这几种结论：A（写法A正确）、B（写法B正确）、C（两个都不对）、U（无法判断）。\n" +
            "3. 选 C 时另外给出你建议的正确写法（只给那几个字，不要给整句）。\n" +
            "4. 拿不准就选 U，**不要猜**。\n" +
            "输出格式：每条一行，形如 `序号|结论|理由(不超过15字)|建议写法(仅结论为C时填)`。\n" +
            "不要输出任何其它内容，不要解释，不要加标题。";

        /// <summary>把一批差异拼成用户消息。只放差异点上下文，不放整章。</summary>
        internal static string BuildUserMessage(List<TypoFinder.Diff> diffs, List<int> batch)
        {
            var sb = new StringBuilder();
            sb.Append("以下是 ").Append(batch.Count).Append(" 条差异点：\n\n");
            for (int k = 0; k < batch.Count; k++)
            {
                var d = diffs[batch[k]];
                sb.Append(k + 1).Append(". ");
                if (!string.IsNullOrEmpty(d.Chapter)) sb.Append("（章节：").Append(d.Chapter).Append("）");
                sb.Append('\n');
                sb.Append("   上下文：").Append(MarkDiff(d)).Append('\n');
                sb.Append("   写法A：").Append(d.Primary).Append('\n');
                sb.Append("   写法B：").Append(d.Other).Append('\n');
                sb.Append('\n');
            }
            sb.Append("请按格式逐条回答。");
            return sb.ToString();
        }

        /// <summary>
        /// 把差异处在上下文里用【】标出来，方便模型定位。
        /// 找不到就退化成"上下文 + 两个写法"，不硬凑。
        /// </summary>
        internal static string MarkDiff(TypoFinder.Diff d)
        {
            var ctx = d.Context ?? "";
            if (ctx.Length == 0) return "（无上下文）";
            var p = d.Primary ?? "";
            if (p.Length == 0) return ctx;

            int at = ctx.IndexOf(p, StringComparison.Ordinal);
            if (at < 0) return ctx + "    ← 差异处应为：" + p;
            return ctx.Substring(0, at) + "【" + p + "】" + ctx.Substring(at + p.Length);
        }

        // ============================================================
        //  响应解析
        // ============================================================

        /// <summary>
        /// 解析模型输出。**必须容错**：小模型经常多写解释、少写字段、换全角竖线。
        /// 解析不出来的条目按"未裁决"处理，绝不因为格式问题把整批丢掉。
        /// </summary>
        internal static List<VerdictResult> ParseReply(string reply, int expectedCount)
        {
            var list = new List<VerdictResult>();
            for (int i = 0; i < expectedCount; i++) list.Add(new VerdictResult());
            if (string.IsNullOrEmpty(reply)) return list;

            var lines = reply.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            foreach (var raw in lines)
            {
                var line = (raw ?? "").Trim();
                if (line.Length == 0) continue;
                // 去掉 markdown 列表符号和代码块残留
                line = line.TrimStart('-', '*', ' ', '\t');
                if (line.StartsWith("```", StringComparison.Ordinal)) continue;

                // 分隔符：半角/全角竖线、逗号、制表符都容忍
                var parts = SplitFields(line);
                if (parts.Count < 2) continue;

                int idx;
                if (!int.TryParse(parts[0].Trim(), out idx)) continue;
                idx -= 1;
                if (idx < 0 || idx >= expectedCount) continue;

                var code = parts[1].Trim().ToUpperInvariant();
                // 只取第一个字符判定，容忍 "A（写法A正确）" 这种啰嗦写法
                char c = code.Length > 0 ? code[0] : 'U';
                var r = list[idx];
                switch (c)
                {
                    case 'A': r.Verdict = Verdict.PrimaryRight; break;
                    case 'B': r.Verdict = Verdict.OtherRight; break;
                    case 'C': r.Verdict = Verdict.BothWrong; break;
                    default: r.Verdict = Verdict.Unsure; break;
                }
                if (parts.Count > 2) r.Reason = parts[2].Trim();
                if (parts.Count > 3 && r.Verdict == Verdict.BothWrong)
                {
                    var sug = parts[3].Trim();
                    // 建议写法要短：模型有时会给整句，那就丢掉（宁可没有建议）
                    if (sug.Length <= 8) r.Suggestion = sug;
                }
            }
            return list;
        }

        private static List<string> SplitFields(string line)
        {
            var parts = new List<string>();
            var sb = new StringBuilder();
            foreach (var ch in line)
            {
                if (ch == '|' || ch == '｜' || ch == '\t' || ch == '，' || ch == ',')
                {
                    parts.Add(sb.ToString());
                    sb.Length = 0;
                }
                else sb.Append(ch);
            }
            parts.Add(sb.ToString());
            return parts;
        }

        // ============================================================
        //  请求体 / 响应体的构造与提取（纯函数，便于离线单测）
        // ============================================================

        /// <summary>构造请求体 JSON（手写，不引第三方库 —— 和项目其它部分一致）</summary>
        internal static string BuildRequestBody(Config cfg, string userMessage)
        {
            var sb = new StringBuilder();
            if (cfg != null && cfg.IsCloud)
            {
                // OpenAI 兼容：/v1/chat/completions
                sb.Append("{\"model\":").Append(J(cfg.Model));
                sb.Append(",\"temperature\":0");
                sb.Append(",\"messages\":[");
                sb.Append("{\"role\":\"system\",\"content\":").Append(J(SystemPrompt)).Append("},");
                sb.Append("{\"role\":\"user\",\"content\":").Append(J(userMessage)).Append("}");
                sb.Append("]}");
            }
            else
            {
                // Ollama：/api/chat
                sb.Append("{\"model\":").Append(J(cfg == null ? "" : cfg.Model));
                sb.Append(",\"stream\":false");
                sb.Append(",\"options\":{\"temperature\":0}");
                sb.Append(",\"messages\":[");
                sb.Append("{\"role\":\"system\",\"content\":").Append(J(SystemPrompt)).Append("},");
                sb.Append("{\"role\":\"user\",\"content\":").Append(J(userMessage)).Append("}");
                sb.Append("]}");
            }
            return sb.ToString();
        }

        /// <summary>请求 URL</summary>
        internal static string BuildUrl(Config cfg)
        {
            var b = (cfg.BaseUrl ?? "").TrimEnd('/');
            if (cfg.IsCloud)
            {
                // 已经给了完整路径 → 原样用
                if (b.IndexOf("/chat/completions", StringComparison.OrdinalIgnoreCase) >= 0) return b;
                // 已经带版本段（/v1、/v4…）→ 只补最后一段。
                // ★ 智谱是 /api/paas/v4，**不是** /v1。第一版没考虑"带版本段但不是 v1"，
                //   拼出了 .../v4/v1/chat/completions —— 用户点一下必然连不上。
                //   是"每个预设都必须能拼出正确 URL"这条断言把它抓出来的。
                if (System.Text.RegularExpressions.Regex.IsMatch(b, @"/v\d+$"))
                    return b + "/chat/completions";
                return b + "/v1/chat/completions";
            }
            if (b.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)) return b;
            return b + "/api/chat";
        }

        /// <summary>
        /// 从响应 JSON 里取模型输出的文本。
        /// 云端和本地的字段名不同：
        ///   OpenAI 兼容 → choices[0].message.content
        ///   Ollama      → message.content
        /// 两种都试，取到就用 —— 有些中转服务两套字段都给。
        /// </summary>
        internal static string ExtractContent(string json, Config cfg)
        {
            if (string.IsNullOrEmpty(json)) return null;

            // 先试 Ollama 的顶层 message.content
            var v = ExtractNested(json, "\"message\"", "content");
            if (!string.IsNullOrEmpty(v)) return v;

            // 再试 OpenAI 的 choices[0].message.content
            int ci = json.IndexOf("\"choices\"", StringComparison.Ordinal);
            if (ci >= 0)
            {
                v = ExtractNested(json.Substring(ci), "\"message\"", "content");
                if (!string.IsNullOrEmpty(v)) return v;
                // 有的实现只给 text
                v = ExtractNested(json.Substring(ci), "\"delta\"", "content");
                if (!string.IsNullOrEmpty(v)) return v;
            }

            // 报错信息要能透出来（模型名写错、key 无效时很有用）
            var err = ExtractNested(json, "\"error\"", "message");
            if (!string.IsNullOrEmpty(err)) return null;
            return null;
        }

        /// <summary>从响应里挖出错误信息（给用户看）</summary>
        internal static string ExtractError(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var e = ExtractNested(json, "\"error\"", "message");
            if (!string.IsNullOrEmpty(e)) return e;
            var m = ExtractSimple(json, "message");
            return m ?? "";
        }

        /// <summary>找 {"obj": { ... "key": "value" ... }} 里的 key 字符串值</summary>
        private static string ExtractNested(string json, string objKey, string key)
        {
            int oi = json.IndexOf(objKey, StringComparison.Ordinal);
            if (oi < 0) return null;
            int brace = json.IndexOf('{', oi);
            if (brace < 0) return null;
            // 从 brace 起找配对的 }
            int depth = 0, end = -1;
            for (int i = brace; i < json.Length; i++)
            {
                if (json[i] == '{') depth++;
                else if (json[i] == '}')
                {
                    depth--;
                    if (depth == 0) { end = i; break; }
                }
                else if (json[i] == '"')
                {
                    i = SkipString(json, i) - 1;   // 跳过字符串，避免里面的 {} 干扰
                }
            }
            if (end < 0) return null;
            return ExtractSimple(json.Substring(brace, end - brace + 1), key);
        }

        private static int SkipString(string s, int start)
        {
            int i = start + 1;
            while (i < s.Length)
            {
                if (s[i] == '\\') { i += 2; continue; }
                if (s[i] == '"') return i + 1;
                i++;
            }
            return s.Length;
        }

        /// <summary>取一个字段的字符串值（不处理嵌套）</summary>
        private static string ExtractSimple(string json, string key)
        {
            var pat = "\"" + key + "\"";
            int at = json.IndexOf(pat, StringComparison.Ordinal);
            while (at >= 0)
            {
                int i = at + pat.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i < json.Length && json[i] == ':')
                {
                    i++;
                    while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                    if (i < json.Length && json[i] == '"')
                        return UnescapeJson(json, i + 1);
                }
                at = json.IndexOf(pat, at + pat.Length, StringComparison.Ordinal);
            }
            return null;
        }

        private static string UnescapeJson(string s, int start)
        {
            var sb = new StringBuilder();
            for (int i = start; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '"') break;
                if (c != '\\') { sb.Append(c); continue; }
                i++;
                if (i >= s.Length) break;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'u':
                        if (i + 4 < s.Length)
                        {
                            int code;
                            if (int.TryParse(s.Substring(i + 1, 4),
                                    System.Globalization.NumberStyles.HexNumber,
                                    System.Globalization.CultureInfo.InvariantCulture, out code))
                            {
                                sb.Append((char)code);
                                i += 4;
                                break;
                            }
                        }
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            return sb.ToString();
        }

        private static string J(string s)
        {
            if (s == null) s = "";
            var sb = new StringBuilder(s.Length + 16);
            sb.Append('"');
            foreach (var c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// 云端预设：用户不懂"baseurl 填什么、模型叫什么"，所以给几个一键选项。
        ///
        /// 为什么只有这几家：它们都提供**标准 OpenAI 兼容接口**，
        /// 所以同一套代码就能用，不必为每家写适配器。
        /// 智谱排第一个是因为 GLM-4-Flash 有免费额度 ——
        /// 想"用 AI 但不想花钱"的话这是正当路径（而不是去绕网页对话界面）。
        ///
        /// ⚠️ 各家的免费额度与模型名会变，请以官方控制台为准；
        ///    这里只是把 baseurl 和常见模型名预填好，省得用户去翻文档。
        /// </summary>
        public class CloudPreset
        {
            public string Name = "";
            public string BaseUrl = "";
            public string Model = "";
            /// <summary>给用户看的一句话说明（尤其标出"有免费额度"）</summary>
            public string Note = "";
            /// <summary>去哪拿 Key</summary>
            public string ConsoleUrl = "";
        }

        public static List<CloudPreset> CloudPresets()
        {
            var list = new List<CloudPreset>();
            list.Add(new CloudPreset
            {
                Name = "智谱 GLM",
                BaseUrl = "https://open.bigmodel.cn/api/paas/v4",
                Model = "glm-4-flash",
                Note = "glm-4-flash 有免费额度，适合「想用 AI 又不想花钱」。额度以官方控制台为准。",
                ConsoleUrl = "https://open.bigmodel.cn/",
            });
            list.Add(new CloudPreset
            {
                Name = "DeepSeek",
                BaseUrl = "https://api.deepseek.com",
                Model = "deepseek-chat",
                Note = "中文强、便宜；裁决异体字这种细活我认为最合适。按量付费。",
                ConsoleUrl = "https://platform.deepseek.com/",
            });
            list.Add(new CloudPreset
            {
                Name = "硅基流动 SiliconFlow",
                BaseUrl = "https://api.siliconflow.cn",
                Model = "Qwen/Qwen2.5-7B-Instruct",
                Note = "聚合了很多开源模型，部分型号有免费档。",
                ConsoleUrl = "https://cloud.siliconflow.cn/",
            });
            list.Add(new CloudPreset
            {
                Name = "自定义（其他 OpenAI 兼容服务）",
                BaseUrl = "",
                Model = "",
                Note = "任何兼容 OpenAI /v1/chat/completions 的服务都能填：本机 vLLM、LM Studio、one-api 中转…",
                ConsoleUrl = "",
            });
            return list;
        }

        /// <summary>
        /// 列出本机 Ollama 已装的模型（GET /api/tags）。
        ///
        /// 为什么值得做：让用户手打模型名是最容易出错的一步
        /// （`qwen2.5:7b` 少个冒号、`Qwen/Qwen2.5-7B-Instruct` 大小写写错，都会连不上）。
        /// 探测出列表让他直接选，就避开了这类问题。
        ///
        /// 失败返回空列表 —— Ollama 没装/没启动是常态，不算错误。
        /// </summary>
        public static List<string> ListLocalModels(string baseUrl, int timeoutSeconds)
        {
            var list = new List<string>();
            try
            {
                var b = (baseUrl ?? "").TrimEnd('/');
                if (string.IsNullOrEmpty(b)) b = "http://127.0.0.1:11434";
                var json = Http.GetWithTimeout(b + "/api/tags", Math.Max(3, timeoutSeconds));
                if (string.IsNullOrEmpty(json)) return list;
                foreach (System.Text.RegularExpressions.Match m in
                    System.Text.RegularExpressions.Regex.Matches(json,
                        "\"name\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\""))
                {
                    var n = m.Groups[1].Value.Trim();
                    if (n.Length > 0 && !list.Contains(n)) list.Add(n);
                }
                list.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch { /* Ollama 没启动是常态，不当错误 */ }
            return list;
        }

        /// <summary>给 HTTP 层拼请求头（云端要 Authorization）</summary>
        internal static Dictionary<string, string> BuildHeaders(Config cfg)
        {
            var h = new Dictionary<string, string>();
            h["Content-Type"] = "application/json";
            if (cfg != null && cfg.IsCloud && !string.IsNullOrEmpty(cfg.ApiKey))
                h["Authorization"] = "Bearer " + cfg.ApiKey;
            return h;
        }

        // ============================================================
        //  会话：真正跑一批裁决
        // ============================================================

        /// <summary>一次裁决会话的统计（给日志和报告用）</summary>
        public class SessionStats
        {
            public int Candidates;          // 筛出来的高可疑差异数
            public int Requested;           // 实际发出的批次数
            public int Succeeded;           // 成功拿到结论的批次数
            public int Verdicts;            // 拿到结论的差异条数
            public int PrimaryRight;
            public int OtherRight;          // ★ 真正找到的错字（主源错）
            public int BothWrong;
            public int Unsure;
            public int Failed;              // 没能解析出结论的条数
            public long SentChars;          // 发出去多少字符
            public double ElapsedSeconds;
            public string LastError = "";

            public string Summary()
            {
                var sb = new StringBuilder();
                sb.Append(string.Format("AI 裁决 {0} 处高可疑差异：{1} 批请求（成功 {2}）",
                    Candidates, Requested, Succeeded));
                sb.Append(string.Format(
                    "\n  主源正确 {0} 处；主源疑似错字 {1} 处；两边都不对 {2} 处；判断不了 {3} 处",
                    PrimaryRight, OtherRight, BothWrong, Unsure));
                if (Failed > 0)
                    sb.Append(string.Format("\n  有 {0} 处没能从模型回答里解析出结论（已跳过）", Failed));
                sb.Append(string.Format("\n  共发出约 {0:N0} 字符（≈{0:N0} token），耗时 {1:F1} 秒",
                    SentChars, ElapsedSeconds));
                if (!string.IsNullOrEmpty(LastError))
                    sb.Append("\n  最后一次错误：" + LastError);
                return sb.ToString();
            }
        }

        /// <summary>
        /// 对一批差异跑 AI 裁决，结果**就地写回 verdicts**（下标与 diffs 对齐）。
        ///
        /// 设计约束：
        ///   · **绝不抛异常**。AI 挂了/超时/格式不对都只记进 stats.LastError，
        ///     主流程（错字检测报告）照常出 —— AI 是锦上添花，不能变成新的失败点。
        ///   · 分批：一次请求塞 BatchSize 条，显著减少请求数（也省钱）。
        ///   · 每批前检查取消，用户点取消能及时停。
        /// </summary>
        public static void Run(List<TypoFinder.Diff> diffs, VerdictResult[] verdicts, Config cfg,
            Action<string> log, Func<bool> isCanceled, SessionStats stats)
        {
            if (stats == null) stats = new SessionStats();
            if (diffs == null || verdicts == null || cfg == null) return;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var cand = PickCandidates(diffs);
                stats.Candidates = cand.Count;
                if (cand.Count == 0)
                {
                    if (log != null)
                        log("没有需要 AI 裁决的高可疑差异（其余差异属于站点排版差异，不值得送 AI）。");
                    return;
                }

                int chars, tokens;
                Estimate(diffs, cand, out chars, out tokens);
                if (log != null)
                    log(string.Format("准备把 {0} 处高可疑差异送 AI 裁决（约 {1:N0} 字符 ≈ {2:N0} token）…",
                        cand.Count, chars, tokens));

                string url = BuildUrl(cfg);
                var headers = BuildHeaders(cfg);
                int batchSize = cfg.BatchSize <= 0 ? 10 : cfg.BatchSize;

                for (int at = 0; at < cand.Count; at += batchSize)
                {
                    if (isCanceled != null && isCanceled())
                    {
                        if (log != null) log("已取消 AI 裁决。");
                        break;
                    }

                    var batch = cand.GetRange(at, Math.Min(batchSize, cand.Count - at));
                    stats.Requested++;

                    string reply;
                    string userMsg = BuildUserMessage(diffs, batch);
                    try
                    {
                        var body = BuildRequestBody(cfg, userMsg);
                        var resp = Http.PostJson(url, body, headers, cfg.TimeoutSeconds);
                        reply = ExtractContent(resp, cfg);
                        if (string.IsNullOrEmpty(reply))
                        {
                            var err = ExtractError(resp);
                            stats.LastError = string.IsNullOrEmpty(err)
                                ? "模型没有返回内容（可能是模型名不对，或服务没起）"
                                : err;
                            if (log != null) log("  AI 这一批没返回内容：" + stats.LastError);
                            continue;
                        }
                    }
                    catch (Exception ex)
                    {
                        stats.LastError = ex.Message;
                        if (log != null) log("  AI 请求失败：" + ex.Message);
                        continue;
                    }

                    stats.Succeeded++;
                    stats.SentChars += userMsg.Length;

                    var parsed = ParseReply(reply, batch.Count);
                    for (int k = 0; k < batch.Count; k++)
                    {
                        var v = parsed[k];
                        int abs = batch[k];
                        // 写回两个地方：
                        //   · verdicts[]  —— 调用方按"与 diffs 同下标"读
                        //   · diffs[].Ai  —— 报告/CSV 直接从这里读
                        // ★ 一开始只写了 verdicts[]，把"抄到 diffs 上"留给调用方，
                        //   结果 MainForm 抄了、探针没抄 → 探针报"明明 3 条裁决但 diff 全是 null"。
                        //   这种"契约依赖调用方记得做"的设计必然会有人漏，所以在这里一次写全。
                        verdicts[abs] = v;
                        if (abs >= 0 && abs < diffs.Count) diffs[abs].Ai = v;

                        switch (v.Verdict)
                        {
                            case Verdict.PrimaryRight: stats.PrimaryRight++; break;
                            case Verdict.OtherRight: stats.OtherRight++; break;
                            case Verdict.BothWrong: stats.BothWrong++; break;
                            case Verdict.Unsure: stats.Unsure++; break;
                            default: stats.Failed++; break;
                        }
                        if (v.Verdict != Verdict.None) stats.Verdicts++;
                    }

                    if (log != null)
                        log(string.Format("  第 {0} 批完成（本批 {1} 处）", stats.Requested, batch.Count));
                }
            }
            catch (Exception ex)
            {
                // 兜底：这个函数承诺过不抛异常
                stats.LastError = ex.Message;
                if (log != null) log("AI 裁决意外中断：" + ex.Message);
            }
            finally
            {
                sw.Stop();
                stats.ElapsedSeconds = sw.Elapsed.TotalSeconds;
            }
        }

        /// <summary>
        /// 探活：问一句最简单的话，确认配置能用。
        /// 给设置里的「测试连接」用 —— 让用户在跑整本书之前先确认能通。
        /// </summary>
        public static string TestConnection(Config cfg)
        {
            var bad = cfg == null ? "没有配置。" : cfg.Validate();
            if (bad != null) return "配置不完整：" + bad;

            try
            {
                var probe = new List<TypoFinder.Diff>
                {
                    new TypoFinder.Diff
                    {
                        Kind = TypoFinder.DiffKind.Replace,
                        Chapter = "连接测试",
                        Primary = "现", Other = "現",
                        Context = "他后来发【现】，时间过得很快。",
                    },
                };
                var body = BuildRequestBody(cfg, BuildUserMessage(probe, new List<int> { 0 }));
                var resp = Http.PostJson(BuildUrl(cfg), body, BuildHeaders(cfg), cfg.TimeoutSeconds);
                var reply = ExtractContent(resp, cfg);
                if (string.IsNullOrEmpty(reply))
                {
                    var err = ExtractError(resp);
                    return "连上了，但模型没返回内容。" +
                           (string.IsNullOrEmpty(err)
                                ? "（检查模型名是否正确、模型是否已下载）"
                                : "\n服务端说：" + err);
                }
                var parsed = ParseReply(reply, 1);
                return string.Format("连接成功！模型回答：{0}{1}\n原始输出：{2}",
                    parsed[0].VerdictText(),
                    parsed[0].Reason.Length > 0 ? "，" + parsed[0].Reason : "",
                    reply.Length > 80 ? reply.Substring(0, 80) + "…" : reply);
            }
            catch (Exception ex)
            {
                return "连接失败：" + ex.Message +
                       (cfg.IsCloud
                            ? "\n（云端接口在国内可能需要挂代理）"
                            : "\n（本地 Ollama 是否已启动？试 `ollama serve`）");
            }
        }
    }
}
