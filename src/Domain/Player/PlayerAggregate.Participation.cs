using System.Text.Json;
using Domain.Combat;
using Domain.Common;
using Domain.GameData;
using Domain.Simulation;

namespace Domain.Player;

/// <summary>行动参与的聚合入口：开打插队、散场收尾与成员面板冻结采集。</summary>
public sealed partial class PlayerAggregate
{
    /// <summary>会话计算容器借用收集器记录个人增量，不向真实成员状态写入。</summary>
    internal void SetParticipationStatsSink(IPlayerStatsSink sink)
    {
        _statsSink = sink;
        IsParticipationSessionContainer = true;
    }

    /// <summary>仅会话计算工作态可推进全队资源；真实成员始终等待投影。</summary>
    internal bool IsParticipationSessionContainer { get; private set; }

    /// <summary>
    /// 在成员单写者内消费权威投影。游标与个人变更一起落盘，重投不会重复发奖；
    /// 修为瓶颈及参悟升级以消费时的个人状态结算，冻结装备只决定原始份额。
    /// </summary>
    public SimulationResult ApplyActionParticipationSettlement(
        ActionParticipationMemberSettlement settlement, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(settlement);
        ArgumentNullException.ThrowIfNull(catalog);
        if (settlement.PlayerId != PlayerId || settlement.Sequence <= 0 || settlement.To < settlement.From
            || !double.IsFinite(settlement.Hitpoints) || settlement.Hitpoints < 0
            || !double.IsFinite(settlement.Manapoints) || settlement.Manapoints < 0
            || !double.IsFinite(settlement.RegenCarrySeconds) || settlement.RegenCarrySeconds < 0
            || settlement.RegenCarrySeconds >= 5
            || !double.IsFinite(settlement.CompletedRepeats) || settlement.CompletedRepeats < 0
            || settlement.Expedition is not { IsValid: true } || settlement.CombatStats is null)
            throw new DomainException(DomainErrorCode.InvalidArgument, "会话成员结算无效。");
        if (_actions.Count == 0 || _actions[0].Participation is not { } binding
            || !string.Equals(binding.SessionId, settlement.SessionId, StringComparison.Ordinal))
            throw new DomainException(DomainErrorCode.InvariantViolation, "会话结算与成员绑定不一致。");
        if (settlement.Sequence <= binding.AppliedSequence)
            return new(PlayerId, LastSimulatedAt, LastSimulatedAt, 0, 0, 0, [], [], StateVersion, false);
        if (settlement.Sequence != binding.AppliedSequence + 1)
            throw new DomainException(DomainErrorCode.InvariantViolation, "会话个人结算序号不连续。");

        var action = _actions[0];
        var actionName = Duel.ReportFor(action.Id)?.ActionName
            ?? (catalog.Actions.TryGet(action.Intent.ActionId, out var definition) ? definition.Name
                : settlement.EndsAtUnixSeconds is not null || double.IsFinite(binding.EndsAtUnixSeconds)
                    ? action.Intent.ActionId.Value
                : catalog.Actions.Get(action.Intent.ActionId).Name);
        EnsureActionReport(action.Id, action.Intent.ActionId, actionName, settlement.From);
        var completed = 0d;
        foreach (var entry in settlement.Stats)
        {
            var delta = entry.Delta;
            if (delta.CombatExperience > 0)
            {
                // 战斗经验按增幅前基础量等额入账；修为按内功增幅后的冻结份额重算瓶颈与底蕴
                // （容器只投递原始份额，个人瓶颈与底蕴兑换在成员单写者内结算）。
                var combatExperience = delta.CombatExperience;
                var amplified = delta.CultivationExperience > 0 ? delta.CultivationExperience : combatExperience;
                // 个人瓶颈与打坐/单人战斗同口径：含灵根乘区（总纲 §7.7.5.1），
                // 否则组队成员入天门后的份额会在裸瓶颈截断、永远无法触及乘区后的突破门槛。
                var cap = catalog.RealmProgression is null
                    ? double.PositiveInfinity
                    : HeavenGateRules.ResolveExpCapWithLinggen(Progression.RealmLevel,
                        Progression.HeavenGate.EffectiveValues, Progression.HeavenGate.Entered,
                        catalog.RealmProgression);
                var credited = Progression.CreditExperience(amplified, cap, combatExperience: combatExperience);
                delta = delta with
                {
                    Experience = credited,
                    Foundation = amplified - credited,
                    CultivationExperience = 0
                };
            }
            if (delta.RootAspect is { } aspect)
            {
                var before = Loadout.CoreAttributes.ExpFor(aspect);
                Loadout.CoreAttributes = Loadout.CoreAttributes.WithAddedExp(aspect, delta.RootExperience);
                var fromLevel = RootAttributeCurve.LevelFor(before, catalog.RealmProgression);
                var toLevel = RootAttributeCurve.LevelFor(before + delta.RootExperience, catalog.RealmProgression);
                delta = delta with { LevelUp = toLevel > fromLevel
                    ? new PlayerReportLevelUp("root", aspect.ToString().ToLowerInvariant(), fromLevel, toLevel) : null };
            }
            if (delta.AbilityId is { } abilityText)
            {
                var abilityId = new AbilityId(abilityText);
                if (!Loadout.LearnedAbilities.Contains(abilityId)) continue;
                var before = Loadout.AbilityExperiences.GetValueOrDefault(abilityId);
                var after = before + delta.AbilityExperience;
                Loadout.AbilityExperiences[abilityId] = after;
                var fromLevel = AbilityExpCurve.LevelFor(before);
                var toLevel = AbilityExpCurve.LevelFor(after);
                if (toLevel > fromLevel)
                {
                    for (var i = 0; i < Loadout.AbilitySlots.Count; i++)
                    {
                        var slot = Loadout.AbilitySlots[i];
                        if (slot.AbilityId == abilityText && slot.Level < toLevel)
                            Loadout.AbilitySlots[i] = new PlayerAbilitySlot(slot.AbilityId, toLevel);
                    }
                }
                delta = delta with { LevelUp = toLevel > fromLevel
                    ? new PlayerReportLevelUp("ability", abilityText, fromLevel, toLevel) : null };
            }
            Progression.CompletedActions += delta.Cycles;
            completed += delta.Cycles;
            for (double i = 0; i < delta.NormalKills; i++) Progression.NormalKills += 1;
            for (double i = 0; i < delta.EliteKills; i++) Progression.EliteKills += 1;
            for (double i = 0; i < delta.BossKills; i++) Progression.BossKills += 1;
            Progression.TotalDefeats += delta.Deaths;
            RecordStats(action.Id, entry.AtUnixSeconds, delta);
        }
        Resources.Hitpoints = settlement.Hitpoints;
        Resources.Manapoints = settlement.Manapoints;
        Resources.RegenCarrySeconds = settlement.RegenCarrySeconds;
        Combat.Skills = settlement.Skills;
        Combat.Buffs = settlement.Buffs ?? PlayerCombatBuffState.Empty;
        if (settlement.ConsumableSlots is { } consumableSlots)
        {
            // 成员可能已等待到投影之后的墙钟；延迟消费不能把已过期周期重新充回槽位。
            var now = Math.Max(settlement.To.ToUnixTimeMilliseconds(), LastSimulatedAt.ToUnixTimeMilliseconds()) / 1000d;
            var slotsChanged = false;
            foreach (var clock in consumableSlots)
            {
                var index = Settings.AutoUseSlots.FindIndex(slot => slot.ActionType == AutoUseBar.Combat
                    && slot.Group == clock.Group && slot.SlotIndex == clock.SlotIndex && slot.ItemId == clock.ItemId);
                if (index < 0) continue;
                var slot = Settings.AutoUseSlots[index];
                var remaining = Math.Max(0, clock.SustainedExpiresAtUnixSeconds - now);
                if (slot.CooldownUntilUnixSeconds == clock.CooldownUntilUnixSeconds
                    && slot.RemainingDurationSeconds == remaining) continue;
                Settings.AutoUseSlots[index] = slot with
                {
                    CooldownUntilUnixSeconds = clock.CooldownUntilUnixSeconds,
                    RemainingDurationSeconds = remaining
                };
                slotsChanged = true;
            }
            if (slotsChanged) MarkDirty(PlayerSectionKind.Settings);
        }
        action.Expedition = settlement.Expedition;
        action.ProgressSeconds = settlement.ProgressSeconds;
        action.CompletedRepeats = settlement.CompletedRepeats;
        action.Participation = binding with
        {
            AppliedSequence = settlement.Sequence,
            EndsAtUnixSeconds = settlement.EndsAtUnixSeconds ?? binding.EndsAtUnixSeconds
        };
        var book = Duel.ReportFor(action.Id)!.CombatStats;
        book.Clear();
        foreach (var row in settlement.CombatStats) book.Import(row);
        ActionReportBook.FlushCombatStats(this, action.Id);
        if (settlement.StagedItems is { Count: > 0 } stagedItems)
        {
            foreach (var item in stagedItems)
            {
                ActionReportBook.StageCombatItem(this, new ItemId(item.ItemId), item.Quantity,
                    item.AcquiredAtUnixSeconds, catalog.Duel.StagedBucketSeconds);
            }
        }
        MarkDirty(PlayerSectionKind.Progression);
        MarkDirty(PlayerSectionKind.Loadout);
        MarkDirty(PlayerSectionKind.Combat);
        MarkDirty(PlayerSectionKind.ActionQueue);
        StateVersion = checked(StateVersion + 1);
        return new(PlayerId, settlement.From, settlement.To, (settlement.To - settlement.From).TotalSeconds,
            completed, completed, settlement.AssetDeltas, settlement.Events, StateVersion, true,
            settlement.DroppedEventCount);
    }

