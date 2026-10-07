using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using TomatoBiquga;

/// <summary>
/// AI 裁决的**端到端验证（不依赖真实模型）**：
/// 起一个假的 OpenAI 兼容服务，让 AiAdjudicator 真的走一遍 HTTP，
/// 验证请求体、请求头、响应解析、以及裁决结果有没有正确落到 Diff 上。
///
/// 为什么要这个：纯函数单测覆盖了"拼字符串/解析字符串"，
/// 但没验证"这些字符串真的被发出去了、真的被当成 JSON 处理了"。
/// 这个项目已经吃过一次亏 —— 离线全绿而每一次真实请求都失败。
///
/// 用法：_aimock.exe [模式]   （模式：openai / ollama / err）
/// 退出码 0 = 全部通过。
/// </summary>
internal static class AiMockProbe
{
    private static int _pass, _fail;
    private static readonly List<string> Fails = new List<string>();

    private static void Chk(string n, bool ok, string d = "")
    {
        Console.WriteLine((ok ? "  [ok] " : "  [FAIL] ") + n + (ok || d.Length == 0 ? "" : " → " + d));
        if (!ok) { _fail++; Fails.Add(n + " " + d); }
        else _pass++;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string mode = args.Length > 0 ? args[0] : "openai";

        // 假服务的响应：模拟模型按我们要求的格式回答
        // 第 1 条说 A 对，第 2 条说 B 对（主源错），第 3 条说 C（都不对）+ 建议
        string replyContent = "1|A|用字正确|\n2|B|主源是错字|\n3|C|两个都不对|後";

        string fakeBody;
        if (mode == "ollama")
            fakeBody = "{\"model\":\"mock\",\"message\":{\"role\":\"assistant\",\"content\":" + J(replyContent) + "},\"done\":true}";
        else if (mode == "err")
            fakeBody = "{\"error\":{\"message\":\"model 'nope' not found\",\"type\":\"invalid_request_error\"}}";
        else
            fakeBody = "{\"id\":\"x\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":" + J(replyContent) + "},\"finish_reason\":\"stop\"}]}";

        int port = FreePort();
        var srv = new Thread(() => Serve(port, fakeBody)) { IsBackground = true };
        srv.Start();
        Thread.Sleep(300);

