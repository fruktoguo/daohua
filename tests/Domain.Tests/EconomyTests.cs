namespace Domain.Tests;

public sealed class EconomyTests
{
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

    [Fact]
    public void WalletRejectsInvalidAssetAndNonFiniteQuantity()
    {
        var wallet = new AssetWallet(PlayerId.New());

        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("")), 1, "grant"));
        Assert.Throws<DomainException>(() => wallet.Credit(new AssetKey(new ItemId("item.x")), double.NaN, "grant"));
    }
}
