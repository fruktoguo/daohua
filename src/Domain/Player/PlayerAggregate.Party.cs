using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;

namespace Domain.Player;

/// <summary>组队讨伐会话的聚合入口：开打插队、散场收尾与成员面板冻结采集。</summary>
public sealed partial class PlayerAggregate
{
    /// <summary>
    /// 开打命令入口：会话行动高优插队队首（挂起原队首行动，散场后恢复）。
    /// 重复会话 ID 幂等——同一 sessionId 已在队首时直接返回现状。
    /// </summary>
    /// <param name="spec">会话规格（含全部成员冻结面板）。</param>
    /// <param name="at">开打墙钟。</param>
    /// <param name="catalog">行动配表（校验 party.expedition 内置行动存在）。</param>
    /// <returns>队首会话行动实例；本成员不在名单中或已在其他会话时返回 null。</returns>
    public QueuedAction? StartPartyExpedition(PartyExpeditionSpec spec, DateTimeOffset at, ActionCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!spec.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "组队会话规格无效。");
        if (spec.SelfIndexOf(PlayerId) < 0) return null;
        if (_actions.Count > 0 && _actions[0].PartySession is { } existing
            && string.Equals(existing.Spec.SessionId, spec.SessionId, StringComparison.Ordinal))
            return _actions[0];
        var intent = new ActionIntent(ActionCatalog.PartyExpeditionActionId, -1, spec.NodeId, PvpEnabled: false);
        return EnqueuePriority(intent, at, catalog, partySession: new PartySessionBinding(spec)) as QueuedAction;
    }

    /// <summary>
    /// 散场命令入口：给队首会话行动写入权威结束时刻，推进到该时刻自然收尾离队；
    /// 非本会话或当前无会话行动时返回 false（幂等）。
    /// </summary>
    public bool EndPartyExpedition(string sessionId, double endsAtUnixSeconds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64
            || !double.IsFinite(endsAtUnixSeconds) || endsAtUnixSeconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "组队散场参数无效。");
        if (_actions.Count == 0 || _actions[0].PartySession is not { } session
            || !string.Equals(session.Spec.SessionId, sessionId, StringComparison.Ordinal))
            return false;
        _actions[0].PartySession = session with { EndsAtUnixSeconds = endsAtUnixSeconds };
        MarkDirty(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>会话期间不可被斗法撮合判定：队首为未结束会话行动即视为在队战中。</summary>
    public bool IsInPartyExpedition => _actions.Count > 0 && _actions[0].PartySession is { } session
        && session.EndsAtUnixSeconds > LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;

    /// <summary>冻结当前面板为会话成员快照（在线采集路径；离线成员走 <see cref="PartyMemberSnapshot.FromState"/>）。</summary>
    public PartyMemberSnapshot CapturePartyMemberSnapshot(GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        var attributes = PlayerAttributeResolver.Resolve(this, gameData);
        var externalBuffs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var buff in Buffs.Active)
            externalBuffs[buff.BuffId] = externalBuffs.GetValueOrDefault(buff.BuffId) + buff.Stacks;
        return new PartyMemberSnapshot(
            PlayerId.Value.ToString("D"),
            DisplayName,
            Progression.RealmLevel,
            attributes.DropBonusPoints,
            attributes.ActionSpeedPoints,
            Resources.Hitpoints,
            Resources.Manapoints,
            attributes.ToCombatant(Progression.CombatExperience),
            SkillSlot.FromAbilitySlots(Loadout.AbilitySlots, gameData.Abilities, Loadout.AbilityTriggers),
            externalBuffs.Count > 0 ? externalBuffs : null,
            Combat.Skills,
            CaptureSustainedBuffs(Settings.AutoUseSlots, gameData));
    }

    /// <summary>
    /// 开打定格的持续消耗品 Buff 采集：与 Ecs 侧 InjectSustainedCombatBuffs 的同槽位判定/同字段读取逐位对齐
    /// （通用栏 + combat 类型 + 持续非耐久消耗品 + 剩余时长大于 0）；会话期间槽位不补能不扣减，
    /// 定格值是所有成员模拟的唯一公共输入。
    /// </summary>
    internal static List<PartySustainedBuff>? CaptureSustainedBuffs(IReadOnlyList<AutoUseSlotState> slots, GameDataCatalog gameData)
    {
        List<PartySustainedBuff>? list = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null || slot.Group != AutoUseSlotState.SlotGroupGeneral
                || !string.Equals(slot.ActionType, "combat", StringComparison.OrdinalIgnoreCase)
                || slot.RemainingDurationSeconds <= 0) continue;
            var itemId = new ItemId(slot.ItemId);
            if (!gameData.Items.TryGetValue(itemId, out var item)
                || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0, Durable: false }
                || !item.Consumable.UsableInActionKinds.Contains(ActionKind.Combat)
                || !gameData.RawItems.TryGetValue(itemId.Value, out var rawItem)
                || !rawItem.TryGetProperty("consumable", out var raw)
                || !raw.TryGetProperty("buffs", out var buffs) || buffs.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var buff in buffs.EnumerateArray())
            {
                var buffId = buff.GetProperty("buffId").GetString()!;
                // 同槽位同 Buff 重复命中时后者覆盖（与战内 Buff 注入的 RemoveAll+Add 语义一致）。
                list ??= [];
                list.RemoveAll(entry => entry.BuffId == buffId);
                list.Add(new PartySustainedBuff(buffId, slot.RemainingDurationSeconds, 1,
                    JsonRead.Number(buff, "hpRegen"), JsonRead.Number(buff, "mpRegen"),
                    JsonRead.Number(buff, "attackSpeedPoints"), JsonRead.Number(buff, "damageAbsorptionPoints"),
                    JsonRead.Number(buff, "threatPoints"),
                    JsonRead.Number(buff, "hpRegenPostRatio"), JsonRead.Number(buff, "mpRegenPostRatio")));
            }
        }
        return list;
    }
}

public sealed partial record PartyMemberSnapshot
{
    /// <summary>从持久化快照重建成员冻结面板（离线/未加载成员路径；与聚合采集同构输出）。</summary>
    public static PartyMemberSnapshot FromState(PlayerAggregateState state, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var attributes = PlayerAttributeResolver.Resolve(state, catalog);
        var loadout = state.Loadout;
        var externalBuffs = new Dictionary<string, double>(StringComparer.Ordinal);
        if (state.Buffs is not null)
            foreach (var buff in state.Buffs)
                externalBuffs[buff.BuffId] = externalBuffs.GetValueOrDefault(buff.BuffId) + buff.Stacks;
        return new PartyMemberSnapshot(
            state.PlayerId.Value.ToString("D"),
            state.DisplayName,
            state.Progression.RealmLevel,
            attributes.DropBonusPoints,
            attributes.ActionSpeedPoints,
            state.Combat.Hitpoints,
            state.Combat.Manapoints,
            attributes.ToCombatant(state.Progression.CombatExperience),
            SkillSlot.FromAbilitySlots(loadout.AbilitySlots, catalog.Abilities, loadout.AbilityTriggers),
            externalBuffs.Count > 0 ? externalBuffs : null,
            state.Combat.Skills,
            PlayerAggregate.CaptureSustainedBuffs(state.Settings.AutoUseSlots ?? [], catalog));
    }
}