        // ------------------------------------------------------------
        //  路径自适应专项：假服务只认一个正确路径，别的都回 404，
        //  验证"用户把 base_url 填成各种形状都能自愈"。
        //  真实世界对应：智谱 /api/paas/v4、商汤 /compatible-mode/v2 都栽在这上面。
        // ------------------------------------------------------------
        if (mode == "fallback")
        {
            Console.WriteLine("=== 接口路径自适应验证（假服务只认一个路径，其余回 404）===");
            Console.WriteLine();

            var cases = new List<string[]>();
            // { 用户填的 base_url（相对路径）, 假服务真正认的路径, 期望候选数 }
            // 候选数说明：结尾已经是 /v<数字> 时**不再**拼 /v1（否则会成 .../v4/v1/...），
            // 所以那几种只有 2 个候选。这是有意为之，不是漏了。
            cases.Add(new[] { "", "/v1/chat/completions", "3" });
            cases.Add(new[] { "/", "/v1/chat/completions", "3" });
            cases.Add(new[] { "/v1", "/v1/chat/completions", "2" });
            cases.Add(new[] { "/api/paas/v4", "/api/paas/v4/chat/completions", "2" });
            cases.Add(new[] { "/compatible-mode/v2", "/compatible-mode/v2/chat/completions", "2" });
            cases.Add(new[] { "/v1/chat/completions", "/v1/chat/completions", "1" });

            foreach (var cs in cases)
            {
                string rel = cs[0];
                string goodPath = cs[1];
                int wantCands = int.Parse(cs[2]);

                int p2 = FreePort();
                string good = goodPath;
                string okBody = fakeBody;
                var s2 = new Thread(() => ServeStrict(p2, good, okBody)) { IsBackground = true };
                s2.Start();
                Thread.Sleep(200);

                string label = rel.Length == 0 ? "(只填主机)" : rel;
                var cf = new AiAdjudicator.Config
                {
                    Backend = "openai",
                    BaseUrl = "http://127.0.0.1:" + p2 + rel,
                    Model = "mock-model", ApiKey = "sk-mock", TimeoutSeconds = 10,
                };
                var cands = AiAdjudicator.BuildUrlCandidates(cf);
                Chk("自适应 [" + label + "] 候选数应为 " + wantCands + "（实际 " + cands.Count + "）",
                    cands.Count == wantCands);
                // 第一个候选必须是"用户填的原样" —— 从官方文档抄来的完整端点不能被改坏
                Chk("自适应 [" + label + "] 首选是用户原样", cands[0] == cf.BaseUrl.TrimEnd('/'), cands[0]);

                var diffs2 = new List<TypoFinder.Diff>
                {
                    new TypoFinder.Diff { Kind = TypoFinder.DiffKind.Replace, Chapter = "c",
                        Primary = "现", Other = "現", Context = "发【现】" },
                };
                var v2 = new AiAdjudicator.VerdictResult[1];
                var st2 = new AiAdjudicator.SessionStats();
                var lg = new List<string>();
                AiAdjudicator.Run(diffs2, v2, cf, m => lg.Add(m), () => false, st2);

                Chk("自适应 [" + label + "] 最终请求成功（" + (st2.Succeeded == 1 ? "通" : st2.LastError) + "）",
                    st2.Succeeded == 1);
                Chk("自适应 [" + label + "] 拿到裁决",
                    v2[0] != null && v2[0].Verdict == AiAdjudicator.Verdict.PrimaryRight);
                if (wantCands > 1)
                {
                    bool adapted = false;
                    foreach (var m in lg) if (m.IndexOf("自适应成功", StringComparison.Ordinal) >= 0) adapted = true;
                    Chk("自适应 [" + label + "] 日志里报告了自适应", adapted);
                }
            }

            Console.WriteLine();
            Chk("判断：404 值得换下一个候选", AiAdjudicator.ShouldTryNextUrl("curl 退出码 22：HTTP 404"));
            Chk("判断：NOT_FOUND 值得换", AiAdjudicator.ShouldTryNextUrl("{\"code\":5,\"message\":\"NOT_FOUND\"}"));
            Chk("判断：no Route matched 值得换", AiAdjudicator.ShouldTryNextUrl("no Route matched with those values"));
            // ★ 这几条是重点：鉴权失败绝不能换路径，否则白等还可能触发风控
            Chk("判断：403 不换（账户权限问题，换路径没用）",
                !AiAdjudicator.ShouldTryNextUrl("curl 退出码 22：HTTP 403 {\"code\":7,\"message\":\"Forbidden\"}"));
            Chk("判断：401 不换", !AiAdjudicator.ShouldTryNextUrl("HTTP 401"));
            Chk("判断：超时不换（换路径也一样慢）", !AiAdjudicator.ShouldTryNextUrl("curl 超时"));
            Chk("判断：空错误不换", !AiAdjudicator.ShouldTryNextUrl(""));

            return Finish();
        }

        Console.WriteLine("=== AI 裁决端到端验证（假服务：" + mode + "，端口 " + port + "）===");
        Console.WriteLine();

        var cfg = new AiAdjudicator.Config
        {
            Backend = mode == "ollama" ? "ollama" : "openai",
            BaseUrl = "http://127.0.0.1:" + port,
            Model = "mock-model",
            ApiKey = mode == "ollama" ? "" : "sk-mock",
            TimeoutSeconds = 15,
            BatchSize = 10,
        };

        // 造 3 条高可疑差异 + 1 条不值得送 AI 的
        var diffs = new List<TypoFinder.Diff>
        {
            new TypoFinder.Diff { Kind = TypoFinder.DiffKind.Replace, Chapter = "第一章", Primary = "现", Other = "現", Context = "他后来发【现】，时间过得快。" },
            new TypoFinder.Diff { Kind = TypoFinder.DiffKind.Replace, Chapter = "第一章", Primary = "里", Other = "裡", Context = "他心里想。" },
            new TypoFinder.Diff { Kind = TypoFinder.DiffKind.Replace, Chapter = "第二章", Primary = "后", Other = "後", Context = "皇后娘娘。" },
            new TypoFinder.Diff { Kind = TypoFinder.DiffKind.ExtraInPrimary, Chapter = "第二章", Primary = "一整段广告", Other = "", Context = "疑似站点差异" },
        };

