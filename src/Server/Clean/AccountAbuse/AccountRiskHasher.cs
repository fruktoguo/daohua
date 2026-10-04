using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Server.Clean.AccountAbuse;

/// <summary>
/// 将跨请求风险信号归一化为 HMAC 摘要。随机密钥提升低熵信号的反查成本，密钥与数据需一并保护。
/// </summary>
public sealed class AccountRiskHasher
{
    private const int MaxSignalLength = 256;
    private readonly byte[] _key;

    /// <summary>测试或显式配置使用的 HMAC 密钥。</summary>
    public AccountRiskHasher(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
    }

    private AccountRiskHasher(byte[] key) => _key = key;

    /// <summary>首次启动生成随机 HMAC 密钥并原子保存在数据库，所有进程共享且重启后匹配保持稳定。</summary>
    public static async Task<AccountRiskHasher> CreateAsync(Npgsql.NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new Npgsql.NpgsqlCommand("""
            INSERT INTO platform.account_risk_key(key_id,key_bytes) VALUES (1,$1)
            ON CONFLICT (key_id) DO UPDATE SET key_bytes=platform.account_risk_key.key_bytes RETURNING key_bytes
            """, connection);
        command.Parameters.AddWithValue(RandomNumberGenerator.GetBytes(32));
        var key = (byte[])(await command.ExecuteScalarAsync(cancellationToken))!;
        return new AccountRiskHasher(key);
    }

    /// <summary>哈希全部有效信号，并按固定顺序输出，便于事务锁与审计复用。</summary>
    public IReadOnlyList<AccountRiskSignalHash> Hash(AccountRiskSignals? signals, AccountRiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (signals is null) return [];
        var values = new List<(string Kind, string Value)>(6);
        Add(values, AccountRiskSignalKind.DeviceConfig, signals.DeviceConfigHash, ModuleEnabled(policy, "deviceConfigHash"));
        Add(values, AccountRiskSignalKind.DeviceId, signals.DeviceId, ModuleEnabled(policy, "deviceId"));
        AddIp(values, signals.IpAddress, (ModuleEnabled(policy, "ipAddress")) || ModuleEnabled(policy, "registrationVelocity"));
        Add(values, AccountRiskSignalKind.Network, signals.NetworkHash, ModuleEnabled(policy, "network"));
        Add(values, AccountRiskSignalKind.Person, signals.PersonKey, ModuleEnabled(policy, "accountAssociation"));
        Add(values, AccountRiskSignalKind.UserAgent, signals.UserAgent, enabled: ModuleEnabled(policy, "browserFingerprint"));

        return values
            .Select(item => new AccountRiskSignalHash(item.Kind, HashValue(item.Kind, item.Value)))
            .OrderBy(item => item.Kind, StringComparer.Ordinal)
            .ThenBy(item => item.Hash, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>计算单个归一化信号的 HMAC-SHA256 十六进制摘要。</summary>
    public string HashValue(string kind, string value)
    {
        var payload = Encoding.UTF8.GetBytes($"v1\n{kind}\n{value}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(_key, payload));
    }

    private static void Add(List<(string Kind, string Value)> values, string kind, string? raw, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(raw)) return;
        var normalized = raw.Trim();
        if (normalized.Length is 0 or > MaxSignalLength) return;
        values.Add((kind, normalized.ToUpperInvariant()));
    }

    private static void AddIp(List<(string Kind, string Value)> values, string? raw, bool enabled)
    {
        if (!enabled || string.IsNullOrWhiteSpace(raw)) return;
        var value = raw.Trim();
        if (!IPAddress.TryParse(value, out var address)) return;
        values.Add((AccountRiskSignalKind.Ip, address.ToString()));
    }

    private static bool ModuleEnabled(AccountRiskPolicy policy, string id) =>
        policy.Modules.Single(module => module.Id == id).Enabled;
}
