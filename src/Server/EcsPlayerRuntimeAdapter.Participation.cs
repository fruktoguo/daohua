using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Domain.Common;
using Domain.Persistence;
using Domain.Player;
using Domain.Simulation;
using Server.Clean;
using Server.Clean.Social;

namespace Server;

public sealed partial class EcsPlayerRuntimeAdapter
{
    /// <summary>
    /// 玩家门内消费唯一会话产出的个人结算；收件箱回执随资产与分段原子提交。
    /// 此处只投影既定结果，不进入成员战斗推进，也不持有其他成员实体。
    /// </summary>
    private async ValueTask ReconcileParticipationOwnedAsync(Guid playerId, DateTimeOffset target,
        List<RuntimeNotification> notifications, CancellationToken cancellationToken, bool advanceSession = true,
        bool initialLoad = false)
    {
        if (participationRuntime is null || participationSessions is null || socialRepository is null) return;
        if (!initialLoad && !participationRuntime.RequiresReconciliation(playerId)
            && (!_loaded.TryGetValue(playerId, out var loaded)
                || !loaded.State.Actions.Any(action => action.Participation is not null))) return;
        var pending = advanceSession
            ? await participationRuntime.AdvanceForMemberAsync(playerId, target, cancellationToken).ConfigureAwait(false)
            : await participationRuntime.LoadPendingAsync(playerId, cancellationToken).ConfigureAwait(false);
        var sessions = new Dictionary<string, PartySessionRecord>(StringComparer.Ordinal);
        foreach (var sessionId in pending.Select(item => item.SessionId).Distinct())
            sessions[sessionId] = await socialRepository.GetPartySessionAsync(sessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("会话结算缺少权威会话记录。");
        // 同刻换场也必须先消费旧会话末批；组内序号才是个人收益消费顺序。
        foreach (var group in pending.GroupBy(item => item.SessionId).OrderBy(group => sessions[group.Key].StartedAt))
        {
            var row = sessions[group.Key];
            var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(row.SpecJson, PersistenceJson.Options)
                ?? throw new InvalidOperationException("会话冻结输入为空。");
            var groupMarks = ecs.GetDirtyMarks(playerId);
            var groupLogs = new List<Protocol.LogEventDto>();
            foreach (var item in group.OrderBy(item => item.Sequence))
            {
                var settlement = JsonSerializer.Deserialize<ActionParticipationMemberSettlement>(item.PayloadJson, PersistenceJson.Options)
                    ?? throw new InvalidOperationException("会话个人结算载荷为空。");
                if (settlement.PlayerId.Value != playerId || settlement.SessionId != item.SessionId || settlement.Sequence != item.Sequence)
                    throw new InvalidOperationException("会话收件箱与个人结算身份不一致。");
                var before = ToSnapshot(ecs.ExportSnapshot(playerId));
                var dirtyBefore = ecs.GetDirtySections(playerId);
                SimulationResult? applied = null;
                try
                {
                    ecs.MutatePlayer(playerId, aggregate =>
                    {
                        if (aggregate.Actions.Count == 0 || aggregate.Actions[0].Participation?.SessionId != item.SessionId)
                        {
                            if (aggregate.Actions.Any(action => action.Participation is not null))
                                throw new DomainException(DomainErrorCode.InvariantViolation, "前一会话结算尚未收尾。");
                            aggregate.StartActionParticipation(spec, row.StartedAt, gameData.Actions,
                                row.EndedAt?.ToUnixTimeMilliseconds() / 1000d ?? double.PositiveInfinity);
                        }
                        aggregate.Actions[0].Participation = aggregate.Actions[0].Participation! with
                        { Spec = spec, EndsAtUnixSeconds = double.PositiveInfinity };
                    });
                    // 参与引用先屏蔽个人行动，保留原资源管线的暂存入账、Buff 到期与道龄。
                    // 散场标记随投影后置，不能让资源推进提前移除引用并执行挂起的原行动。
                    if (settlement.To > before.LastSimulatedAt)
                        await ecs.RunPlayerOnceAsync(playerId, settlement.To, cancellationToken).ConfigureAwait(false);
                    var personal = ecs.DrainSimulationResult(playerId);
                    ecs.DrainSimulationEvents(playerId);
                    ecs.MutatePlayer(playerId, aggregate =>
                    {
                        applied = aggregate.ApplyActionParticipationSettlement(settlement, gameData);
                        if (personal is not null)
                            applied = applied with
                            {
                                AssetDeltas = personal.AssetDeltas.Count == 0 ? applied.AssetDeltas
                                    : personal.AssetDeltas.Concat(applied.AssetDeltas).ToArray(),
                                Events = personal.Events.Count == 0 ? applied.Events
                                    : personal.Events.Concat(applied.Events).OrderBy(entry => entry.At).TakeLast(512).ToArray(),
                                DroppedEventCount = personal.DroppedEventCount + applied.DroppedEventCount
                                    + Math.Max(0, personal.Events.Count + applied.Events.Count - 512)
                            };
                        // 最终回执与归档/释放引用同事务提交，崩溃后不会留下已确认但尚未收尾的队首。
                        if (settlement.EndsAtUnixSeconds is { } endsAt)
                        {
                            aggregate.EndActionParticipation(item.SessionId, endsAt);
                            aggregate.RemoveAction(aggregate.Actions[0].Id, endedAtUnixSeconds: endsAt);
                            Domain.Combat.PlayerAttributeResolver.Apply(aggregate, gameData);
                        }
                    });
                }
                catch
                {
                    RestoreUncommittedPlayer(playerId, before, dirtyBefore);
                    throw;
                }
                var key = $"participation:{item.SessionId}:{playerId:D}:{item.Sequence}";
                var transaction = SimulationEconomyTransaction.Create(playerId, applied!) with
                {
                    TransactionId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16)),
                    IdempotencyKey = key,
                    TransactionType = "participation.settlement",
                    ParticipationReceipt = new ParticipationInboxReceipt(item.SessionId, playerId, item.Sequence)
                };
                await CommitPlayerAssetsAsync(playerId, before, dirtyBefore, transaction, null, cancellationToken).ConfigureAwait(false);
                if (applied!.Events.Count > 0)
                    groupLogs.AddRange(SimulationLogMapper.ToLogEvents(applied.Events, gameData,
                        _logs.GetOrAdd(playerId, static _ => new PlayerLogBuffer(LogBufferCapacity)), LogBufferCapacity));
            }
            var after = ToSnapshot(ecs.ExportSnapshot(playerId)) with
            {
                TouchedSections = TouchedSectionsSince(groupMarks, ecs.GetDirtyMarks(playerId)),
                WireAttributesChanged = true
            };
            _loaded[playerId] = after;
            notifications.Add(new RuntimeNotification(playerId, groupLogs.TakeLast(LogBufferCapacity).ToArray(), after));
        }

