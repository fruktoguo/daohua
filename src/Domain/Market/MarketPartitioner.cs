namespace Domain.Market;

public static class MarketPartitioner
{
    public const string Algorithm = "fnv1a64-v1";

    public static int GetPartition(MarketInstrument instrument, int partitionCount)
    {
        if (partitionCount is < 1 or > 4096) throw new DomainException(DomainErrorCode.InvalidArgument, "市场分片数量无效。");
        var bytes = System.Text.Encoding.UTF8.GetBytes(instrument.ToString());
        ulong hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return (int)(hash % (ulong)partitionCount);
    }
}
