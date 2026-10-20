using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Server.Clean;
using Server.Clean.AccountAbuse;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

/// <summary>
/// 邀请系统仓储级回归（social.player_invitations + identity.players.invite_code）：
/// 注册结成奖励、无效邀请码拒绝、练气/筑基里程碑幂等发放与总览投影。
/// 依赖 DAOHUA_MARKET_TEST_CONNECTION 指向的专用库。
/// </summary>
[Collection("PostgresIntegration")]
[Trait("Category", "PostgresIntegration")]
public sealed class InvitationTests
{
    /// <summary>有效邀请码注册：同一事务建立关系并向双方发注册奖励（被邀请者功德+灵石，邀请者功德）。</summary>
    [Fact]
    public async Task RegisterWithValidInvitationCodeGrantsBothSides()
    {
        var (source, accounts, invitations, assets) = await OpenRepositoriesAsync();
        var inviter = await RegisterAsync(source, accounts);
        var inviteeResult = await RegisterAsync(accounts, inviter.InviteCode);

        Assert.True(inviteeResult.Success);
        Assert.Equal(inviter.Result.PlayerId, inviteeResult.InviterPlayerId);

        var inviteeBalances = await LoadBalancesAsync(assets, inviteeResult.PlayerId!.Value);
        Assert.Equal(InvitationRewards.SignupInviteeMerit, inviteeBalances[InvitationRewards.BoundMeritItemId]);
        Assert.Equal(InvitationRewards.SignupInviteeSpiritStones, inviteeBalances[InvitationRewards.SpiritStoneItemId]);

        var inviterBalances = await LoadBalancesAsync(assets, inviter.Result.PlayerId!.Value);
        Assert.Equal(InvitationRewards.SignupInviterMerit, inviterBalances[InvitationRewards.BoundMeritItemId]);

        var overview = await invitations.GetOverviewAsync(inviter.Result.PlayerId.Value);
        Assert.NotNull(overview);
        var row = Assert.Single(overview!.Invitees);
        Assert.Equal(inviteeResult.DisplayName, row.DisplayName);
        Assert.Equal(1, row.HighestRealmLevel);
        Assert.False(row.QiRewarded);
        Assert.False(row.FoundationRewarded);

        var inviteeOverview = await invitations.GetOverviewAsync(inviteeResult.PlayerId.Value);
        Assert.Equal(inviter.Result.DisplayName, inviteeOverview!.InvitedByName);
        await source.DisposeAsync();
    }

    /// <summary>不存在的邀请码使注册整体失败（INVALID_INVITATION_CODE），账号不可登录。</summary>
    [Fact]
    public async Task RegisterWithInvalidInvitationCodeFails()
    {
        var (source, accounts, _, _) = await OpenRepositoriesAsync();
        var account = UniqueAccount();
        var result = await accounts.RegisterAsync(account, "invite-pass-1", null, "NOTEXIST99",
            new AccountRiskSignals(DeviceId: account));

        Assert.False(result.Success);
        Assert.Equal("INVALID_INVITATION_CODE", result.ErrorCode);
        Assert.False((await accounts.LoginAsync(account, "invite-pass-1", new AccountRiskSignals(DeviceId: account))).Success);
        await source.DisposeAsync();
    }

    /// <summary>空白邀请码等价于未填写：注册照常，无邀请关系与奖励。</summary>
    [Fact]
    public async Task RegisterWithBlankInvitationCodeBehavesLikeNoCode()
    {
        var (source, accounts, invitations, assets) = await OpenRepositoriesAsync();
        var account = UniqueAccount();
        var result = await accounts.RegisterAsync(account, "invite-pass-1", null, "   ",
            new AccountRiskSignals(DeviceId: account));

        Assert.True(result.Success);
        Assert.Null(result.InviterPlayerId);
        Assert.Empty(await LoadBalancesAsync(assets, result.PlayerId!.Value));
        var overview = await invitations.GetOverviewAsync(result.PlayerId.Value);
        Assert.Null(overview!.InvitedByName);
        await source.DisposeAsync();
    }

    /// <summary>
    /// 里程碑幂等：突破练气（19）双方各 +1000、筑基（31）双方各 +2000；
    /// 重复按同等级结算不再入账，低等级不触发但境界进度照常推进。
    /// </summary>
    [Fact]
    public async Task GrantMilestonesIsIdempotentAndThresholdBound()
    {
        var (source, accounts, invitations, assets) = await OpenRepositoriesAsync();
        var inviter = await RegisterAsync(source, accounts);
        var inviteeResult = await RegisterAsync(accounts, inviter.InviteCode);
        var inviteeId = inviteeResult.PlayerId!.Value;

        // 未到练气门槛：不发放任何里程碑，但境界进度照常推进。
        var none = await invitations.GrantMilestonesAsync(inviteeId, 18);
        Assert.Empty(none);
        var overview = await invitations.GetOverviewAsync(inviter.Result.PlayerId!.Value);
        Assert.Equal(18, Assert.Single(overview!.Invitees).HighestRealmLevel);

        var qi = await invitations.GrantMilestonesAsync(inviteeId, InvitationRewards.QiRealmLevel);
        var qiGrant = Assert.Single(qi);
        Assert.Equal(InvitationRewards.MilestoneQi, qiGrant.Milestone);
        Assert.Equal(inviter.Result.PlayerId!.Value, qiGrant.InviterId);
        Assert.Equal(InvitationRewards.QiMilestoneMerit, qiGrant.Merit);

        // 重复同等级结算：标记已翻转，不再入账。
        Assert.Empty(await invitations.GrantMilestonesAsync(inviteeId, InvitationRewards.QiRealmLevel));

        var foundation = await invitations.GrantMilestonesAsync(inviteeId, InvitationRewards.FoundationRealmLevel);
        Assert.Single(foundation);
        Assert.Empty(await invitations.GrantMilestonesAsync(inviteeId, InvitationRewards.FoundationRealmLevel + 5));

        const double expected = InvitationRewards.SignupInviteeMerit
            + InvitationRewards.QiMilestoneMerit + InvitationRewards.FoundationMilestoneMerit;
        Assert.Equal(expected, (await LoadBalancesAsync(assets, inviteeId))[InvitationRewards.BoundMeritItemId]);
        Assert.Equal(expected, (await LoadBalancesAsync(assets, inviter.Result.PlayerId!.Value))[InvitationRewards.BoundMeritItemId]);

        overview = await invitations.GetOverviewAsync(inviter.Result.PlayerId.Value);
        var row = Assert.Single(overview!.Invitees);
        Assert.True(row.QiRewarded);
        Assert.True(row.FoundationRewarded);
        Assert.Equal(InvitationRewards.FoundationRealmLevel + 5, row.HighestRealmLevel);
        await source.DisposeAsync();
    }