        var state = ecs.ExportSnapshot(playerId).State;
        if (state.Actions.Count > 0 && state.Actions[0].Participation is { } binding)
        {
            var row = await socialRepository.GetPartySessionAsync(binding.SessionId, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("参与引用缺少权威会话记录。");
            if (row.EndedAt is { } endedAt && double.IsFinite(binding.EndsAtUnixSeconds))
            {
                ecs.MutatePlayer(playerId, aggregate =>
                {
                    aggregate.EndActionParticipation(binding.SessionId, endedAt.ToUnixTimeMilliseconds() / 1000d);
                    aggregate.RemoveAction(aggregate.Actions[0].Id, endedAtUnixSeconds: endedAt.ToUnixTimeMilliseconds() / 1000d);
                });
                TrackDirty(playerId);
                _loaded[playerId] = ToSnapshot(ecs.ExportSnapshot(playerId));
            }
            else
            {
                var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(row.SpecJson, PersistenceJson.Options)
                    ?? throw new InvalidOperationException("会话冻结输入为空。");
                ecs.MutatePlayer(playerId, aggregate => aggregate.Actions[0].Participation = binding with { Spec = spec });
            }
        }
        else
        {
            var partyId = await socialRepository.GetPartyIdAsync(playerId, cancellationToken).ConfigureAwait(false);
            var active = partyId is { } id
                ? await socialRepository.GetActivePartySessionAsync(id, cancellationToken).ConfigureAwait(false) : null;
            if (active is not null && active.MemberIds.Contains(playerId))
            {
                var spec = JsonSerializer.Deserialize<ActionParticipationSpec>(active.SpecJson, PersistenceJson.Options)
                    ?? throw new InvalidOperationException("会话冻结输入为空。");
                ecs.MutatePlayer(playerId, aggregate => aggregate.StartActionParticipation(spec, active.StartedAt, gameData.Actions));
                TrackDirty(playerId);
                _loaded[playerId] = ToSnapshot(ecs.ExportSnapshot(playerId));
            }
        }
    }
}