    /// <summary>
    /// 开始行动参与：参与行动高优插队队首（挂起原队首行动，散场后恢复）。
    /// 重复会话 ID 幂等——同一 sessionId 已在队首时直接返回现状。
    /// </summary>
    /// <param name="spec">行动参与规格（含全部成员冻结面板与真实行动 ID）。</param>
    /// <param name="at">开打墙钟。</param>
    /// <param name="catalog">行动配表。</param>
    /// <param name="endsAtUnixSeconds">终局补投时的确定散场时刻；活动开场为正无穷。</param>
    /// <param name="appliedSequence">补投引用时已消费的个人结算序号；新开场为零。</param>
    /// <returns>队首参与行动实例；本成员不在名单中或已在其他会话时返回 null。</returns>
    public QueuedAction? StartActionParticipation(ActionParticipationSpec spec, DateTimeOffset at, ActionCatalog catalog,
        double endsAtUnixSeconds = double.PositiveInfinity, long appliedSequence = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(catalog);
        if (!spec.IsValid)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动参与规格无效。");
        if (spec.SelfIndexOf(PlayerId) < 0) return null;
        if (_actions.Count > 0 && _actions[0].Participation is { } existing)
        {
            if (_actions[0].Intent.ActionId == spec.ActionId
                && string.Equals(existing.SessionId, spec.SessionId, StringComparison.Ordinal))
                return _actions[0];
            return null;
        }
        var intent = new ActionIntent(spec.ActionId, -1, spec.NodeId, PvpEnabled: spec.PvpEnabled);
        return EnqueuePriority(intent, at, catalog,
            participation: new ActionParticipationBinding(spec, endsAtUnixSeconds, appliedSequence)) as QueuedAction;
    }