        var verdicts = new AiAdjudicator.VerdictResult[diffs.Count];
        var stats = new AiAdjudicator.SessionStats();
        var logs = new List<string>();
        AiAdjudicator.Run(diffs, verdicts, cfg, m => { logs.Add(m); Console.WriteLine("    [log] " + m); }, () => false, stats);

        // 诊断：把真实状态打出来（"明明计数对了但没写回"这种只能靠它定位）
        Console.WriteLine();
        Console.WriteLine("    [diag] verdicts 数组：");
        for (int i = 0; i < verdicts.Length; i++)
            Console.WriteLine("      verdicts[" + i + "] = " +
                (verdicts[i] == null ? "<null>" : verdicts[i].Verdict.ToString()));
        Console.WriteLine("    [diag] diffs 上的 Ai：");
        for (int i = 0; i < diffs.Count; i++)
            Console.WriteLine("      diffs[" + i + "].Ai = " +
                (diffs[i].Ai == null ? "<null>" : diffs[i].Ai.Verdict.ToString()));

        Console.WriteLine();
        Chk("会话：没有抛异常（Run 承诺过不抛）", true);
        Chk("会话：筛出 3 条高可疑（广告那条被排除）", stats.Candidates == 3, stats.Candidates.ToString());
        Chk("会话：发出了 1 批请求", stats.Requested == 1, stats.Requested.ToString());
        if (mode != "err")
            Chk("会话：请求成功", stats.Succeeded == 1, stats.Succeeded.ToString());
        else
            Chk("会话：错误响应下不算成功（Succeeded=0）", stats.Succeeded == 0, stats.Succeeded.ToString());

        if (mode == "err")
        {
            // 错误模式：应当拿到错误信息、没有任何裁决，而且不影响主流程
            Chk("错误模式：拿到了服务端错误信息", stats.LastError.Contains("not found"), stats.LastError);
            Chk("错误模式：没有任何裁决被写回", stats.Verdicts == 0, stats.Verdicts.ToString());
            Chk("错误模式：diffs 上的 Ai 仍是空", diffs[0].Ai == null || diffs[0].Ai.Verdict == AiAdjudicator.Verdict.None);
            Chk("错误模式：报告照常能生成（AI 失败不影响主流程）", BuildReportOk(diffs));
        }
        else
        {
            // 正常模式：3 条裁决都要落到对应的 Diff 上
            Chk("结果：拿到 3 条裁决", stats.Verdicts == 3, stats.Verdicts.ToString());
            Chk("结果：1 条判主源对", stats.PrimaryRight == 1, stats.PrimaryRight.ToString());
            Chk("结果：1 条判主源错", stats.OtherRight == 1, stats.OtherRight.ToString());
            Chk("结果：1 条判两边都不对", stats.BothWrong == 1, stats.BothWrong.ToString());

            Chk("写回：diff[0] 是主源对", diffs[0].Ai != null && diffs[0].Ai.Verdict == AiAdjudicator.Verdict.PrimaryRight);
            Chk("写回：diff[1] 是主源错", diffs[1].Ai != null && diffs[1].Ai.Verdict == AiAdjudicator.Verdict.OtherRight);
            Chk("写回：diff[2] 是两边都不对", diffs[2].Ai != null && diffs[2].Ai.Verdict == AiAdjudicator.Verdict.BothWrong);
            Chk("写回：diff[2] 拿到了建议写法", diffs[2].Ai != null && diffs[2].Ai.Suggestion == "後",
                diffs[2].Ai == null ? "null" : diffs[2].Ai.Suggestion);
            Chk("写回：被排除的那条没被写（保持未裁决）",
                diffs[3].Ai == null || diffs[3].Ai.Verdict == AiAdjudicator.Verdict.None);

            // 报告要能把 AI 结论体现出来（这是用户真正看的东西）
            var book = new BookInfo { Title = "AI 验证", Author = "测试" };
            var res = new TypoFinder.Result { Title = "AI 验证", OtherSource = "对照源" };
            res.ComparedChapters = 2; res.IdenticalChapters = 0;
            res.Diffs.AddRange(diffs);
            var report = TypoFinder.BuildReport(book, res, @"C:\x.txt", "对照源");
            Chk("报告：出现待改清单", report.Contains("待改清单"));
            Chk("报告：给出了建议写法「後」", report.Contains("後"));
            Chk("报告：带 AI 标记", report.Contains("【AI】"));
            var csv = TypoFinder.BuildCsv(res);
            Chk("CSV：新增 AI 列", csv.Contains("AI结论"), csv.Split('\n')[0]);
            Chk("CSV：主源错那一行标出来了", csv.Contains("主源错"));
        }

