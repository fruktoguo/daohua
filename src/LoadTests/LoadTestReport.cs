namespace DaoHua.LoadTests;

/// <summary>
/// 压测结果报告。
/// 记录场景、玩家数、耗时、资源占用、错误数、是否通过容量门禁等关键指标。
/// </summary>
/// <param name="ContractVersion">报告契约版本（当前固定 "loadtest.v1"）。</param>
/// <param name="Scenario">压测场景名称。</param>
/// <param name="RequestedPlayers">请求的玩家数。</param>
/// <param name="DurationSeconds">压测持续时间（秒）。</param>
/// <param name="ElapsedMilliseconds">总耗时（毫秒）。</param>
/// <param name="CpuMilliseconds">CPU 时间（毫秒）。</param>
/// <param name="ManagedBytesBefore">压测前托管堆内存（字节）。</param>
/// <param name="ManagedBytesAfter">压测后托管堆内存（字节）。</param>
/// <param name="WorkingSetBytes">工作集内存（字节）。</param>
/// <param name="SentBytes">发送总字节数。</param>
/// <param name="ReceivedBytes">接收总字节数。</param>
/// <param name="SentOperations">发送侧操作数；口径随场景（HTTP 请求数 / WS 发送帧数 / 队列 upsert 数 / 状态转移数）。</param>
/// <param name="ReceivedOperations">接收侧操作数；口径随场景（成功请求数 / 接收帧数 / 已冲刷行数 / 完成行动数）。</param>
/// <param name="Errors">错误计数。</param>
/// <param name="GatePassed">容量门禁是否通过；进程退出码还要求 Errors 为 0（见 Program.Main）。</param>
/// <param name="Metrics">扩展指标字典。</param>
/// <param name="Warnings">警告消息列表。</param>
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
    /// <summary>
    /// 构造一个表示压测失败的结果报告。
    /// </summary>
    /// <param name="scenario">失败场景名称。</param>
    /// <param name="message">失败原因消息。</param>
    /// <returns>失败报告实例。</returns>
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