    /// <summary>
    /// 结束行动参与：给队首参与行动写入权威结束时刻，推进到该时刻自然收尾离队。
    /// 非本会话或当前无参与行动时返回 false（幂等）。散场时刻倒地成员即刻以最低气血
    /// （1 点）起身——与移除死亡中远征的恢复口径一致（§21.13），不依赖成员下次推进，
    /// 保证离线成员与紧随其后的新会话冻结面板都读到复活后的气血；灵力不恢复。
    /// </summary>
    public bool EndActionParticipation(string sessionId, double endsAtUnixSeconds)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 64
            || !double.IsFinite(endsAtUnixSeconds) || endsAtUnixSeconds < 0)
            throw new DomainException(DomainErrorCode.InvalidArgument, "行动参与散场参数无效。");
        if (_actions.Count == 0 || _actions[0].Participation is not { } participation
            || !string.Equals(participation.SessionId, sessionId, StringComparison.Ordinal))
            return false;
        _actions[0].Participation = participation with { EndsAtUnixSeconds = endsAtUnixSeconds };
        if (Resources.Hitpoints <= 0)
        {
            Resources.Hitpoints = 1;
            Touch(PlayerSectionKind.Combat);
        }
        Touch(PlayerSectionKind.ActionQueue);
        return true;
    }

    /// <summary>参与期间不可被斗法撮合判定：队首为未结束参与行动即视为在队战中。</summary>
    public bool IsInActionParticipation => _actions.Count > 0 && _actions[0].Participation is { } participation
        && participation.EndsAtUnixSeconds > LastSimulatedAt.ToUnixTimeMilliseconds() / 1000d;
    /// <summary>冻结当前面板为行动参与者快照（在线采集路径；离线成员走 <see cref="ActionParticipantSnapshot.FromState"/>）。</summary>
    public ActionParticipantSnapshot CaptureActionParticipantSnapshot(GameDataCatalog gameData)
    {
        ArgumentNullException.ThrowIfNull(gameData);
        var attributes = PlayerAttributeResolver.Resolve(this, gameData);
        var externalBuffs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var buff in Buffs.Active)
            externalBuffs[buff.BuffId] = externalBuffs.GetValueOrDefault(buff.BuffId) + buff.Stacks;
        return new ActionParticipantSnapshot(
            PlayerId.Value.ToString("D"),
            DisplayName,
            Progression.RealmLevel,
            attributes.DropBonusPoints,
            attributes.SearchSpeedPoints,
            Resources.Hitpoints,
            Resources.Manapoints,
            attributes.ToCombatant(Progression.CombatExperience),
            SkillSlot.FromAbilitySlots(Loadout.AbilitySlots, gameData.Abilities, Loadout.AbilityTriggers),
            externalBuffs.Count > 0 ? externalBuffs : null,
            Combat.Skills,
            Combat.Buffs,
            CaptureSustainedBuffs(Settings.AutoUseSlots, gameData),
            Loadout.Equipment.ToArray(),
            Resources.RegenCarrySeconds,
            CaptureConsumableSlots(Settings.AutoUseSlots, gameData),
            Avatar: Settings.Avatar,
            InternalArt: CaptureInternalArt(Loadout, gameData),
            RespawnPolicy: Settings.RespawnPolicy,
            RespawnTargetPlayerId: Settings.RespawnTargetPlayerId?.ToString("D"));
    }

    /// <summary>
    /// 开打采集已生效的持续消耗品 Buff，入场由会话槽时钟覆盖，防止冻结效果重复生效。
    /// general/potion 两组沿用同一非耐久战斗消耗品资格。
    /// </summary>
    internal static List<ActionSustainedBuff>? CaptureSustainedBuffs(IReadOnlyList<AutoUseSlotState> slots, GameDataCatalog gameData)
    {
        List<ActionSustainedBuff>? list = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null
                || slot.Group is not (AutoUseSlotState.SlotGroupGeneral or AutoUseSlotState.SlotGroupPotion)
                || slot.ActionType != Domain.GameData.AutoUseBar.Combat
                || slot.RemainingDurationSeconds <= 0) continue;
            var itemId = new ItemId(slot.ItemId);
            if (!gameData.Items.TryGetValue(itemId, out var item)
                || item.Category != ItemDefinition.CategoryConsumable
                || item.Consumable is not { IsContinuous: true, DurationSeconds: > 0, Durable: false }
                || !item.Consumable.UsableInBars.Contains(Domain.GameData.AutoUseBar.Combat)
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
                // 效果键按注册表解析（持续消耗品定格无等级成长，乘区恒 1）；statRatios 沿用既有定格口径不采集。
                list.Add(new ActionSustainedBuff(buffId, slot.RemainingDurationSeconds, 1,
                    JsonRead.Text(buff, "name") ?? buffId,
                    // 阵法类（subCategory=formation）持续消耗品 Buff 全队生效——注入己方阵列而非仅使用者。
                    TeamWide: string.Equals(item.SubCategory, Domain.GameData.AutoUseBar.Formation, StringComparison.Ordinal),
                    Effects: BuffEffectParser.Parse(buff, 1.0)));
            }
        }
        return list;
    }

    /// <summary>
    /// 冻结非耐久战斗消耗品的槽配置与时钟，不采集库存；数量由独立工作钱包或离线储备提供。
    /// </summary>
    internal static List<ActionConsumableSlot>? CaptureConsumableSlots(IReadOnlyList<AutoUseSlotState> slots, GameDataCatalog gameData)
    {
        List<ActionConsumableSlot>? list = null;
        for (var i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (!slot.Enabled || slot.ItemId is null
                || slot.Group is not (AutoUseSlotState.SlotGroupGeneral or AutoUseSlotState.SlotGroupPotion)
                || slot.ActionType != Domain.GameData.AutoUseBar.Combat) continue;
            var itemId = new ItemId(slot.ItemId);
            if (!gameData.Items.TryGetValue(itemId, out var item)
                || item.Category != ItemDefinition.CategoryConsumable
                || item.Consumable is not { Durable: false } consumable
                || !consumable.UsableInBars.Contains(Domain.GameData.AutoUseBar.Combat)) continue;
            var isSustained = consumable is { IsContinuous: true, DurationSeconds: > 0, Durable: false };
            (list ??= []).Add(new ActionConsumableSlot(slot.ItemId, slot.ActionType, slot.Group, slot.SlotIndex,
                slot.CooldownUntilUnixSeconds,
                isSustained ? slot.RemainingDurationSeconds : 0));
        }
        return list;
    }

    /// <summary>
    /// 开打定格 0 号内功位（在线聚合）：只有 <c>discipline=internal</c> 的装配算内功
    /// （术法位不参与收益重分配）。等级与 <see cref="PlayerAttributeResolver"/> 同口径——
    /// 槽位等级与经验派生等级取高者，保证会话内六维效率 eff 与单人结算逐位一致。
    /// </summary>
    internal static InternalArtSnapshot? CaptureInternalArt(PlayerLoadoutState loadout, GameDataCatalog gameData)
        => CaptureInternalArtCore(loadout.AbilitySlots,
            abilityId => loadout.AbilityExperiences.TryGetValue(new AbilityId(abilityId), out var experience)
                ? experience : 0, gameData);

    /// <summary>开打定格 0 号内功位（持久化投影）：与在线采集同构，经验按功法 ID 文本查表。</summary>
    internal static InternalArtSnapshot? CaptureInternalArt(PlayerLoadoutDocument loadout, GameDataCatalog gameData)
        => CaptureInternalArtCore(loadout.AbilitySlots,
            abilityId => loadout.AbilityExperiences is { } experiences
                && experiences.TryGetValue(abilityId, out var experience) ? experience : 0, gameData);

    private static InternalArtSnapshot? CaptureInternalArtCore(IReadOnlyList<PlayerAbilitySlot> slots,
        Func<string, double> experienceOf, GameDataCatalog gameData)
    {
        if (slots is not { Count: > 0 } || slots[0].AbilityId is not { } abilityId
            || !gameData.Abilities.TryGetValue(abilityId, out var ability)
            || JsonRead.Text(ability, "discipline") != "internal")
            return null;
        var level = Math.Max(1, slots[0].Level);
        var experience = experienceOf(abilityId);
        if (experience > 0) level = Math.Max(level, AbilityExpCurve.LevelFor(experience));
        return new InternalArtSnapshot(abilityId, level);
    }
}