    /// <summary>跳级突破（直达筑基）：单次结算一次补齐练气+筑基两档奖励。</summary>
    [Fact]
    public async Task GrantMilestonesSkipsQiWhenJumpingStraightToFoundation()
    {
        var (source, accounts, invitations, assets) = await OpenRepositoriesAsync();
        var inviter = await RegisterAsync(source, accounts);
        var inviteeResult = await RegisterAsync(accounts, inviter.InviteCode);
        var inviteeId = inviteeResult.PlayerId!.Value;

        var grants = await invitations.GrantMilestonesAsync(inviteeId, InvitationRewards.FoundationRealmLevel);
        Assert.Equal([InvitationRewards.MilestoneQi, InvitationRewards.MilestoneFoundation],
            grants.Select(grant => grant.Milestone).ToArray());

        const double milestoneTotal = InvitationRewards.QiMilestoneMerit + InvitationRewards.FoundationMilestoneMerit;
        Assert.Equal(InvitationRewards.SignupInviterMerit + milestoneTotal,
            (await LoadBalancesAsync(assets, inviter.Result.PlayerId!.Value))[InvitationRewards.BoundMeritItemId]);
        await source.DisposeAsync();
    }

    /// <summary>未受邀玩家调用里程碑结算为空操作。</summary>
    [Fact]
    public async Task GrantMilestonesWithoutInvitationIsNoOp()
    {
        var (source, accounts, invitations, _) = await OpenRepositoriesAsync();
        var solo = await RegisterAsync(source, accounts);
        Assert.Empty(await invitations.GrantMilestonesAsync(solo.Result.PlayerId!.Value, InvitationRewards.FoundationRealmLevel));
        await source.DisposeAsync();
    }

    private static string UniqueAccount() => $"inv_{Guid.NewGuid():N}"[..16];

    /// <summary>无邀请码注册；返回结果与本人专属邀请码（从玩家行读回，与端点同一数据面）。</summary>
    private static async Task<(AccountRegistrationResult Result, string InviteCode)> RegisterAsync(
        NpgsqlDataSource source, PostgresAccountRepository accounts)
    {
        var result = await RegisterAsync(accounts, invitationCode: null);
        await using var connection = await source.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT invite_code FROM identity.players WHERE player_id = $1", connection);
        command.Parameters.Add(new NpgsqlParameter { Value = result.PlayerId!.Value });
        var inviteCode = (string)(await command.ExecuteScalarAsync())!;
        return (result, inviteCode);
    }

    /// <summary>按邀请码注册并返回注册结果。</summary>
    private static async Task<AccountRegistrationResult> RegisterAsync(
        PostgresAccountRepository accounts, string? invitationCode)
    {
        var account = UniqueAccount();
        return await accounts.RegisterAsync(account, "invite-pass-1", null, invitationCode,
            new AccountRiskSignals(DeviceId: account));
    }

    /// <summary>按 asset_id 汇总玩家正余额（变体合并，测试断言只看总量）。</summary>
    private static async Task<IReadOnlyDictionary<string, double>> LoadBalancesAsync(
        PostgresAssetRepository assets, Guid playerId)
        => (await assets.LoadBalancesAsync(playerId))
            .GroupBy(row => row.AssetId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));

    private static async Task<(NpgsqlDataSource Source, PostgresAccountRepository Accounts,
        PostgresInvitationRepository Invitations, PostgresAssetRepository Assets)> OpenRepositoriesAsync()
    {
        var connectionString = PostgresTestSchema.RequireConnectionString("邀请仓储集成测试");
        var source = NpgsqlDataSource.Create(connectionString);
        await PostgresTestSchema.EnsureAsync(source);
        var options = new ServerOptions(
            "test", connectionString, 128,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30),
            64, TimeSpan.FromSeconds(1), TimeSpan.FromDays(30),
            64 * 1024, 128, 1, 0, EnableSchemaBootstrap: false);
        return (source, new PostgresAccountRepository(
            source, options, NullLogger<PostgresAccountRepository>.Instance, new PostgresOutboxRepository(source),
            new AccountRiskPolicyRegistry(), new AccountRiskHasher("invitation-tests")),
            new PostgresInvitationRepository(source), new PostgresAssetRepository(source));
    }
}
