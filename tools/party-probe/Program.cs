using System.Security.Cryptography;
using System.Text;

using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Protocol;

/// <summary>
/// 组队共战视角探针（本地开发工具）：以指定账号登录 → 建立 /ws 长连接 →
/// 抓首份全量快照与社交帧，打印该成员在行动参与会话中实际收到的
/// 盟友阵列（allies）与本机位（selfIndex），用于交叉验证「同一场战斗、各成员视角一致」。
/// 用法：dotnet run --project tools/party-probe -- &lt;account&gt; &lt;password&gt; [account password ...]
/// </summary>
if (args.Length < 2 || args.Length % 2 != 0)
{
    Console.Error.WriteLine("用法：party-probe <account> <password> [account password ...]");
    return 2;
}

var views = new List<MemberView>();
for (var i = 0; i < args.Length; i += 2)
{
    var (account, password) = (args[i], args[i + 1]);
    views.Add(await ProbeAsync(account, password));
}

// 交叉比对：所有成员的 allies 阵列应逐位一致（同一冻结规格剧本）；
// 唯一差异是 selfIndex 指向各自机位。
if (views.Count > 1)
{
    Console.WriteLine("=== 交叉比对 ===");
    var reference = views[0];
    foreach (var view in views.Skip(1))
    {
        var sameSession = view.SessionId == reference.SessionId;
        var sameAllies = reference.AllyFingerprint == view.AllyFingerprint;
        Console.WriteLine($"{view.DisplayName} vs {reference.DisplayName}: session 一致={sameSession}, allies 阵列一致={sameAllies}, selfIndex {view.SelfIndex} vs {reference.SelfIndex}");
    }
}
return 0;