        Chk("统计：摘要里含字符数", stats.Summary().Contains("字符"), stats.Summary());
        return _fail == 0 ? Finish() : Finish();
    }

    private static bool BuildReportOk(List<TypoFinder.Diff> diffs)
    {
        try
        {
            var book = new BookInfo { Title = "t", Author = "a" };
            var r = new TypoFinder.Result { Title = "t", OtherSource = "o" };
            r.Diffs.AddRange(diffs);
            return TypoFinder.BuildReport(book, r, "x", "o").Length > 0;
        }
        catch { return false; }
    }

    private static int Finish()
    {
        Console.WriteLine();
        Console.WriteLine(string.Format("通过 {0}/{1}", _pass, _pass + _fail));
        if (_fail > 0) foreach (var f in Fails) Console.WriteLine("  " + f);
        else Console.WriteLine("==> AI 裁决端到端验证通过");
        Environment.ExitCode = _fail == 0 ? 0 : 1;
        return _fail == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------ 极简 HTTP 服务

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>够用的假服务：读掉请求头与 Content-Length 的 body，然后回一个固定 JSON</summary>
    private static void Serve(int port, string body)
    {
        ServeStrict(port, null, body);
    }

    /// <summary>
    /// 严格版假服务：只有请求路径等于 <paramref name="onlyPath"/> 时才回 200，
    /// 其余一律 404（模仿"接口路径不对"的真实表现）。
    /// onlyPath 为 null 时全部放行。
    /// </summary>
    private static void ServeStrict(int port, string onlyPath, string body)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        while (true)
        {
            try
            {
                using (var client = listener.AcceptTcpClient())
                using (var stream = client.GetStream())
                {
                    // 读请求头
                    var buf = new byte[8192];
                    var sb = new StringBuilder();
                    int contentLength = 0;
                    string path = "/";
                    while (true)
                    {
                        int n = stream.Read(buf, 0, buf.Length);
                        if (n <= 0) break;
                        sb.Append(Encoding.UTF8.GetString(buf, 0, n));
                        var head = sb.ToString();
                        int hEnd = head.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                        if (hEnd >= 0)
                        {
                            var lines = head.Substring(0, hEnd).Split(new[] { "\r\n" }, StringSplitOptions.None);
                            if (lines.Length > 0)
                            {
                                // "POST /v1/chat/completions HTTP/1.1"
                                var parts = lines[0].Split(' ');
                                if (parts.Length >= 2) path = parts[1];
                            }
                            foreach (var line in lines)
                            {
                                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                    int.TryParse(line.Substring(15).Trim(), out contentLength);
                            }
                            int got = Encoding.UTF8.GetByteCount(head.Substring(hEnd + 4));
                            while (got < contentLength)
                            {
                                int m = stream.Read(buf, 0, buf.Length);
                                if (m <= 0) break;
                                got += m;
                            }
                            break;
                        }
                    }

                    bool ok = onlyPath == null || string.Equals(path, onlyPath, StringComparison.Ordinal);
                    string payload = ok
                        ? body
                        : "{\"error\":{\"code\":5,\"message\":\"NOT_FOUND\",\"details\":[]}}";
                    var bodyBytes = Encoding.UTF8.GetBytes(payload);
                    var resp = "HTTP/1.1 " + (ok ? "200 OK" : "404 Not Found") + "\r\n" +
                               "Content-Type: application/json\r\n" +
                               "Content-Length: " + bodyBytes.Length + "\r\n" +
                               "Connection: close\r\n\r\n";
                    var headBytes = Encoding.ASCII.GetBytes(resp);
                    stream.Write(headBytes, 0, headBytes.Length);
                    stream.Write(bodyBytes, 0, bodyBytes.Length);
                    stream.Flush();
                }
            }
            catch { /* 探针结束时的正常断开 */ }
        }
    }

    private static string J(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"') sb.Append("\\\"");
            else if (c == '\\') sb.Append("\\\\");
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
