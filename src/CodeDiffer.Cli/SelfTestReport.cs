using System.Globalization;
using System.Text;
using System.Text.Json;

namespace CodeDiffer.Cli;

/// <summary>The selftest's HTML page: one self-contained file (no script, no fetch), light and dark.</summary>
internal static class SelfTestReport
{
    public static string Html(string name, List<string> trees, string version, DateTime started, double seconds, List<SelfTest.Check> checks,
        List<SelfTest.Metric> metrics, List<SelfTest.Step> steps, List<JsonElement> history, Dictionary<string, string> facts, string workRel)
    {
        int Count(SelfTest.Verdict v) => checks.Count(c => c.Verdict == v);
        bool passed = Count(SelfTest.Verdict.Fail) == 0;
        var h = new StringBuilder(64 * 1024);
        h.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        h.Append($"<title>CodeDiffer selftest · {E(name)}</title><style>{Css}</style></head><body><main>");
        h.Append($"<header><h1>CodeDiffer selftest <span class=\"repo\">{E(name)}</span></h1>");
        h.Append($"<span class=\"pill {(passed ? "pass" : "fail")}\">{(passed ? "PASSED" : "FAILED")}</span></header>");
        h.Append("<p class=\"sub\">").Append(E($"codediffer {version} · {started:yyyy-MM-dd HH:mm} · took {Dur(seconds)}")).Append("</p>");

        h.Append("<div class=\"chips\">");
        foreach (var v in Enum.GetValues<SelfTest.Verdict>())
            h.Append($"<span class=\"chip {Cls(v)}\"><b>{Count(v)}</b> {v.ToString().ToLowerInvariant()}</span>");
        h.Append("</div>");

        h.Append("<section><h2>What was compared</h2><table class=\"kv\">");
        for (int i = 0; i < trees.Count; i++)
            h.Append($"<tr><th>{(trees.Count == 3 ? new[] { "base", "v1", "v2" }[i] : new[] { "left", "right" }[i])}</th><td><code>{E(trees[i])}</code></td></tr>");
        foreach (var (k, v) in facts.Where(f => !f.Key.EndsWith(" trees", StringComparison.Ordinal)))
            h.Append($"<tr><th>{E(k)}</th><td>{E(v)}</td></tr>");
        h.Append("</table></section>");

        h.Append("<section><h2>Checks</h2>");
        foreach (var g in checks.GroupBy(c => c.Area))
        {
            h.Append($"<h3>{E(g.Key)}</h3><table class=\"checks\"><tbody>");
            foreach (var c in g)
                h.Append($"<tr><td class=\"v\"><span class=\"tag {Cls(c.Verdict)}\">{c.Verdict.ToString().ToUpperInvariant()}</span></td><td class=\"n\">{E(c.Name)}</td><td class=\"d\">{E(c.Detail)}</td></tr>");
            h.Append("</tbody></table>");
        }
        h.Append("</section>");

        if (metrics.Count > 0)
        {
            h.Append("<section><h2>Metrics</h2><div class=\"scroll\"><table class=\"grid\"><thead><tr><th>run</th><th>id</th><th>ended</th><th>time</th><th>read</th>" +
                     "<th>read rate</th><th>peak memory</th><th>allocated</th><th>CPU</th><th>threads</th><th>cache</th><th>from cache</th><th>merge</th></tr></thead><tbody>");
            foreach (var m in metrics)
                h.Append($"<tr><td>{E(m.Label)}</td><td><code>{E(m.Id)}</code></td><td>{E(m.Ended)}</td><td class=\"r\">{Dur(m.Seconds)}</td>" +
                         $"<td class=\"r\">{MB(m.ReadMB)}</td><td class=\"r\">{(m.Seconds > 0 ? MB(m.ReadMB / m.Seconds) + "/s" : "")}</td>" +
                         $"<td class=\"r\">{MB(m.PeakMB)}</td><td class=\"r\">{MB(m.AllocatedMB)}</td><td class=\"r\">{m.CpuSeconds.ToString("0.0", CultureInfo.InvariantCulture)} s</td>" +
                         $"<td class=\"r\">{m.Threads}</td><td>{E(m.Cache)}</td><td class=\"r\">{m.CacheSides:N0} sides</td>" +
                         $"<td>{(m.MergeSeconds is { } ms ? E($"{Dur(ms)} · {MB(m.MergeAllocatedMB ?? 0)} · {m.FilesMerged ?? 0:N0} files merged") : "")}</td></tr>");
            h.Append("</tbody></table></div>");
            h.Append("<p class=\"sub\">Memory, allocation and CPU are the process's: an MCP row's server ran the compares before it too, so its allocation and CPU include theirs.</p>");
            h.Append("<details><summary>Each run's record (as <code>codediffer runs</code> prints it)</summary>");
            foreach (var m in metrics.Where(m => m.Summary.Length > 0))
                h.Append($"<h4>{E(m.Label)}</h4><pre>{E(m.Summary)}</pre>");
            h.Append("</details></section>");
        }

        if (history.Count > 1)
        {
            h.Append("<section><h2>History on this repo</h2><div class=\"scroll\"><table class=\"grid\"><thead><tr><th>started</th><th>version</th><th>result</th><th>took</th><th>first compare</th><th>peak memory</th></tr></thead><tbody>");
            foreach (var r in Enumerable.Reverse(history).Take(15))
            {
                int fail = Int(r, "fail");
                var first = r.TryGetProperty("compares", out var cs) && cs.ValueKind == JsonValueKind.Array && cs.GetArrayLength() > 0 ? cs[0] : default;
                h.Append($"<tr><td>{E(Str(r, "started"))}</td><td>{E(Str(r, "tool"))}</td>" +
                         $"<td><span class=\"tag {(fail == 0 ? "pass" : "fail")}\">{Int(r, "pass")} pass · {fail} fail · {Int(r, "warn")} warn</span></td>" +
                         $"<td class=\"r\">{Dur(Dbl(r, "seconds"))}</td>" +
                         $"<td class=\"r\">{(first.ValueKind == JsonValueKind.Object ? $"{Dur(Dbl(first, "seconds"))} · {MB(Dbl(first, "readMB"))} read" : "")}</td>" +
                         $"<td class=\"r\">{(first.ValueKind == JsonValueKind.Object ? MB(Dbl(first, "peakMB")) : "")}</td></tr>");
            }
            h.Append("</tbody></table></div></section>");
        }

        h.Append("<section><h2>Commands run</h2><p class=\"sub\">").Append(E($"Each one's whole output is in logs\\; what it wrote is in {workRel}\\.")).Append("</p>");
        foreach (var s in steps)
            h.Append($"<details><summary><span class=\"tag {(s.Exit == 0 ? "pass" : "info")}\">exit {s.Exit}</span> {E(s.Label)} <span class=\"sub\">{Dur(s.Seconds)}</span></summary>" +
                     $"<pre class=\"cmd\">{E(s.Command)}</pre><pre>{E(s.Head)}</pre><p class=\"sub\"><a href=\"{E(s.Log.Replace('\\', '/'))}\">{E(s.Log)}</a></p></details>");
        h.Append("</section></main></body></html>");
        return h.ToString();
    }

