// ==============================================================================
// 文件名称: PlayerStateReconciler.cs
// 主要职责: 提供基于最新静态配表快照的玩家领域状态容错自愈与清洗服务 (PlayerStateReconciler)。
// 核心逻辑 / 定义内容:
//   1. 遍历背包与装备，安全剔除配表中已删除的物品与失效装备
//   2. 遍历技能与快捷栏，剔除已删除的技能并置空失效槽位
//   3. 检查动作队列与战斗历练态，安全移除失效动作与孤立遭遇，防止队列推进死锁
//   4. 校验大世界坐标与自动消耗品槽位，安全回退至合法城镇节点
//   5. 汇总生成结构化自愈报告 (ReconciliationReport)，便于审计与监控
// ==============================================================================

using Domain.GameData;
using Domain.Models;

namespace Domain.Simulation;

/// <summary>
/// 玩家状态容错自愈报告。
/// </summary>
public sealed class ReconciliationReport
{
    /// <summary>快照指纹是否与当前系统不一致。</summary>
    public bool FingerprintMismatched { get; set; }
    /// <summary>快照原本记录的指纹。</summary>
    public string? OriginalFingerprint { get; set; }
    /// <summary>目标系统当前配表指纹。</summary>
    public string TargetFingerprint { get; set; } = string.Empty;
    /// <summary>剔除的失效背包物品种类数。</summary>
    public int RemovedInventoryItems { get; set; }
    /// <summary>卸下并抹除的失效装备件数。</summary>
    public int RemovedEquipment { get; set; }
    /// <summary>清除的失效已学技能数。</summary>
    public int RemovedLearnedAbilities { get; set; }
    /// <summary>置空的失效技能槽位数。</summary>
    public int ResetAbilitySlots { get; set; }
    /// <summary>出队的失效队列动作数。</summary>
    public int RemovedQueueActions { get; set; }
    /// <summary>解绑置空的失效自动吃药槽位数。</summary>
    public int ResetAutoUseSlots { get; set; }
    /// <summary>是否重置了世界地图停驻节点。</summary>
    public bool ResetWorldNode { get; set; }
    /// <summary>是否重置了失效的战斗历练运行态。</summary>
    public bool ResetCombatExpedition { get; set; }

    /// <summary>是否存在任何清洗或修正变更。</summary>
    public bool HasChanges => FingerprintMismatched
        || RemovedInventoryItems > 0
        || RemovedEquipment > 0
        || RemovedLearnedAbilities > 0
        || ResetAbilitySlots > 0
        || RemovedQueueActions > 0
        || ResetAutoUseSlots > 0
        || ResetWorldNode
        || ResetCombatExpedition;

    /// <summary>
    /// 生成紧凑的可读自愈摘要字符串。
    /// </summary>
    public string ToSummaryString() =>
        $"指纹变更[{OriginalFingerprint ?? "none"} -> {TargetFingerprint}], " +
        $"剔除失效物品:{RemovedInventoryItems}, 卸下失效装备:{RemovedEquipment}, " +
        $"清除失效技能:{RemovedLearnedAbilities}, 重置技能槽:{ResetAbilitySlots}, " +
        $"出队失效动作:{RemovedQueueActions}, 解绑吃药槽:{ResetAutoUseSlots}, " +
        $"地图节点重置:{(ResetWorldNode ? "是" : "否")}, 历练重置:{(ResetCombatExpedition ? "是" : "否")}";
}