public sealed partial record ActionParticipantSnapshot
{
    /// <summary>从持久化快照重建成员冻结面板（离线/未加载成员路径；与聚合采集同构输出）。</summary>
    public static ActionParticipantSnapshot FromState(PlayerAggregateState state, GameDataCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);
        var attributes = PlayerAttributeResolver.Resolve(state, catalog);
        var loadout = state.Loadout;
        var externalBuffs = new Dictionary<string, double>(StringComparer.Ordinal);
        if (state.Buffs is not null)
            foreach (var buff in state.Buffs)
                externalBuffs[buff.BuffId] = externalBuffs.GetValueOrDefault(buff.BuffId) + buff.Stacks;
        return new ActionParticipantSnapshot(
            state.PlayerId.Value.ToString("D"),
            state.DisplayName,
            state.Progression.RealmLevel,
            attributes.DropBonusPoints,
            attributes.SearchSpeedPoints,
            state.Combat.Hitpoints,
            state.Combat.Manapoints,
            attributes.ToCombatant(state.Progression.CombatExperience),
            SkillSlot.FromAbilitySlots(loadout.AbilitySlots, catalog.Abilities, loadout.AbilityTriggers),
            externalBuffs.Count > 0 ? externalBuffs : null,
            state.Combat.Skills,
            state.Combat.Buffs,
            PlayerAggregate.CaptureSustainedBuffs(state.Settings.AutoUseSlots ?? [], catalog),
            loadout.Equipment,
            state.Combat.RegenCarrySeconds,
            PlayerAggregate.CaptureConsumableSlots(state.Settings.AutoUseSlots ?? [], catalog),
            Avatar: state.Settings.Avatar,
            InternalArt: PlayerAggregate.CaptureInternalArt(loadout, catalog),
            RespawnPolicy: state.Settings.RespawnPolicy,
            RespawnTargetPlayerId: state.Settings.RespawnTargetPlayerId?.ToString("D"));
    }
}
