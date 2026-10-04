using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>账号检测跨边界回归：策略持久化、运维读模型、登录关联与生产摘要密钥。</summary>
[Collection("PostgresIntegration")]
public sealed class AccountRiskBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>保存的策略必须能被全新的仓储实例读回，并保留模块状态与数值。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SavedPolicyLoadsIntoNewRepository()
    {
        await using var fixture = await OpenFixtureAsync();
        var saved = AccountRiskPolicy.Default with
        {
            RegistrationEnabled = false,
            RiskThreshold = 73,
            HighRiskAction = "review",
            Modules = AccountRiskPolicy.Default.Modules.Select(module => module.Id switch
            {
                "network" => module with { Enabled = false },
                "registrationVelocity" => module with { Weight = 42, Threshold = 7 },
                _ => module
            }).ToArray()
        };

        await fixture.Risk.ReplacePolicyAsync(saved);
        var restarted = new PostgresAccountRiskRepository(fixture.Source, new AccountRiskPolicyRegistry(), fixture.Hasher);
        var loaded = await restarted.LoadPolicyAsync();

        Assert.False(loaded.RegistrationEnabled);
        Assert.Equal(saved.RiskThreshold, loaded.RiskThreshold);
        Assert.Equal(saved.HighRiskAction, loaded.HighRiskAction);
        Assert.Equal(saved.Modules.ToArray(), loaded.Modules.ToArray());
    }

    /// <summary>运维查询实际注册事件和关联，覆盖 JSON 反序列化、SQL 列与 reader 类型。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OpsOverviewReadsActualEventsAndAssociations()
    {
        await using var fixture = await OpenFixtureAsync();
        var policy = AccountRiskPolicy.Default with
        {
            RiskThreshold = 80,
            HighRiskAction = "review",
            Modules = AccountRiskPolicy.Default.Modules.Select(module => module.Id switch
            {
                "ipAddress" => module with { Weight = 85, Threshold = 1 },
                "registrationVelocity" => module with { Enabled = false },
                _ => module
            }).ToArray()
        };
        await fixture.Risk.ReplacePolicyAsync(policy);
        var person = Guid.NewGuid().ToString("N");
        var sharedNetwork = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var sharedIp = new IPAddress(Guid.NewGuid().ToByteArray()).ToString();
        var firstSignals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"),
            IpAddress: sharedIp, NetworkHash: sharedNetwork, PersonKey: person);
        var secondSignals = firstSignals with { DeviceId = Guid.NewGuid().ToString("N") };
        var first = await fixture.Accounts.RegisterAsync(UniqueAccount(), "boundary-pass-1", null, firstSignals);
        var second = await fixture.Accounts.RegisterAsync(UniqueAccount(), "boundary-pass-1", null, secondSignals);
        Assert.True(first.Success);
        Assert.True(second.Success);

        var overview = JsonSerializer.SerializeToElement(await fixture.Risk.GetOpsOverviewAsync(), JsonOptions);
        var accountKey = second.AccountId!.Value.ToString("N")[..12];
        var accountEvent = Assert.Single(overview.GetProperty("recentEvents").EnumerateArray(),
            item => item.GetProperty("accountKey").GetString() == accountKey);
        Assert.Equal("register", accountEvent.GetProperty("kind").GetString());
        Assert.Equal("review", accountEvent.GetProperty("action").GetString());
        Assert.Equal(85, accountEvent.GetProperty("riskScore").GetInt32());
        Assert.NotEmpty(accountEvent.GetProperty("reasons").EnumerateArray());
        var hashes = fixture.Hasher.Hash(secondSignals, policy);
        Assert.Equal(HashPrefix(hashes, AccountRiskSignalKind.DeviceId), accountEvent.GetProperty("deviceKey").GetString());
        Assert.Equal(HashPrefix(hashes, AccountRiskSignalKind.Network), accountEvent.GetProperty("networkKey").GetString());

        var personKey = $"{AccountRiskSignalKind.Person}:{HashPrefix(hashes, AccountRiskSignalKind.Person)}";
        var association = Assert.Single(overview.GetProperty("associations").EnumerateArray(),
            item => item.GetProperty("associationKey").GetString() == personKey);
        Assert.Equal(2, association.GetProperty("accountCount").GetInt32());
        Assert.Equal(2, association.GetProperty("deviceCount").GetInt32());
        Assert.Equal(1, association.GetProperty("networkCount").GetInt32());
        Assert.Equal(85, association.GetProperty("maxRiskScore").GetInt32());
        Assert.Equal(new[] { first.AccountId!.Value.ToString("N")[..12], accountKey }.Order(StringComparer.Ordinal),
            association.GetProperty("accountKeys").EnumerateArray().Select(item => item.GetString()!).Order(StringComparer.Ordinal));
        Assert.True(overview.GetProperty("stats").GetProperty("reviewedToday").GetInt64() >= 1);
        Assert.NotEqual(JsonValueKind.Null, overview.GetProperty("policy").GetProperty("updatedAt").ValueKind);
        Assert.DoesNotContain(sharedIp, overview.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(person, overview.GetRawText(), StringComparison.Ordinal);
    }

    /// <summary>同一主体登录最多关联两个账号，已关联账号重复登录不消耗额外额度。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task LoginAssociationAllowsTwoAccountsAndDoesNotCountRepeats()
    {
        await using var fixture = await OpenFixtureAsync();
        await fixture.Risk.ReplacePolicyAsync(AccountRiskPolicy.Default);
        var accounts = new List<Guid>();
        for (var index = 0; index < 3; index++)
        {
            var registered = await fixture.Accounts.RegisterAsync(UniqueAccount(), "boundary-pass-1", null,
                new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N")));
            Assert.True(registered.Success);
            accounts.Add(registered.AccountId!.Value);
        }
        var loginSignals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"), PersonKey: Guid.NewGuid().ToString("N"));

        Assert.True((await fixture.Risk.ObserveLoginAsync(accounts[0], loginSignals, eventKind: "handshake")).Allowed);
        Assert.True((await fixture.Risk.ObserveLoginAsync(accounts[0], loginSignals)).Allowed);
        Assert.True((await fixture.Risk.ObserveLoginAsync(accounts[1], loginSignals)).Allowed);
        Assert.True((await fixture.Risk.ObserveLoginAsync(accounts[0], loginSignals)).Allowed);
        var rejected = await fixture.Risk.ObserveLoginAsync(accounts[2], loginSignals);

        Assert.False(rejected.Allowed);
        Assert.Equal(AccountRiskErrorCodes.PersonAccountLimit, rejected.ErrorCode);
        var personHash = Assert.Single(fixture.Hasher.Hash(loginSignals, AccountRiskPolicy.Default),
            item => item.Kind == AccountRiskSignalKind.Person).Hash;
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT array_agg(DISTINCT account_id) FROM identity.account_risk_signals
            WHERE signal_kind = $1 AND signal_hash = $2
            """, connection);
        command.Parameters.AddWithValue(AccountRiskSignalKind.Person);
        command.Parameters.AddWithValue(personHash);
        var associated = (Guid[])(await command.ExecuteScalarAsync())!;
        Assert.Equal(accounts.Take(2).Order(), associated.Order());
    }

    /// <summary>生产随机 HMAC 密钥写库后，新数据源与新实例仍产生相同摘要。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ProductionHasherRetainsKeyAcrossNewDataSource()
    {
        await using var fixture = await OpenFixtureAsync();
        var signals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"), IpAddress: "192.0.2.47");
        var before = fixture.Hasher.Hash(signals, AccountRiskPolicy.Default);
        await using var restartedSource = NpgsqlDataSource.Create(PostgresTestSchema.RequireConnectionString("生产账号风险密钥回归"));
        var restarted = await AccountRiskHasher.CreateAsync(restartedSource, CancellationToken.None);

        Assert.Equal(before, restarted.Hash(signals, AccountRiskPolicy.Default));
        await using var connection = await restartedSource.OpenConnectionAsync();
        await using var count = new NpgsqlCommand("SELECT count(*) FROM platform.account_risk_key", connection);
        Assert.Equal(1L, Convert.ToInt64(await count.ExecuteScalarAsync()));
    }

    /// <summary>同一 IPv6 的展开与压缩写法不会拆成两份风险信号。</summary>
    [Fact]
    public void HasherNormalizesEquivalentIpv6Representations()
    {
        var hasher = new AccountRiskHasher("account-risk-ip-boundary");
        var expanded = hasher.Hash(new AccountRiskSignals(IpAddress: "2001:0DB8:0000:0000:0000:0000:0000:0047"), AccountRiskPolicy.Default);
        var compressed = hasher.Hash(new AccountRiskSignals(IpAddress: "2001:db8::47"), AccountRiskPolicy.Default);

        Assert.Equal(expanded, compressed);
    }

    /// <summary>可信请求边界将 IPv4-mapped 来源与普通 IPv4 归为同一 IP 和网段。</summary>
    [Fact]
    public void RequestContextNormalizesMappedIpv4Source()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var plain = new DefaultHttpContext();
        plain.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.47");
        var mapped = new DefaultHttpContext();
        mapped.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:192.0.2.47");

        var plainSignals = collector.Collect(plain, null, persistCookie: false);
        var mappedSignals = collector.Collect(mapped, null, persistCookie: false);

        Assert.Equal(plainSignals.IpAddress, mappedSignals.IpAddress);
        Assert.Equal(plainSignals.NetworkHash, mappedSignals.NetworkHash);
    }

    /// <summary>空客户端对象不能得到随机设备额度；已验证的设备 Cookie 仍能恢复真实锚点。</summary>
    [Fact]
    public void EmptyClientCannotInventDeviceAndProtectedCookieRestoresAnchor()
    {
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var empty = new DefaultHttpContext();
        var missing = collector.Collect(empty, new(null, null, null, null));
        Assert.Null(missing.DeviceId);
        Assert.Null(missing.PersonKey);
        Assert.False(empty.Response.Headers.ContainsKey("Set-Cookie"));

        var original = new DefaultHttpContext();
        var id = Guid.NewGuid().ToString("D");
        collector.Collect(original, new(null, id, null, null));
        var restored = new DefaultHttpContext();
        restored.Request.Headers.Cookie = original.Response.Headers.SetCookie.ToString().Split(';')[0];
        var actual = collector.Collect(restored, new(null, Guid.NewGuid().ToString("D"), null, null), false);
        Assert.Equal(id, actual.DeviceId);
        Assert.Equal($"device:{id}", actual.PersonKey);
    }

    /// <summary>配置相同的不同真机不会共用硬注册额度，缺信号拒绝也不污染共享 IP 注册速度。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SameHardwareConfigurationIsSoftAndDeniedAttemptsDoNotBlockSharedIp()
    {
        await using var fixture = await OpenFixtureAsync();
        var policy = AccountRiskPolicy.Default with
        {
            Modules = AccountRiskPolicy.Default.Modules.Select(module => module.Id == "registrationVelocity"
                ? module with { Threshold = 2, Weight = 100 } : module).ToArray()
        };
        await fixture.Risk.ReplacePolicyAsync(policy);
        var collector = new AccountRiskRequestContext(new EphemeralDataProtectionProvider());
        var config = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var ip = new IPAddress(Guid.NewGuid().ToByteArray());
        for (var index = 0; index < 3; index++)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = ip;
            var missing = collector.Collect(context, new(null, null, null, null), false);
            var rejected = await fixture.Accounts.RegisterAsync(UniqueAccount(), "boundary-pass-1", null, missing);
            Assert.Equal(AccountRiskErrorCodes.RiskSignalRequired, rejected.ErrorCode);
        }
        for (var index = 0; index < 2; index++)
        {
            var context = new DefaultHttpContext();
            context.Connection.RemoteIpAddress = ip;
            var signals = collector.Collect(context, new(config, Guid.NewGuid().ToString("D"), null, null), false);
            var result = await fixture.Accounts.RegisterAsync(UniqueAccount(), "boundary-pass-1", null, signals);
            Assert.True(result.Success, result.ErrorCode);
        }
    }

    /// <summary>高频风险事件按独立保留期清理，长期业务审计配置不会使其积累十年。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RiskRetentionIsIndependentAndKeepsStableKey()
    {
        await using var fixture = await OpenFixtureAsync();
        var now = DateTimeOffset.UtcNow;
        var oldId = Guid.NewGuid();
        var recentId = Guid.NewGuid();
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO identity.account_risk_events(event_id,decision,signals,risk_score,reasons,event_kind,occurred_at)
            VALUES ($1,'allow','[]',0,'[]','handshake',$3),($2,'allow','[]',0,'[]','handshake',$4)
            """, connection))
        {
            insert.Parameters.AddWithValue(oldId);
            insert.Parameters.AddWithValue(recentId);
            insert.Parameters.AddWithValue(now.AddDays(-40));
            insert.Parameters.AddWithValue(now.AddDays(-2));
            await insert.ExecuteNonQueryAsync();
        }
        var retention = new PostgresRetentionRepository(fixture.Source,
            fixture.Options with { AuditRetention = TimeSpan.FromDays(3650), AccountRiskEventRetention = TimeSpan.FromDays(30) },
            NullLogger<PostgresRetentionRepository>.Instance);
        Assert.True((await retention.PruneAsync(now)).AccountRiskEvents >= 1);
        await using var remaining = new NpgsqlCommand("SELECT event_id FROM identity.account_risk_events WHERE event_id IN ($1,$2)", connection);
        remaining.Parameters.AddWithValue(oldId);
        remaining.Parameters.AddWithValue(recentId);
        Assert.Equal(recentId, await remaining.ExecuteScalarAsync());
        await using var key = new NpgsqlCommand("SELECT count(*) FROM platform.account_risk_key", connection);
        Assert.Equal(1L, await key.ExecuteScalarAsync());
    }

    private static string UniqueAccount() => $"boundary_{Guid.NewGuid():N}"[..28];
    private static string HashPrefix(IReadOnlyList<AccountRiskSignalHash> hashes, string kind) =>
        Assert.Single(hashes, item => item.Kind == kind).Hash[..12];

    private static async Task<RiskFixture> OpenFixtureAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("账号检测跨边界集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var registry = new AccountRiskPolicyRegistry();
        var hasher = await AccountRiskHasher.CreateAsync(source, CancellationToken.None);
        var options = new ServerOptions("test", connectionString, 128,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, EnableSchemaBootstrap: false);
        return new RiskFixture(source, hasher,
            new PostgresAccountRepository(source, options, NullLogger<PostgresAccountRepository>.Instance,
                new PostgresOutboxRepository(source), registry, hasher),
            new PostgresAccountRiskRepository(source, registry, hasher), options);
    }

    private sealed record RiskFixture(
        NpgsqlDataSource Source,
        AccountRiskHasher Hasher,
        PostgresAccountRepository Accounts,
        PostgresAccountRiskRepository Risk,
        ServerOptions Options) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
