using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>登录签发事务边界：拒绝风险账号时只留下风险审计，不生成成功凭据或事件。</summary>
[Collection("PostgresIntegration")]
public sealed class AccountRiskLoginTests
{
    /// <summary>第三个账号争用已有两个关联账号的设备时，登录拒绝不能先签发再吊销会话。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task DeniedLoginDoesNotIssueSessionOrSuccessOutbox()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("登录风控集成测试");
        await using var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = new ServerOptions(
            "test", connectionString, 128,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, EnableSchemaBootstrap: false);
        var repository = new PostgresAccountRepository(
            source, options, NullLogger<PostgresAccountRepository>.Instance, new PostgresOutboxRepository(source),
            new AccountRiskPolicyRegistry(), new AccountRiskHasher("account-risk-login-tests"));
        var accounts = Enumerable.Range(0, 3).Select(_ => $"risklogin_{Guid.NewGuid():N}"[..24]).ToArray();
        var registrations = new List<AccountRegistrationResult>();
        foreach (var account in accounts)
        {
            var registration = await repository.RegisterAsync(account, "risk-pass-1", null,
                null, new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N")));
            Assert.True(registration.Success);
            registrations.Add(registration);
        }

        var sharedDevice = new AccountRiskSignals(DeviceId: Guid.NewGuid().ToString("N"));
        Assert.True((await repository.LoginAsync(accounts[0], "risk-pass-1", sharedDevice)).Success);
        Assert.True((await repository.LoginAsync(accounts[1], "risk-pass-1", sharedDevice)).Success);
        var rejectedAccountId = registrations[2].AccountId!.Value;
        var before = await ReadLoginEffectsAsync(source, rejectedAccountId);

        var rejected = await repository.LoginAsync(accounts[2], "risk-pass-1", sharedDevice);

        Assert.False(rejected.Success);
        Assert.Equal(AccountRiskErrorCodes.PersonAccountLimit, rejected.ErrorCode);
        Assert.Null(rejected.SessionToken);
        var after = await ReadLoginEffectsAsync(source, rejectedAccountId);
        Assert.Equal(before.Sessions, after.Sessions);
        Assert.Equal(before.SuccessEvents, after.SuccessEvents);
        Assert.Equal(before.Denials + 1, after.Denials);
    }

    private static async Task<(long Sessions, long SuccessEvents, long Denials)> ReadLoginEffectsAsync(
        NpgsqlDataSource source, Guid accountId)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT
              (SELECT count(*) FROM identity.auth_sessions WHERE account_id = $1),
              (SELECT count(*) FROM platform.integration_outbox WHERE aggregate_id = $1 AND event_type = 'auth.account.login'),
              (SELECT count(*) FROM identity.account_risk_events WHERE account_id = $1 AND event_kind = 'login' AND decision = 'deny')
            """, connection);
        command.Parameters.AddWithValue(accountId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
    }
}
