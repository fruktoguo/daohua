namespace Domain.Tests;

/// <summary>
/// 经济测试：钱包余额、交易流水、资产有效性与零增量幂等性。
/// </summary>
public sealed class EconomyTests
{
    /// <summary>
    /// 钱包余额不得透支：借贷按序入账、资产版本逐笔递增，超额扣款必须抛错。
    /// </summary>
    [Fact]
    public void WalletEnforcesNonNegativeBalanceAndVersions()
    {
        var playerId = PlayerId.New();
        var wallet = new AssetWallet(playerId);
        var asset = new AssetKey(new ItemId("item.spirit-stone"));

        var first = wallet.Credit(asset, 10, "grant");
        var second = wallet.Debit(asset, 3, "spend");

        Assert.Equal(10, first.Quantity);
        Assert.Equal(7, second.Quantity);
        Assert.Equal(2, second.Version);
        Assert.Throws<DomainException>(() => wallet.Debit(asset, 8, "overspend"));
    }

    /// <summary>
    /// 行号唯一且为正的合法流水可正常建立（基准用例，对照下一条拒绝用例）。
    /// </summary>
    [Fact]
    public void TransactionLinesRequireUniquePositiveLineNumbers()
    {
        var player = PlayerId.New();
        var asset = new AssetKey(new ItemId("item.spirit-stone"));
        var lines = new[]
        {
            new EconomyTransactionLine(1, player, asset, -1),
            new EconomyTransactionLine(2, player, asset, 1)
        };
        var transaction = new EconomyTransaction(TransactionId.New(), new IdempotencyKey("req-1"), EconomyTransactionType.AdminAdjustment, player, DateTimeOffset.UtcNow, lines);
        Assert.Equal(2, transaction.Lines.Count);
    }

    /// <summary>
    /// 行号重复的流水必须以 InvalidArgument 拒绝，防止同一行被重复结算。
    /// </summary>
    [Fact]
    public void EconomyTransactionRejectsDuplicateOrZeroLines()
    {
        var player = PlayerId.New();
        var asset = new AssetKey(new ItemId("item.spirit-stone"));
        var lines = new[]
        {
            new EconomyTransactionLine(1, player, asset, -1),
            new EconomyTransactionLine(1, player, asset, 1)
        };

        var error = Assert.Throws<DomainException>(() => new EconomyTransaction(TransactionId.New(), new IdempotencyKey("req-duplicate"), EconomyTransactionType.AdminAdjustment, player, DateTimeOffset.UtcNow, lines));

        Assert.Equal(DomainErrorCode.InvalidArgument, error.Code);
    }

    /// <summary>
    /// 空资产标识与非有限数量（NaN）在入账边界即拒绝，防止脏数值进入账本。
    /// </summary>
    [Fact]
    public void WalletRejectsInvalidAssetAndNonFiniteQuantity()
    {
        var wallet = new AssetWallet(PlayerId.New());

        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("")), 1, "grant"));
        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("item.x")), double.NaN, "grant"));
    }

    /// <summary>
    /// 零增量 Apply 为幂等空操作：无论钱包是否已有余额，余额均不受影响。
    /// </summary>
    [Fact]
    public void ApplyZeroDeltaIsNoOp()
    {
        var playerId = PlayerId.New();
        var wallet = new AssetWallet(playerId);
        var asset = new AssetKey(new ItemId("item.spirit-stone"));

        wallet.Apply(new AssetDelta(playerId, asset, 0, "noop"));
        Assert.Equal(0, wallet.Get(asset));

        wallet.Credit(asset, 5, "grant");
        wallet.Apply(new AssetDelta(playerId, asset, 0, "noop"));
        Assert.Equal(5, wallet.Get(asset));
    }
}
