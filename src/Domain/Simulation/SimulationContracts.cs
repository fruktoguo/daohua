using Domain.Common;
using Domain.Economy;

namespace Domain.Simulation;

/// <summary>
/// 快进模拟推进过程中触发的关键业务事件（如单轮行动完成、掉落产生、失效行动自动移除等）。
/// </summary>
/// <param name="At">事件发生时间（UTC）</param>
/// <param name="Code">事件分类代码</param>
/// <param name="Message">事件日志文本</param>
/// <param name="ActionId">触发事件的行动定义 ID</param>
/// <param name="ItemDeltas">产出或消耗的物品变动明细</param>
public sealed record SimulationEvent(
    DateTimeOffset At,
    string Code,
    string Message,
    ActionId? ActionId = null,
    IReadOnlyDictionary<ItemId, double>? ItemDeltas = null);

/// <summary>
/// 模拟引擎单次推进的完整结算结果。
/// 包含时间跨度、推进轮次、物品资产流水变动以及是否引起聚合状态演进。
/// Events 是有界尾窗（至少保留末尾 256 条、至多 512 条）；更早的事件折叠进 DroppedEventCount。
/// </summary>
public sealed record SimulationResult(
    PlayerId PlayerId,
    DateTimeOffset From,
    DateTimeOffset To,
    double ElapsedSeconds,
    double CompletedActions,
    double CompletedRepeats,
    IReadOnlyList<AssetDelta> AssetDeltas,
    IReadOnlyList<SimulationEvent> Events,
    long StateVersion,
    bool Changed,
    /// <summary>被事件尾窗折叠丢弃的事件总数：DroppedEventCount + Events.Count == 本次推进实际产出数。</summary>
    long DroppedEventCount = 0);