    private static string Cls(SelfTest.Verdict v) => v.ToString().ToLowerInvariant();

    private static string Dur(double s)
        => s < 60 ? s.ToString("0.0", CultureInfo.InvariantCulture) + " s" : TimeSpan.FromSeconds(Math.Round(s)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    private static string MB(double mb)
        => double.IsNaN(mb) ? "" : mb >= 1024 ? (mb / 1024).ToString("0.00", CultureInfo.InvariantCulture) + " GB" : mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";

    private static int Int(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double Dbl(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static string Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    /// <summary>HTML-escaped text.</summary>
    internal static string E(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var b = new StringBuilder(s.Length + 16);
        foreach (var c in s)
            b.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", '\'' => "&#39;", _ => c.ToString() });
        return b.ToString();
    }

    private const string Css = """
        :root{--bg:#fbfbfa;--fg:#1d1f21;--mute:#6b6f76;--line:#e3e4e6;--card:#fff;--pass:#1a7f37;--passbg:#e6f4ea;--fail:#c62828;--failbg:#fdecea;
        --warn:#9a6700;--warnbg:#fff4d6;--skip:#57606a;--skipbg:#eef0f2;--info:#0b5cad;--infobg:#e7f0fb;--code:#f3f4f5}
        @media (prefers-color-scheme:dark){:root:not([data-theme="light"]){--bg:#16181b;--fg:#e6e7e9;--mute:#9aa0a6;--line:#2c3036;--card:#1d2024;
        --pass:#5fd08a;--passbg:#183524;--fail:#ff7b72;--failbg:#3d1d1d;--warn:#e3b341;--warnbg:#3a2f12;--skip:#a0a6ad;--skipbg:#262a30;--info:#79b8ff;--infobg:#132a44;--code:#23272c}}
        :root[data-theme="dark"]{--bg:#16181b;--fg:#e6e7e9;--mute:#9aa0a6;--line:#2c3036;--card:#1d2024;--pass:#5fd08a;--passbg:#183524;--fail:#ff7b72;
        --failbg:#3d1d1d;--warn:#e3b341;--warnbg:#3a2f12;--skip:#a0a6ad;--skipbg:#262a30;--info:#79b8ff;--infobg:#132a44;--code:#23272c}
        *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--fg);font:14px/1.5 system-ui,-apple-system,"Segoe UI",sans-serif}
        main{max-width:1180px;margin:0 auto;padding:24px 16px 64px}header{display:flex;flex-wrap:wrap;align-items:center;gap:12px}
        h1{font-size:22px;margin:0}h1 .repo{color:var(--mute);font-weight:500}h2{font-size:17px;margin:28px 0 8px;border-bottom:1px solid var(--line);padding-bottom:4px}
        h3{font-size:14px;margin:16px 0 4px;color:var(--mute);text-transform:uppercase;letter-spacing:.04em}h4{margin:12px 0 4px}
        .sub{color:var(--mute);margin:4px 0}.pill{padding:3px 12px;border-radius:999px;font-weight:700;font-size:13px}
        .chips{display:flex;flex-wrap:wrap;gap:8px;margin:14px 0}.chip{padding:4px 10px;border-radius:8px;font-size:13px}
        .pass{color:var(--pass);background:var(--passbg)}.fail{color:var(--fail);background:var(--failbg)}.warn{color:var(--warn);background:var(--warnbg)}
        .skip{color:var(--skip);background:var(--skipbg)}.info{color:var(--info);background:var(--infobg)}
        .tag{display:inline-block;padding:1px 7px;border-radius:5px;font-size:11px;font-weight:700;white-space:nowrap}
        table{border-collapse:collapse;width:100%;background:var(--card)}td,th{padding:6px 8px;border-bottom:1px solid var(--line);text-align:left;vertical-align:top}
        th{font-weight:600}.kv th{width:140px;color:var(--mute)}.checks td.v{width:64px}.checks td.n{width:32%;font-weight:500}.checks td.d{color:var(--mute);word-break:break-word}
        .grid th{font-size:12px;color:var(--mute);white-space:nowrap}.grid td{white-space:nowrap}.r{text-align:right}.scroll{overflow-x:auto}
        code,pre{font:12px/1.45 ui-monospace,Consolas,monospace}code{background:var(--code);padding:0 4px;border-radius:4px;word-break:break-all}
        pre{background:var(--code);padding:10px;border-radius:6px;overflow-x:auto;white-space:pre-wrap;word-break:break-word;margin:6px 0}
        pre.cmd{color:var(--info)}details{border-bottom:1px solid var(--line);padding:6px 0}summary{cursor:pointer}a{color:var(--info)}
        """;
}
