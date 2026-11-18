using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;
using Protocol;

namespace Server.Clean.Redeem;

/// <summary>
/// 兑换码核心业务运行时：
/// - 玩家兑换：归一化校验、提交事务入账并实时热刷新在线玩家 ECS 内存钱包；
/// - 运营管理：配表合法性校验（物品/装备存在性、强化等级 0..20 约束、数量与过期时间范围）、批次创建与停用恢复。
/// </summary>
public sealed class RedeemRuntime(
    PostgresRedeemRepository repository,
    GameDataCatalog gameData,
    IPlayerRuntime? playerRuntime = null,
    IPlayerSimulationClock? clock = null)
{
    private DateTimeOffset Now => clock?.UtcNow ?? DateTimeOffset.UtcNow;

    /// <summary>玩家核销兑换码并同步通知在线运行时刷新背包快照。</summary>
    public async ValueTask<RedeemCodeResponseDto> RedeemAsync(Guid playerId, string? rawCode, CancellationToken ct = default)
    {
        if (!RedeemCodeRules.TryNormalizeCode(rawCode, out var normalizedCode))
        {
            throw new RedeemOperationException(
                RedeemOperationFailure.InvalidCode,
                $"请输入有效的兑换码（{RedeemCodeRules.MinCodeLength}~{RedeemCodeRules.MaxCodeLength} 位字母、数字、连字符或下划线）。");
        }

        var response = await repository.RedeemAsync(playerId, normalizedCode, Now, ct).ConfigureAwait(false);
        if (playerRuntime is not null)
            await playerRuntime.ReloadWalletAndNotifyAsync(playerId, CancellationToken.None).ConfigureAwait(false);
        return response;
    }

    /// <summary>运营后台创建通用口令码。</summary>
    public ValueTask<OpsCreateRedeemBatchResponse> CreateUniversalAsync(
        OpsCreateUniversalRedeemRequest request,
        CancellationToken ct = default)
    {
        if (!RedeemCodeRules.TryNormalizeCode(request.Code, out var normalizedCode))
        {
            throw new RedeemOperationException(
                RedeemOperationFailure.InvalidArgument,
                $"通用口令格式无效：长度须为 {RedeemCodeRules.MinCodeLength}~{RedeemCodeRules.MaxCodeLength} 位，仅允许字母、数字、'-' 或 '_'。");
        }

        if (request.MaxClaims is { } maxClaims && (maxClaims <= 0 || maxClaims > 10_000_000))
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, "全服限量次数必须为大于 0 的正整数（不限请留空）。");

        var (title, rewards, now, expiresAt) = ValidateBatchCommon(
            request.Title,
            request.Rewards,
            request.ExpireDays,
            request.NeverExpire);

        return repository.CreateUniversalBatchAsync(normalizedCode, title, rewards, request.MaxClaims, now, expiresAt, ct);
    }

    /// <summary>运营后台批量创建一码一用单次码。</summary>
    public ValueTask<OpsCreateRedeemBatchResponse> CreateSingleUseAsync(
        OpsCreateSingleUseRedeemRequest request,
        CancellationToken ct = default)
    {
        if (request.Count < RedeemCodeRules.MinSingleUseCount || request.Count > RedeemCodeRules.MaxSingleUseCount)
        {
            throw new RedeemOperationException(
                RedeemOperationFailure.InvalidArgument,
                $"单批生成数量必须在 {RedeemCodeRules.MinSingleUseCount}~{RedeemCodeRules.MaxSingleUseCount} 之间。");
        }

        if (!RedeemCodeRules.TryNormalizePrefix(request.Prefix, out var normalizedPrefix))
        {
            throw new RedeemOperationException(
                RedeemOperationFailure.InvalidArgument,
                $"单次码前缀长度不能超过 {RedeemCodeRules.MaxPrefixLength} 位，且仅允许字母、数字、'-' 或 '_'。");
        }

        var (title, rewards, now, expiresAt) = ValidateBatchCommon(
            request.Title,
            request.Rewards,
            request.ExpireDays,
            request.NeverExpire);

        return repository.CreateSingleUseBatchAsync(request.Count, normalizedPrefix, title, rewards, now, expiresAt, ct);
    }

    /// <summary>运营后台查询兑换码批次列表。</summary>
    public ValueTask<IReadOnlyList<OpsRedeemBatchDto>> ListOpsBatchesAsync(CancellationToken ct = default)
        => repository.ListBatchesAsync(Now, ct);

    /// <summary>运营后台查询指定批次下的兑换码或核销明细。</summary>
    public ValueTask<IReadOnlyList<OpsRedeemCodeItemDto>> ListOpsBatchCodesAsync(
        long batchId,
        string? statusFilter,
        int limit,
        CancellationToken ct = default)
    {
        if (batchId <= 0)
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, "批次 ID 无效。");
        return repository.ListBatchCodesAsync(batchId, statusFilter, limit, ct);
    }

    /// <summary>运营后台停用或恢复指定兑换码批次。</summary>
    public ValueTask<bool> SetBatchDisabledAsync(long batchId, bool disabled, CancellationToken ct = default)
    {
        if (batchId <= 0)
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, "批次 ID 无效。");
        return repository.SetBatchDisabledAsync(batchId, disabled, Now, ct);
    }

    private (string Title, IReadOnlyList<RedeemRewardDto> Rewards, DateTimeOffset Now, DateTimeOffset? ExpiresAt) ValidateBatchCommon(
        string? rawTitle,
        IReadOnlyList<RedeemRewardDto>? rawRewards,
        int? rawExpireDays,
        bool neverExpire)
    {
        var now = Now;
        var title = (rawTitle ?? string.Empty).Trim();
        if (title.Length == 0 || title.Length > RedeemCodeRules.MaxTitleLength)
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"批次名称长度必须在 1~{RedeemCodeRules.MaxTitleLength} 字之间。");

        DateTimeOffset? expiresAt = null;
        if (!neverExpire)
        {
            var expireDays = rawExpireDays ?? 30;
            if (expireDays < 1 || expireDays > RedeemCodeRules.MaxExpireDays)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"有效天数必须在 1~{RedeemCodeRules.MaxExpireDays} 天之间。");
            expiresAt = now.AddDays(expireDays);
        }

        if (rawRewards is not { Count: > 0 })
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, "请至少配置 1 项有效兑换奖励。");
        if (rawRewards.Count > RedeemCodeRules.MaxRewardsPerBatch)
            throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"单批次最多配置 {RedeemCodeRules.MaxRewardsPerBatch} 种奖励槽位。");

        var merged = new Dictionary<(string ItemId, int EnhancementLevel), double>();
        foreach (var entry in rawRewards)
        {
            var itemId = (entry.ItemId ?? string.Empty).Trim();
            if (itemId.Length == 0)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, "奖励物品 ID 不能为空。");

            var isEquipment = gameData.RawEquipments.ContainsKey(itemId);
            var isKnownItem = gameData.Items.ContainsKey(new ItemId(itemId)) || gameData.RawItems.ContainsKey(itemId) || isEquipment;
            if (!isKnownItem)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"奖励物品不存在于当前配表：{itemId}");

            if (entry.EnhancementLevel < 0 || entry.EnhancementLevel > EnhancementRules.MaxLevel)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"强化等级必须在 0~{EnhancementRules.MaxLevel} 之间：{itemId}");
            if (entry.EnhancementLevel > 0 && !isEquipment)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"非装备物品不支持设置强化等级：{itemId}");

            if (!double.IsFinite(entry.Quantity) || entry.Quantity < 1 || entry.Quantity > 1_000_000_000d || entry.Quantity != Math.Floor(entry.Quantity))
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"奖励 {itemId} 数量必须为 1~1,000,000,000 之间的正整数。");

            var key = (itemId, entry.EnhancementLevel);
            var nextQty = merged.GetValueOrDefault(key) + entry.Quantity;
            if (nextQty > 1_000_000_000d)
                throw new RedeemOperationException(RedeemOperationFailure.InvalidArgument, $"奖励 {itemId} 合并数量超出上限。");
            merged[key] = nextQty;
        }

        var normalizedRewards = merged
            .Select(pair => new RedeemRewardDto(pair.Key.ItemId, pair.Value, pair.Key.EnhancementLevel))
            .ToArray();
        return (title, normalizedRewards, now, expiresAt);
    }
}
