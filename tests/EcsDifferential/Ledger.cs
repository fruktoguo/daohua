using System.Text;

namespace DaoHua.EcsDifferential;

/// <summary>
/// 事件台账：JSON Lines 追加写。所有多值字段在生成处即完成排序归一化，
/// 保证两侧实现语义一致时文件逐字节相同。
/// </summary>
public sealed class Ledger : IDisposable
{
    private readonly StreamWriter _writer;

    public Ledger(string path)
    {
        _writer = new StreamWriter(path, append: false, Encoding.UTF8)
        {
            AutoFlush = true,
            NewLine = "\n"
        };
    }

    /// <summary>当前操作序号（-op 行的 s 字段），由 Runner 在派发操作前设置。</summary>
    public long OpSeq { get; set; }

    /// <summary>当前操作内回调序，Runner 每操作清零。</summary>
    public int CbIndex;

    /// <summary>当前执行的定向用例名，无定向上下文时为 null。</summary>
    public string CaseName;

    private void Line(StringBuilder sb)
    {
        _writer.Write(sb.Append('\n'));
    }

    public void Meta(string keyValues)
    {
        Line(new StringBuilder(96).Append("{\"t\":\"meta\"," ).Append(keyValues).Append('}'));
    }

    public void SysList(string orderedNames)
    {
        Line(new StringBuilder(128).Append("{\"t\":\"syslist\",\"v\":").Append(orderedNames).Append('}'));
    }

    public void Op(string kind, string args)
    {
        var sb = new StringBuilder(96).Append("{\"t\":\"op\",\"s\":").Append(OpSeq).Append(",\"k\":\"").Append(kind).Append('"');
        if (CaseName != null) sb.Append(",\"case\":\"").Append(Esc(CaseName)).Append('"');
        if (args != null) sb.Append(',').Append(args);
        Line(sb.Append('}'));
    }

    public void Cb(string sys, string tag, long entityId, string comp, string payload)
    {
        Line(new StringBuilder(128)
            .Append("{\"t\":\"cb\",\"s\":").Append(OpSeq)
            .Append(",\"i\":").Append(CbIndex++)
            .Append(",\"sys\":\"").Append(sys).Append('"')
            .Append(",\"tag\":\"").Append(tag).Append('"')
            .Append(",\"e\":").Append(entityId)
            .Append(",\"c\":\"").Append(comp).Append('"')
            .Append(",\"v\":\"").Append(Esc(payload)).Append("\"}"));
    }

    public void Q(string kind, string payload)
    {
        Line(new StringBuilder(96).Append("{\"t\":\"q\",\"s\":").Append(OpSeq)
            .Append(",\"k\":\"").Append(kind).Append("\",\"v\":").Append(payload).Append('}'));
    }

    /// <summary>每操作后的世界归一化摘要哈希；h 相同则两侧状态等价。</summary>
    public void St(ulong hash, int entityCount)
    {
        Line(new StringBuilder(64).Append("{\"t\":\"st\",\"s\":").Append(OpSeq)
            .Append(",\"h\":\"").Append(hash.ToString("x16")).Append("\",\"n\":").Append(entityCount).Append('}'));
    }

    /// <summary>批次摘要：本操作内每个 sys|tag 的回调原始顺序哈希 + 排序后实体集合。</summary>
    public void Bs(Dictionary<string, List<long>> batch)
    {
        var sb = new StringBuilder(256).Append("{\"t\":\"bs\",\"s\":").Append(OpSeq).Append(",\"v\":{");
        var first = true;
        foreach (var kv in batch.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            if (!first) sb.Append(',');
            first = false;
            var sorted = kv.Value.ToList();
            sorted.Sort();
            sb.Append('"').Append(kv.Key).Append("\":{\"o\":\"").Append(Fnv64.Hash(string.Join(',', kv.Value)).ToString("x8"))
                .Append("\",\"n\":").Append(kv.Value.Count)
                .Append(",\"ids\":[").Append(string.Join(',', sorted)).Append("]}");
        }

        Line(sb.Append("}}"));
    }

    /// <summary>周期性全量归一化转储（单行），便于 diff 时直接看到差异内容。</summary>
    public void Dump(string dump)
    {
        Line(new StringBuilder(dump.Length + 64).Append("{\"t\":\"dump\",\"s\":").Append(OpSeq)
            .Append(",\"v\":\"").Append(Esc(dump)).Append("\"}"));
    }

    public void Ex(string where, Exception e)
    {
        var detail = Environment.GetEnvironmentVariable("DIFF_EX_STACK") == "1"
            ? Esc(e.ToString())
            : Esc(e.Message);
        Line(new StringBuilder(160).Append("{\"t\":\"ex\",\"s\":").Append(OpSeq)
            .Append(",\"w\":\"").Append(where).Append("\",\"et\":\"").Append(e.GetType().Name)
            .Append("\",\"m\":\"").Append(detail).Append("\"}"));
    }

    public void Ex(string where, string detail)
    {
        Line(new StringBuilder(160).Append("{\"t\":\"ex\",\"s\":").Append(OpSeq)
            .Append(",\"w\":\"").Append(where).Append("\",\"et\":\"note\",\"m\":\"").Append(Esc(detail)).Append("\"}"));
    }

    public void Case(string name)
    {
        Line(new StringBuilder(64).Append("{\"t\":\"case\",\"name\":\"").Append(name).Append("\"}"));
    }

    public void Final(string payload)
    {
        Line(new StringBuilder(payload.Length + 32).Append("{\"t\":\"final\",").Append(payload).Append('}'));
    }

    public void Dispose() => _writer.Dispose();

    public static string Esc(string s)
    {
        if (s == null) return null;
        StringBuilder sb = null;
        for (var i = 0; i < s.Length; i++)
        {
            var ch = s[i];
            string rep = ch switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ => ch < ' ' || ch > '~' ? "\\u" + ((int)ch).ToString("x4") : null
            };
            if (rep != null)
            {
                sb ??= new StringBuilder(s.Length + 8).Append(s, 0, i);
                sb.Append(rep);
            }
            else sb?.Append(ch);
        }

        return sb?.ToString() ?? s;
    }
}

/// <summary>FNV-1a 64 位哈希，跨进程/跨实现稳定。</summary>
public static class Fnv64
{
    public static ulong Hash(string s)
    {
        ulong h = 14695981039346656037UL;
        foreach (var ch in s)
        {
            h ^= ch;
            h *= 1099511628211UL;
        }

        return h;
    }
}
