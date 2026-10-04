using System.Text.Json;
using Npgsql;

namespace Server.Clean.AccountAbuse;

/// <summary>账号检测策略、脱敏关联与审计的 PostgreSQL 权威存储。</summary>
public sealed class PostgresAccountRiskRepository(
    NpgsqlDataSource dataSource,
    AccountRiskPolicyRegistry policyRegistry,
    AccountRiskHasher hasher)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    /// <summary>最近一次从数据库读取的策略快照。</summary>
    public AccountRiskPolicy CurrentPolicy => policyRegistry.Current;

    /// <summary>读取数据库中的最新策略，使运维变更跨进程生效。</summary>
    public async ValueTask<AccountRiskPolicy> LoadPolicyAsync(CancellationToken cancellationToken = default)
    {
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("SELECT payload FROM platform.account_risk_policy WHERE policy_id = 1", connection);
            var raw = await command.ExecuteScalarAsync(cancellationToken) as string;
            var policy = raw is null ? AccountRiskPolicy.Default : JsonSerializer.Deserialize<AccountRiskPolicy>(raw, JsonOptions)
                ?? throw new InvalidOperationException("账号检测策略载荷无效。");
            policy.Validate();
            policyRegistry.Replace(policy);
            return policy;
        }
        finally
        {
            _policyGate.Release();
        }
    }

    /// <summary>原子保存策略并记录不含敏感值的管理审计。</summary>
    public async ValueTask ReplacePolicyAsync(AccountRiskPolicy policy, CancellationToken cancellationToken = default)
    {
        policy.Validate();
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand("""
                INSERT INTO platform.account_risk_policy(policy_id, payload, updated_at)
                VALUES (1, $1::jsonb, now()) ON CONFLICT (policy_id)
                DO UPDATE SET payload = EXCLUDED.payload, updated_at = EXCLUDED.updated_at
                """, connection, transaction);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(policy, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await using var audit = new NpgsqlCommand("""
                INSERT INTO identity.audit_events(event_id,event_type,payload,occurred_at)
                VALUES ($1,'account-risk.policy.updated',$2::jsonb,now())
                """, connection, transaction);
            audit.Parameters.AddWithValue(Guid.NewGuid());
            audit.Parameters.AddWithValue(JsonSerializer.Serialize(policy, JsonOptions));
            await audit.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            policyRegistry.Replace(policy);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    /// <summary>供运维面板读取可编辑策略和持久化更新时间。</summary>
    public async ValueTask<object> GetOpsOverviewAsync(CancellationToken cancellationToken = default)
    {
        var policy = await LoadPolicyAsync(cancellationToken);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var summary = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM identity.accounts),
              (SELECT count(DISTINCT signal_hash) FROM identity.account_risk_signals WHERE signal_kind = 'person'),
              (SELECT count(DISTINCT signal_hash) FROM identity.account_risk_signals WHERE signal_kind = 'device_id'),
              (SELECT count(*) FROM identity.accounts WHERE created_at >= date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'),
              count(*) FILTER (WHERE decision = 'allow'), count(*) FILTER (WHERE decision = 'review'),
              count(*) FILTER (WHERE decision = 'deny'), count(*) FILTER (WHERE risk_score >= $1), count(*)
            FROM identity.account_risk_events
            WHERE occurred_at >= date_trunc('day', now() AT TIME ZONE 'UTC') AT TIME ZONE 'UTC'
            """, connection);
        summary.Parameters.AddWithValue(policy.RiskThreshold);
        AccountRiskStats stats;
        await using (var reader = await summary.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            stats = new(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3),
                reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetInt64(8));
        }
        var events = new List<AccountRiskEventSummary>();
        await using var eventCommand = new NpgsqlCommand("""
            SELECT event_id, occurred_at, decision, risk_score, account_id, signals, reasons, event_kind
            FROM identity.account_risk_events ORDER BY occurred_at DESC LIMIT 100
            """, connection);
        await using (var reader = await eventCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var signals = JsonSerializer.Deserialize<AccountRiskSignalHash[]>(reader.GetString(5), JsonOptions)!;
                var reasons = JsonSerializer.Deserialize<string[]>(reader.GetString(6), JsonOptions)!;
                events.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(7), reader.GetString(2),
                    reader.GetInt32(3), reader.IsDBNull(4) ? null : reader.GetGuid(4).ToString("N")[..12],
                    Prefix(signals, AccountRiskSignalKind.DeviceId) ?? Prefix(signals, AccountRiskSignalKind.DeviceConfig),
                    Prefix(signals, AccountRiskSignalKind.Network) ?? Prefix(signals, AccountRiskSignalKind.Ip), reasons));
            }
        }
        var associations = new List<AccountRiskAssociationSummary>();
        await using var associationCommand = new NpgsqlCommand("""
            SELECT s.signal_kind || ':' || left(s.signal_hash,12), count(DISTINCT s.account_id),
                   array_agg(DISTINCT left(replace(s.account_id::text,'-',''),12)), max(s.last_seen_at),
                   (SELECT count(DISTINCT x.signal_hash) FROM identity.account_risk_signals x
                    WHERE x.account_id IN (SELECT y.account_id FROM identity.account_risk_signals y WHERE y.signal_kind=s.signal_kind AND y.signal_hash=s.signal_hash)
                      AND x.signal_kind='device_id'),
                   (SELECT count(DISTINCT x.signal_hash) FROM identity.account_risk_signals x
                    WHERE x.account_id IN (SELECT y.account_id FROM identity.account_risk_signals y WHERE y.signal_kind=s.signal_kind AND y.signal_hash=s.signal_hash)
                      AND x.signal_kind='network'),
                   COALESCE((SELECT max(e.risk_score) FROM identity.account_risk_events e
                    WHERE e.account_id IN (SELECT y.account_id FROM identity.account_risk_signals y WHERE y.signal_kind=s.signal_kind AND y.signal_hash=s.signal_hash)),0)
            FROM identity.account_risk_signals s GROUP BY s.signal_kind,s.signal_hash
            HAVING count(DISTINCT s.account_id) > 1 ORDER BY max(s.last_seen_at) DESC LIMIT 100
            """, connection);
        await using (var reader = await associationCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                associations.Add(new(reader.GetString(0), checked((int)reader.GetInt64(1)), reader.GetFieldValue<string[]>(2),
                    checked((int)reader.GetInt64(4)), checked((int)reader.GetInt64(5)), reader.GetInt32(6), reader.GetFieldValue<DateTimeOffset>(3)));
        }
        await using var updated = new NpgsqlCommand("SELECT updated_at FROM platform.account_risk_policy WHERE policy_id=1", connection);
        var updatedAt = await updated.ExecuteScalarAsync(cancellationToken);
        return new { policy = new { policy.Enabled, policy.RequireDeviceSignal, policy.RegistrationEnabled, policy.MaxAccountsPerPerson, policy.MaxAccountsPerDevice,
            policy.RiskThreshold, policy.HighRiskAction, policy.Modules, updatedAt }, stats, recentEvents = events, associations };
    }

    /// <summary>记录无法正规化的外部设备信息拒绝，不保存原始信号。</summary>
    public async ValueTask RecordInvalidSignalsAsync(string eventKind, Guid? accountId, CancellationToken cancellationToken = default)
    {
        var decision = new AccountRiskDecision(false, "INVALID_RISK_SIGNALS", "设备校验信息无效。", [], 100, "deny", ["设备信息无效"]);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await AppendEventAsync(connection, transaction, accountId, decision, DateTimeOffset.UtcNow, cancellationToken, eventKind);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>认证成功后关联设备主体；握手可传入已读取的策略，确保观测与准入使用同一快照。</summary>
    public async ValueTask<AccountRiskDecision> ObserveLoginAsync(Guid accountId, AccountRiskSignals signals,
        CancellationToken cancellationToken = default, AccountRiskPolicy? policySnapshot = null, string eventKind = "login")
    {
        var policy = policySnapshot ?? await LoadPolicyAsync(cancellationToken);
        var hashes = hasher.Hash(signals, policy);
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var decision = await EvaluateAsync(connection, transaction, accountId, hashes, policy, cancellationToken, registration: false);
        if (decision.Allowed) await PersistSignalsAsync(connection, transaction, accountId, hashes, DateTimeOffset.UtcNow, cancellationToken);
        await AppendEventAsync(connection, transaction, accountId, decision, DateTimeOffset.UtcNow, cancellationToken, eventKind);
        await transaction.CommitAsync(cancellationToken);
        return decision;
    }

    /// <summary>按固定顺序持有信号锁，等待后在 ReadCommitted 快照中检查已提交的额度。</summary>
    internal static async ValueTask<AccountRiskDecision> EvaluateAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Guid accountId, IReadOnlyList<AccountRiskSignalHash> signals, AccountRiskPolicy policy, CancellationToken cancellationToken, bool registration = true)
    {
        if (!policy.Enabled || (registration && !policy.RegistrationEnabled)) return new(true, null, null, signals);
        foreach (var signal in signals.OrderBy(item => item.Kind, StringComparer.Ordinal).ThenBy(item => item.Hash, StringComparer.Ordinal))
        {
            await using var command = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended($1::text,0))", connection, transaction);
            command.Parameters.AddWithValue($"{signal.Kind}:{signal.Hash}");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var deviceSignals = signals.Where(item => item.Kind is AccountRiskSignalKind.DeviceConfig or AccountRiskSignalKind.DeviceId).ToArray();
        // 所有设备模块关闭时不会因为必填规则让运维无法主动停用检测。
        var idEnabled = policy.Modules.Single(item => item.Id == "deviceId").Enabled;
        var configEnabled = policy.Modules.Single(item => item.Id == "deviceConfigHash").Enabled;
        var hasRequiredDevice = deviceSignals.Any(item => item.Kind == (idEnabled ? AccountRiskSignalKind.DeviceId : AccountRiskSignalKind.DeviceConfig));
        if (policy.RequireDeviceSignal && (idEnabled || configEnabled) && !hasRequiredDevice)
            return Deny(AccountRiskErrorCodes.RiskSignalRequired, "设备校验失败，请刷新后重试。", signals, "设备信号缺失");
        if (registration)
        {
            foreach (var signal in deviceSignals.Where(item => item.Kind == AccountRiskSignalKind.DeviceId))
                if (await CountAccountsAsync(connection, transaction, signal, accountId, cancellationToken) >= policy.MaxAccountsPerDevice)
                    return Deny(AccountRiskErrorCodes.DeviceAlreadyRegistered, "该设备已达到可注册账号上限。", signals, "设备注册上限");
        }
        var person = signals.FirstOrDefault(item => item.Kind == AccountRiskSignalKind.Person);
        if (person is not null && await CountAccountsAsync(connection, transaction, person, accountId, cancellationToken) >= policy.MaxAccountsPerPerson)
            return Deny(AccountRiskErrorCodes.PersonAccountLimit, "该设备关联的账号数量已达上限。", signals, "关联账号上限");
        if (!registration && policy.Modules.Single(item => item.Id == "accountAssociation").Enabled)
        {
            foreach (var device in deviceSignals.Where(item => item.Kind == AccountRiskSignalKind.DeviceId))
                if (await CountAccountsAsync(connection, transaction, device, accountId, cancellationToken) >= policy.MaxAccountsPerPerson)
                    return Deny(AccountRiskErrorCodes.PersonAccountLimit, "该设备关联的账号数量已达上限。", signals, "关联账号上限");
        }
        var score = 0;
        var reasons = new List<string>();
        foreach (var signal in signals.Where(item => item.Kind is AccountRiskSignalKind.DeviceConfig or AccountRiskSignalKind.Ip or AccountRiskSignalKind.Network or AccountRiskSignalKind.UserAgent))
        {
            var id = signal.Kind switch { AccountRiskSignalKind.DeviceConfig => "deviceConfigHash", AccountRiskSignalKind.Ip => "ipAddress", AccountRiskSignalKind.Network => "network", _ => "browserFingerprint" };
            var module = policy.Modules.Single(item => item.Id == id);
            var count = await CountAccountsAsync(connection, transaction, signal, accountId, cancellationToken);
            if (module.Enabled && count >= module.Threshold)
            {
                score += module.Weight;
                reasons.Add($"{module.Name}关联过多");
            }
        }
        var velocity = policy.Modules.Single(item => item.Id == "registrationVelocity");
        var ip = signals.FirstOrDefault(item => item.Kind == AccountRiskSignalKind.Ip);
        if (registration && velocity.Enabled && ip is not null)
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM identity.account_risk_events WHERE occurred_at >= now()-interval '1 hour'
                AND event_kind='register' AND decision IN ('allow','review') AND signals @> $1::jsonb
                """, connection, transaction);
            command.Parameters.AddWithValue(JsonSerializer.Serialize(new[] { ip }, JsonOptions));
            if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) >= velocity.Threshold)
            {
                score += velocity.Weight;
                reasons.Add("注册过于频繁");
            }
        }
        score = Math.Min(100, score);
        var action = score >= policy.RiskThreshold ? policy.HighRiskAction : "allow";
        return new(action != "deny", action == "deny" ? "RISK_SCORE_TOO_HIGH" : null,
            action == "deny" ? (registration ? "注册受限，请稍后重试。" : "账号使用受限，请稍后重试。") : null, signals, score, action, reasons);
    }

    /// <summary>把账号关联与账号创建放在同一事务内。</summary>
    internal static async ValueTask PersistSignalsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid accountId,
        IReadOnlyList<AccountRiskSignalHash> signals, DateTimeOffset now, CancellationToken cancellationToken)
    {
        foreach (var signal in signals)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO identity.account_risk_signals(signal_id,account_id,signal_kind,signal_hash,first_seen_at,last_seen_at,hit_count)
                VALUES ($1,$2,$3,$4,$5,$5,1) ON CONFLICT (account_id,signal_kind,signal_hash)
                DO UPDATE SET last_seen_at=EXCLUDED.last_seen_at,hit_count=identity.account_risk_signals.hit_count+1
                """, connection, transaction);
            command.Parameters.AddWithValue(Guid.NewGuid()); command.Parameters.AddWithValue(accountId);
            command.Parameters.AddWithValue(signal.Kind); command.Parameters.AddWithValue(signal.Hash); command.Parameters.AddWithValue(now);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>写入脱敏判定事件；与额度锁持有同一个事务。</summary>
    internal static async ValueTask AppendEventAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid? accountId,
        AccountRiskDecision decision, DateTimeOffset now, CancellationToken cancellationToken, string eventKind = "register")
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.account_risk_events(event_id,account_id,decision,error_code,signals,risk_score,reasons,occurred_at,event_kind)
            VALUES ($1,$2,$3,$4,$5::jsonb,$6,$7::jsonb,$8,$9)
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Uuid, Value=(object?)accountId ?? DBNull.Value });
        command.Parameters.AddWithValue(decision.Action);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType=NpgsqlTypes.NpgsqlDbType.Varchar, Value=(object?)decision.ErrorCode ?? DBNull.Value });
        command.Parameters.AddWithValue(JsonSerializer.Serialize(decision.Signals, JsonOptions));
        command.Parameters.AddWithValue(decision.RiskScore); command.Parameters.AddWithValue(JsonSerializer.Serialize(decision.Reasons ?? [], JsonOptions));
        command.Parameters.AddWithValue(now); command.Parameters.AddWithValue(eventKind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static AccountRiskDecision Deny(string code, string message, IReadOnlyList<AccountRiskSignalHash> signals, string reason) =>
        new(false,code,message,signals,100,"deny",[reason]);
    private static string? Prefix(IReadOnlyList<AccountRiskSignalHash> signals, string kind) => signals.FirstOrDefault(item => item.Kind == kind)?.Hash[..12];
    private static async ValueTask<int> CountAccountsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        AccountRiskSignalHash signal, Guid accountId, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT count(DISTINCT account_id) FROM identity.account_risk_signals WHERE signal_kind=$1 AND signal_hash=$2 AND account_id<>$3",connection,transaction);
        command.Parameters.AddWithValue(signal.Kind); command.Parameters.AddWithValue(signal.Hash); command.Parameters.AddWithValue(accountId);
        return checked((int)Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)));
    }
}
