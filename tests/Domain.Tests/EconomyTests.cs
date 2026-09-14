namespace Domain.Tests;

/// <summary>
/// 经济测试：钱包余额、交易流水、资产有效性与零增量幂等性。
/// </summary>
public sealed class EconomyTests
{
    /// <summary>
    /// 验证 <c>WalletEnforcesNonNegativeBalanceAndVersions</c>（Wallet Enforces Non Negative Balance And Versions）所描述的领域行为。
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
    /// 验证 <c>TransactionLinesRequireUniquePositiveLineNumbers</c>（Transaction Lines Require Unique Positive Line Numbers）所描述的领域行为。
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
    /// 验证 <c>EconomyTransactionRejectsDuplicateOrZeroLines</c>（Economy Transaction Rejects Duplicate Or Zero Lines）所描述的领域行为。
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
    /// 验证 <c>WalletRejectsInvalidAssetAndNonFiniteQuantity</c>（Wallet Rejects Invalid Asset And Non Finite Quantity）所描述的领域行为。
    /// </summary>
    [Fact]
    public void WalletRejectsInvalidAssetAndNonFiniteQuantity()
    {
        var wallet = new AssetWallet(PlayerId.New());

        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("")), 1, "grant"));
        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("item.x")), double.NaN, "grant"));
    }

    /// <summary>
    /// 验证 <c>ApplyZeroDeltaIsNoOp</c>（Apply Zero Delta Is No Op）所描述的领域行为。
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