/// <summary>
/// 纯领域层玩家状态容错自愈清洗器 (PlayerStateReconciler)。
/// </summary>
public static class PlayerStateReconciler
{
    /// <summary>
    /// 根据当前静态数据配表快照，清洗玩家状态中的孤立与失效引用，保留全部合法兼容数据。
    /// </summary>
    /// <param name="state">待清洗的玩家聚合状态。</param>
    /// <param name="catalog">当前生效的游戏静态数据配表快照。</param>
    /// <param name="originalFingerprint">快照信封原本记录的指纹。</param>
    /// <returns>自愈报告详情。</returns>
    public static ReconciliationReport Reconcile(
        PlayerState state,
        GameDataCatalogSnapshot catalog,
        string? originalFingerprint = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(catalog);

        var report = new ReconciliationReport
        {
            OriginalFingerprint = originalFingerprint,
            TargetFingerprint = catalog.Fingerprint,
            FingerprintMismatched = !string.Equals(originalFingerprint, catalog.Fingerprint, StringComparison.Ordinal)
        };

        // 1. 清洗背包物品 (Inventory)
        if (state.Inventory?.Items is { Count: > 0 } items)
        {
            var invalidItemIds = items.Keys
                .Where(itemId => !catalog.Items.ContainsKey(itemId))
                .ToList();

            foreach (var itemId in invalidItemIds)
            {
                items.Remove(itemId);
                report.RemovedInventoryItems++;
            }
        }

        // 2. 清洗穿戴装备 (Combat.Equipment)
        if (state.Combat?.Equipment is { Count: > 0 } equipmentList)
        {
            var invalidEquipments = equipmentList
                .Where(equipped => !catalog.Items.ContainsKey(equipped.ItemId) || !catalog.Equipment.ContainsKey(equipped.ItemId))
                .ToList();

            foreach (var invalid in invalidEquipments)
            {
                equipmentList.Remove(invalid);
                report.RemovedEquipment++;
            }
        }

        // 3. 清洗已学技能与技能槽位 (Combat.LearnedAbilityIds & AbilitySlots)
        if (state.Combat?.LearnedAbilityIds is { Count: > 0 } learnedAbilities)
        {
            var invalidAbilities = learnedAbilities
                .Where(abilityId => !catalog.Abilities.ContainsKey(abilityId))
                .ToList();

            foreach (var invalid in invalidAbilities)
            {
                learnedAbilities.Remove(invalid);
                report.RemovedLearnedAbilities++;
            }
        }

        if (state.Combat?.AbilitySlots is { Count: > 0 } abilitySlots)
        {
            foreach (var slot in abilitySlots)
            {
                if (slot.AbilityId is { Length: > 0 } abilityId && !catalog.Abilities.ContainsKey(abilityId))
                {
                    slot.AbilityId = null;
                    slot.Triggers.Clear();
                    report.ResetAbilitySlots++;
                }
            }
        }

        // 4. 清洗战斗消耗品快捷槽位 (Combat.ConsumableSlots)
        if (state.Combat?.ConsumableSlots is { Count: > 0 } consumableSlots)
        {
            foreach (var slot in consumableSlots)
            {
                if (slot.ItemId is { Length: > 0 } itemId && !catalog.Items.ContainsKey(itemId))
                {
                    slot.ItemId = null;
                    slot.Enabled = false;
                    slot.Triggers.Clear();
                    report.ResetAutoUseSlots++;
                }
            }
        }

        // 5. 清洗动作队列各分类自动使用槽位 (Queue.AutoUseSlots)
        if (state.Queue?.AutoUseSlots is { Count: > 0 } queueAutoUse)
        {
            foreach (var slotList in queueAutoUse.Values)
            {
                foreach (var slot in slotList)
                {
                    if (slot.ItemId is { Length: > 0 } itemId && !catalog.Items.ContainsKey(itemId))
                    {
                        slot.ItemId = null;
                        slot.Enabled = false;
                        slot.Triggers?.Clear();
                        report.ResetAutoUseSlots++;
                    }
                }
            }
        }

        // 6. 清洗动作队列排队项 (Queue.Entries)
        if (state.Queue?.Entries is { Count: > 0 } entries)
        {
            var invalidEntries = new List<QueuedAction>();
            foreach (var action in entries)
            {
                if (!catalog.Actions.TryGetValue(action.ActionId, out var actionDef))
                {
                    invalidEntries.Add(action);
                    continue;
                }

                if (actionDef.CombatEncounterId is { Length: > 0 } encounterId && !catalog.Encounters.ContainsKey(encounterId))
                {
                    invalidEntries.Add(action);
                    continue;
                }

                if (action.TravelPlan is not null)
                {
                    if (!catalog.Maps.TryGetValue(action.TravelPlan.MapId, out var mapDef)
                        || !mapDef.Nodes.Any(n => string.Equals(n.Id, action.TravelPlan.DestinationNodeId, StringComparison.Ordinal)))
                    {
                        invalidEntries.Add(action);
                        continue;
                    }
                }

                if (action.CombatExpedition is not null)
                {
                    var expEncounterId = action.CombatExpedition.BattleSnapshot?.EncounterId ?? actionDef.CombatEncounterId;
                    if (expEncounterId is { Length: > 0 } && !catalog.Encounters.ContainsKey(expEncounterId))
                    {
                        action.CombatExpedition = null;
                        action.Status = ActionStatus.Queued;
                        report.ResetCombatExpedition = true;
                    }
                }
            }

            foreach (var invalid in invalidEntries)
            {
                entries.Remove(invalid);
                report.RemovedQueueActions++;
            }
        }

        // 7. 校验大世界坐标位置 (World)
        if (state.World is not null)
        {
            var maps = catalog.Maps;
            if (maps.Count > 0)
            {
                if (string.IsNullOrWhiteSpace(state.World.MapId) || !maps.TryGetValue(state.World.MapId, out var currentMap))
                {
                    var fallbackMap = maps.Values.First();
                    state.World.MapId = fallbackMap.Id;
                    state.World.NodeId = fallbackMap.Nodes.FirstOrDefault()?.Id;
                    state.World.EdgeId = null;
                    state.World.EdgeProgress = 0;
                    report.ResetWorldNode = true;
                }
                else if (state.World.NodeId is { Length: > 0 } nodeId && !currentMap.Nodes.Any(n => string.Equals(n.Id, nodeId, StringComparison.Ordinal)))
                {
                    state.World.NodeId = currentMap.Nodes.FirstOrDefault()?.Id;
                    state.World.EdgeId = null;
                    state.World.EdgeProgress = 0;
                    report.ResetWorldNode = true;
                }
            }
        }

        // 8. 清洗战斗资源冷却中的失效技能 (CombatResources.AbilityCooldowns)
        if (state.CombatResources?.AbilityCooldowns is { Count: > 0 } cooldowns)
        {
            var invalidCooldowns = cooldowns.Keys
                .Where(abilityId => !catalog.Abilities.ContainsKey(abilityId))
                .ToList();

            foreach (var abilityId in invalidCooldowns)
            {
                cooldowns.Remove(abilityId);
            }
        }

        return report;
    }
}
