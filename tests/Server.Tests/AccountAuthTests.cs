using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Xunit;

namespace Server.Tests;

/// <summary>
/// identity.accounts 认证仓库级回归：注册/登录之外重点覆盖改密链路——
/// 旧密码校验、新密码约束、改密后其他会话吊销与当前会话豁免。依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库。
/// </summary>
[Collection("PostgresIntegration")]
public sealed class AccountAuthTests
{
    /// <summary>当前密码错误时改密被拒绝，且原密码仍可正常登录。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangePasswordRejectsWrongCurrentPassword()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var account = UniqueAccount();
        var registered = await repository.RegisterAsync(account, "old-pass-1", null, null, new AccountRiskSignals(DeviceId: account));
        Assert.True(registered.Success);

        var result = await repository.ChangePasswordAsync(
            registered.AccountId!.Value, "wrong-pass", "new-pass-2", keepSessionTokenHash: null);

        Assert.False(result.Success);
        Assert.Equal("INVALID_CREDENTIALS", result.ErrorCode);
        var relogin = await repository.LoginAsync(account, "old-pass-1", new AccountRiskSignals(DeviceId: account));
        Assert.True(relogin.Success);
        await source.DisposeAsync();
    }

    /// <summary>新密码不满足长度约束时直接拒绝，不触碰既有凭据。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangePasswordRejectsInvalidNewPassword()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var account = UniqueAccount();
        var registered = await repository.RegisterAsync(account, "old-pass-1", null, null, new AccountRiskSignals(DeviceId: account));
        Assert.True(registered.Success);

        var result = await repository.ChangePasswordAsync(
            registered.AccountId!.Value, "old-pass-1", "abc", keepSessionTokenHash: null);

        Assert.False(result.Success);
        Assert.Equal("INVALID_PASSWORD", result.ErrorCode);
        await source.DisposeAsync();
    }

    /// <summary>
    /// 改密成功后：旧密码登录失败、新密码可登录；
    /// 未豁免的存活会话被吊销，keepSessionTokenHash 指向的当前会话继续有效。
    /// </summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ChangePasswordRevokesOtherSessionsAndKeepsCurrent()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var account = UniqueAccount();
        var registered = await repository.RegisterAsync(account, "old-pass-1", null, null, new AccountRiskSignals(DeviceId: account));
        Assert.True(registered.Success);
        var firstToken = registered.SessionToken!;
        var secondLogin = await repository.LoginAsync(account, "old-pass-1", new AccountRiskSignals(DeviceId: account));
        Assert.True(secondLogin.Success);
        var secondToken = secondLogin.SessionToken!;

        var keepHash = SHA256.HashData(Encoding.UTF8.GetBytes(firstToken));
        var result = await repository.ChangePasswordAsync(
            registered.AccountId!.Value, "old-pass-1", "new-pass-2", keepHash);

        Assert.True(result.Success);
        Assert.NotNull(await repository.ValidateSessionAsync(firstToken));
        Assert.Null(await repository.ValidateSessionAsync(secondToken));
        Assert.False((await repository.LoginAsync(account, "old-pass-1", new AccountRiskSignals(DeviceId: account))).Success);
        Assert.True((await repository.LoginAsync(account, "new-pass-2", new AccountRiskSignals(DeviceId: account))).Success);
        await source.DisposeAsync();
    }

    /// <summary>运维只读查询能正确关联玩家的真实登录名与角色自增序号。</summary>
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task OpsStorageInsights_GetPlayerIdentities_RetrievesLoginAndSeq()
    {
        var (source, repository) = await OpenRepositoryAsync();
        var account = UniqueAccount();
        var registered = await repository.RegisterAsync(account, "test-pass-1", null, null, new AccountRiskSignals(DeviceId: account));
        Assert.True(registered.Success);

        var insights = new Server.Clean.Ops.OpsStorageInsights(source);
        var identities = await insights.GetPlayerIdentitiesAsync([registered.PlayerId!.Value]);

        Assert.True(identities.TryGetValue(registered.PlayerId.Value, out var info));
        Assert.Equal(account, info.LoginName);
        Assert.True(info.PlayerSeq > 0);
        await source.DisposeAsync();
    }

    private static string UniqueAccount() => $"pwtest_{Guid.NewGuid():N}"[..16];

    private static async Task<(NpgsqlDataSource Source, PostgresAccountRepository Repository)> OpenRepositoryAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("账号认证仓库集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = new ServerOptions(
            "test", connectionString, 128,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, EnableSchemaBootstrap: false);
        return (source, new PostgresAccountRepository(
            source, options, NullLogger<PostgresAccountRepository>.Instance, new PostgresOutboxRepository(source),
            new AccountRiskPolicyRegistry(), new AccountRiskHasher("account-auth-tests")));
    }
}
