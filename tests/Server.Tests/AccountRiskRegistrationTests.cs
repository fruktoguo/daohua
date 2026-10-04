using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>注册风控的真实事务边界：配额、并发、动态开关与拒绝审计。</summary>
[Collection("PostgresIntegration")]
public sealed class AccountRiskRegistrationTests
{
    /// <summary>已有注册关联的设备不能再次注册，拒绝不会写入第二份账号关联。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SameDeviceRejectsSecondRegistration()
    {
        await using var fixture = await OpenRepositoryAsync();
        var signals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"));

        var first = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, signals);
        var second = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, signals);

        Assert.True(first.Success);
        Assert.False(second.Success);
        Assert.Equal(AccountRiskErrorCodes.DeviceAlreadyRegistered, second.ErrorCode);
        Assert.Equal(1, await CountAssociatedAccountsAsync(fixture, signals, AccountRiskSignalKind.DeviceId));
    }

    /// <summary>同一身份可关联两个不同设备的账号，第三次注册必须拒绝。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SamePersonRejectsThirdRegistration()
    {
        await using var fixture = await OpenRepositoryAsync();
        var person = Guid.NewGuid().ToString("N");
        var results = new List<AccountRegistrationResult>();
        for (var index = 0; index < 3; index++)
        {
            var signals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"), PersonKey: person);
            results.Add(await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, signals));
        }

        Assert.True(results[0].Success);
        Assert.True(results[1].Success);
        Assert.False(results[2].Success);
        Assert.Equal(AccountRiskErrorCodes.PersonAccountLimit, results[2].ErrorCode);
        Assert.Equal(2, await CountAssociatedAccountsAsync(
            fixture, new AccountRiskSignals(PersonKey: person), AccountRiskSignalKind.Person));
    }

    /// <summary>两个请求同时争用尚无关联行的设备时，事务锁保证只有一份注册成功。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ConcurrentSameDeviceCreatesOnlyOneAccount()
    {
        await using var fixture = await OpenRepositoryAsync();
        var signals = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registrations = Enumerable.Range(0, 2).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, signals);
        })).ToArray();

        start.SetResult();
        var results = await Task.WhenAll(registrations);

        Assert.Single(results, result => result.Success);
        var rejected = Assert.Single(results, result => !result.Success);
        Assert.Equal(AccountRiskErrorCodes.DeviceAlreadyRegistered, rejected.ErrorCode);
        Assert.Equal(1, await CountAssociatedAccountsAsync(fixture, signals, AccountRiskSignalKind.DeviceId));
    }

    /// <summary>运行中关闭设备配置模块后，同配置不同设备的下一次注册立即放行。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DisabledDeviceConfigModuleStopsApplyingItsQuota()
    {
        await using var fixture = await OpenRepositoryAsync();
        var deviceConfig = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString("N"))));
        var firstSignals = new AccountRiskSignals(DeviceConfigHash: deviceConfig, DeviceId: Guid.NewGuid().ToString("N"));
        var first = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, firstSignals);
        Assert.True(first.Success);

        var current = fixture.Registry.Current;
        fixture.Registry.Replace(current with
        {
            Modules = current.Modules.Select(module => module.Id == "deviceConfigHash"
                ? module with { Enabled = false }
                : module).ToArray()
        });
        var secondSignals = firstSignals with { DeviceId = Guid.NewGuid().ToString("N") };
        var second = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, secondSignals);

        Assert.True(second.Success);
        Assert.DoesNotContain(fixture.Hasher.Hash(secondSignals, fixture.Registry.Current),
            signal => signal.Kind == AccountRiskSignalKind.DeviceConfig);
    }

    /// <summary>缺少设备信号的拒绝也落库，防止无信号绕过尝试在运维审计中消失。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task MissingSignalsStillPersistDenyAudit()
    {
        await using var fixture = await OpenRepositoryAsync();
        var before = await CountMissingSignalDenialsAsync(fixture.Source);

        var result = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null, null);

        Assert.False(result.Success);
        Assert.Equal(AccountRiskErrorCodes.RiskSignalRequired, result.ErrorCode);
        Assert.Equal(before + 1, await CountMissingSignalDenialsAsync(fixture.Source));
    }

    /// <summary>软关联命中复核阈值时仍允许注册，并准确保存复核分数和原因。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task SoftIpRiskPersistsReviewScoreAndReasons()
    {
        await using var fixture = await OpenRepositoryAsync();
        var current = fixture.Registry.Current;
        fixture.Registry.Replace(current with
        {
            RiskThreshold = 80,
            HighRiskAction = "review",
            Modules = current.Modules.Select(module => module.Id switch
            {
                "ipAddress" => module with { Enabled = true, Weight = 85, Threshold = 1 },
                "network" or "browserFingerprint" or "registrationVelocity" => module with { Enabled = false },
                _ => module
            }).ToArray()
        });
        var ip = new System.Net.IPAddress(Guid.NewGuid().ToByteArray()).ToString();
        var first = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null,
            new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"), IpAddress: ip));
        var second = await fixture.Repository.RegisterAsync(UniqueAccount(), "risk-pass-1", null,
            new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"), IpAddress: ip));
        Assert.True(first.Success);
        Assert.True(second.Success);

        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT decision, risk_score, reasons::text FROM identity.account_risk_events
            WHERE account_id = $1 AND event_kind = 'register'
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = second.AccountId!.Value });
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("review", reader.GetString(0));
        Assert.Equal(85, reader.GetInt32(1));
        using var reasons = JsonDocument.Parse(reader.GetString(2));
        Assert.NotEmpty(reasons.RootElement.EnumerateArray());
    }

    private static string UniqueAccount() => $"risk_{Guid.NewGuid():N}"[..24];

    private static async Task<int> CountAssociatedAccountsAsync(
        RiskFixture fixture,
        AccountRiskSignals signals,
        string kind)
    {
        var signal = Assert.Single(fixture.Hasher.Hash(signals, fixture.Registry.Current), value => value.Kind == kind);
        await using var connection = await fixture.Source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(DISTINCT account_id)
            FROM identity.account_risk_signals
            WHERE signal_kind = $1 AND signal_hash = $2
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = kind });
        command.Parameters.Add(new NpgsqlParameter { Value = signal.Hash });
        return Convert.ToInt32(await command.ExecuteScalarAsync());
    }

    private static async Task<long> CountMissingSignalDenialsAsync(NpgsqlDataSource source)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM identity.account_risk_events
            WHERE account_id IS NULL AND decision = 'deny' AND error_code = $1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = AccountRiskErrorCodes.RiskSignalRequired });
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<RiskFixture> OpenRepositoryAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("注册风控集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = new ServerOptions(
            "test", connectionString, 128,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, EnableSchemaBootstrap: false);
        var registry = new AccountRiskPolicyRegistry();
        var hasher = new AccountRiskHasher("account-risk-registration-tests");
        var repository = new PostgresAccountRepository(
            source, options, NullLogger<PostgresAccountRepository>.Instance, new PostgresOutboxRepository(source), registry, hasher);
        return new RiskFixture(source, repository, registry, hasher);
    }

    private sealed record RiskFixture(
        NpgsqlDataSource Source,
        PostgresAccountRepository Repository,
        AccountRiskPolicyRegistry Registry,
        AccountRiskHasher Hasher) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Source.DisposeAsync();
    }
}