/// <summary>单成员视角采样：登录→握手→快照→提取 session/allies/selfIndex。</summary>
static async Task<MemberView> ProbeAsync(string account, string password)
{
    var http = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:5128") };
    // 风控信号校验：哈希字段必须为 64 位十六进制（SHA-256 摘要格式），deviceId 为 UUID；
    // 每账号独立合成，避免命中设备在线配额互踢。
    var deviceId = Guid.NewGuid().ToString();
    var configHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"probe-config-{account}")));
    var fingerprint = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"probe-fp-{account}")));
    var login = await http.PostAsJsonAsync("/api/v1/auth/login", new
    {
        account,
        password,
        riskSignals = new { deviceConfigHash = configHash, deviceId, networkHash = (string?)null, browserFingerprint = fingerprint },
    });
    var body = await login.Content.ReadAsStringAsync();
    if (!login.IsSuccessStatusCode)
        throw new InvalidOperationException($"[{account}] 登录失败 HTTP {(int)login.StatusCode}: {body}");
    var cookie = login.Headers.TryGetValues("Set-Cookie", out var values)
        ? values.Select(v => v.Split(';')[0]).FirstOrDefault(v => v.StartsWith("daohua_session=", StringComparison.Ordinal))
        : null;
    if (cookie is null) throw new InvalidOperationException($"[{account}] 登录响应未下发会话 Cookie。");

    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Cookie", cookie);
    await ws.ConnectAsync(new Uri("ws://127.0.0.1:5128/ws"), CancellationToken.None);

    var hello = WireEnvelopeCodec.EncodeJson(WireMessageType.Hello, Guid.NewGuid(), 1,
        new HelloRequest("party-probe/1.0", DeviceFingerprint: fingerprint, DeviceConfigHash: configHash, DeviceId: deviceId));
    await ws.SendAsync(hello, WebSocketMessageType.Binary, true, CancellationToken.None);

    JsonElement? snapshot = null;
    JsonElement? social = null;
    var frame = new MemoryStream();
    var buffer = new byte[64 * 1024];
    var deadline = DateTime.UtcNow.AddSeconds(10);
    while ((snapshot is null || social is null) && DateTime.UtcNow < deadline)
    {
        var result = await ws.ReceiveAsync(buffer.AsMemory(), CancellationToken.None);
        if (result.MessageType == WebSocketMessageType.Close) break;
        frame.Write(buffer, 0, result.Count);
        if (!result.EndOfMessage) continue; // 分片帧：累积到消息边界再解码
        var bytes = frame.ToArray();
        frame.SetLength(0);
        var envelope = WireEnvelopeCodec.Decode(bytes);
        switch (envelope.MessageType)
        {
            case WireMessageType.Snapshot:
                snapshot = JsonDocument.Parse(envelope.Payload).RootElement.Clone();
                break;
            case WireMessageType.SocialState:
                social = JsonDocument.Parse(envelope.Payload).RootElement.Clone();
                break;
            case WireMessageType.Error:
                Console.Error.WriteLine($"[{account}] Error 帧: {JsonDocument.Parse(envelope.Payload).RootElement.GetRawText()}");
                break;
        }
    }

    if (snapshot is null) throw new InvalidOperationException($"[{account}] 未收到全量快照。");

    // action_queue 分段 Data 为队列条目裸数组：取队首（首个）的 expedition。
    var playerId = snapshot.Value.GetProperty("playerId").GetString()!;
    var displayName = snapshot.Value.GetProperty("displayName").GetString()!;
    string? sessionId = null, encounterId = null, phase = null, specHash = null;
    int? selfIndex = null, waveNumber = null;
    long? specSeed = null;
    double? battleElapsed = null, specStartedAt = null;
    var allies = new List<JsonElement>();
    var enemies = new List<JsonElement>();
    foreach (var section in snapshot.Value.GetProperty("sections").EnumerateArray())
    {
        if (!section.TryGetProperty("section", out var kind) || kind.GetString() != "action_queue") continue;
        var data = section.GetProperty("data");
        if (data.ValueKind != JsonValueKind.Array) continue;
        var slotNo = 0;
        foreach (var entry in data.EnumerateArray())
        {
            var intentId = "?";
            if (entry.TryGetProperty("intent", out var it) && it.TryGetProperty("actionId", out var aid))
                intentId = aid.ValueKind == JsonValueKind.String ? aid.GetString()!
                    : aid.TryGetProperty("value", out var av) ? av.GetString()!
                    : aid.GetRawText();
            var st = entry.TryGetProperty("status", out var s)
                ? s.ValueKind == JsonValueKind.Number ? s.GetInt32().ToString() : s.GetRawText()
                : "?";
            var hasSession = entry.TryGetProperty("participation", out var p2) && p2.ValueKind == JsonValueKind.Object;
            var hasExp = entry.TryGetProperty("expedition", out var e2) && e2.ValueKind == JsonValueKind.Object;
            Console.WriteLine($"    queue[{slotNo}] {intentId} status={st} participation={hasSession} expedition={hasExp}");
            slotNo++;
            if (!entry.TryGetProperty("expedition", out var expedition) || expedition.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("participation", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                sessionId = ps.GetProperty("sessionId").GetString();
                specSeed = null;
                specStartedAt = null;
                specHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ps.GetRawText())))[..16];
            }
            encounterId = expedition.TryGetProperty("encounterId", out var e) ? e.GetString() : encounterId;
            phase = expedition.TryGetProperty("phase", out var p) ? p.GetString() : phase;
            waveNumber = expedition.TryGetProperty("waveNumber", out var w) ? w.GetInt32() : waveNumber;
            battleElapsed = expedition.TryGetProperty("battleElapsedSeconds", out var be) && be.ValueKind == JsonValueKind.Number ? be.GetDouble() : battleElapsed;
            selfIndex = expedition.TryGetProperty("selfIndex", out var si) && si.ValueKind == JsonValueKind.Number ? si.GetInt32() : selfIndex;
            if (expedition.TryGetProperty("allies", out var allyArr) && allyArr.ValueKind == JsonValueKind.Array)
                allies = allyArr.EnumerateArray().Select(a => a.Clone()).ToList();
            if (expedition.TryGetProperty("enemies", out var enemyArr) && enemyArr.ValueKind == JsonValueKind.Array)
                enemies = enemyArr.EnumerateArray().Select(x => x.Clone()).ToList();
            break; // 只看队首
        }
    }

    Console.WriteLine($"=== {displayName} ({account}) 视角 ===");
    Console.WriteLine($"playerId={playerId}");
    Console.WriteLine($"sessionId={sessionId}  encounter={encounterId}  phase={phase}  wave={waveNumber}  battleElapsed={(battleElapsed.HasValue ? $"{battleElapsed:F1}s" : "?")}  selfIndex={selfIndex}");
    Console.WriteLine($"spec: seed={specSeed}  startedAt={(specStartedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds((long)specStartedAt.Value).ToString("HH:mm:ss") : "?")}  specHash={specHash}  (now={(DateTimeOffset.UtcNow - (specStartedAt.HasValue ? DateTimeOffset.FromUnixTimeSeconds((long)specStartedAt.Value) : DateTimeOffset.UtcNow)).TotalMinutes:F1}min 会话后）");
    Console.WriteLine($"allies（{allies.Count} 人，memberIndex 序）:");
    foreach (var ally in allies)
    {
        var mi = ally.GetProperty("memberIndex").GetInt32();
        var name = ally.GetProperty("displayName").GetString();
        var hp = ally.GetProperty("hitpoints").GetDouble();
        var maxHp = ally.GetProperty("maxHitpoints").GetDouble();
        var mp = ally.GetProperty("manapoints").GetDouble();
        var maxMp = ally.GetProperty("maxManapoints").GetDouble();
        var respawn = ally.TryGetProperty("respawnAtSeconds", out var r) && r.ValueKind == JsonValueKind.Number ? $" respawn@{r.GetDouble():F1}s" : "";
        var mark = mi == selfIndex ? " ←本机位（我）" : "";
        Console.WriteLine($"  [{mi}] {name}  HP {hp:F0}/{maxHp:F0}  MP {mp:F0}/{maxMp:F0}{respawn}{mark}");
    }
    if (enemies.Count > 0)
    {
        var totalHp = enemies.Sum(x => x.TryGetProperty("hitpoints", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetDouble() : 0);
        Console.WriteLine($"enemies（{enemies.Count} 只）: 总血 {totalHp:F0}");
        foreach (var enemy in enemies)
            Console.WriteLine($"  {(enemy.TryGetProperty("name", out var n) ? n.GetString() : "?")}  HP {(enemy.TryGetProperty("hitpoints", out var h) ? h.GetDouble() : 0):F0}");
    }
    if (social is not null && social.Value.TryGetProperty("party", out var party) && party.ValueKind == JsonValueKind.Object)
    {
        var members = party.TryGetProperty("members", out var m) ? m.EnumerateArray().Select(x =>
            $"{x.GetProperty("displayName").GetString()}{(x.GetProperty("playerId").GetString() == playerId ? "(我)" : "")} ready={x.GetProperty("ready").GetBoolean()} online={x.GetProperty("online").GetBoolean()}") : Enumerable.Empty<string>();
        Console.WriteLine($"party「{party.GetProperty("name").GetString()}」成员: {string.Join(" | ", members)}");
    }
    Console.WriteLine();

    var allyFingerprint = string.Join("|", allies.OrderBy(a => a.GetProperty("memberIndex").GetInt32()).Select(a => a.GetRawText()));
    return new MemberView(displayName, playerId, sessionId, selfIndex, allyFingerprint);
}

internal sealed record MemberView(string? DisplayName, string? PlayerId, string? SessionId, int? SelfIndex, string AllyFingerprint);
