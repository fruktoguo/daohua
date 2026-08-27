namespace DaoHua.LoadTests;

public sealed record LoadTestReport(
    string ContractVersion,
    string Scenario,
    int RequestedPlayers,
    int DurationSeconds,
    double ElapsedMilliseconds,
    double CpuMilliseconds,
    long ManagedBytesBefore,
    long ManagedBytesAfter,
    long WorkingSetBytes,
    long SentBytes,
    long ReceivedBytes,
    long SentOperations,
    long ReceivedOperations,
    int Errors,
    bool GatePassed,
    IReadOnlyDictionary<string, object?> Metrics,
    IReadOnlyList<string> Warnings)
{
    public static LoadTestReport Failure(string scenario, string message) => new(
        "loadtest.v1",
        scenario,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        0,
        1,
        false,
        new Dictionary<string, object?>(),
        [message]);
}
