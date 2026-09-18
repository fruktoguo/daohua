using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;

namespace Server.Clean;

/// <summary>客户端只能装配自己持有的装备与已经习得的功法，不能修改成长数据。</summary>
internal static class LoadoutCommand
{
    /// <summary>
    /// 校验 loadout.replace 载荷并整体替换玩家的功法槽与装备槽。
    /// JSON 形状与取值错误统一翻译成 InvalidArgument DomainException，
    /// 避免把反序列化细节直接泄露给协议层。
    /// </summary>
    /// <param name="payload">客户端命令载荷（equipment 与 abilitySlots 数组）</param>
    /// <param name="player">目标玩家聚合</param>
    /// <param name="wallet">玩家资产钱包，用于校验装备确已持有</param>
    /// <param name="catalog">当前激活配表，用于校验装备与功法定义</param>
    public static void Apply(JsonElement payload, PlayerAggregate player, AssetWallet wallet, GameDataCatalog catalog)
    {
        try { ApplyValidated(payload, player, wallet, catalog); }
        catch (Exception error) when (error is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配字段缺失或类型无效。");
        }
    }

    private static void ApplyValidated(JsonElement payload, PlayerAggregate player, AssetWallet wallet, GameDataCatalog catalog)
    {
        if (!payload.TryGetProperty("equipment", out var rawEquipment) || rawEquipment.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("abilitySlots", out var rawAbilities) || rawAbilities.ValueKind != JsonValueKind.Array
            || rawEquipment.GetArrayLength() > 16 || rawAbilities.GetArrayLength() > 16)
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配槽位载荷无效。");
        var equipment = new List<PlayerEquipmentSlot>();
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawEquipment.EnumerateArray())
        {
            var id = raw.GetProperty("itemId").GetString() ?? throw new JsonException("装备 ID 不能为空。");
            var enhancement = raw.GetProperty("enhancementLevel").GetDouble();
            if (!catalog.RawEquipments.TryGetValue(id, out var definition)
                || wallet.Get(new AssetKey(new ItemId(id))) < 1)
                throw new DomainException(DomainErrorCode.InvalidArgument, "装备不存在或未持有。");
            var slot = definition.GetProperty("slot").GetString()!;
            // 双手武器独占武器与副手两个槽位，与任一单手槽共存即冲突。
            if (!slots.Add(slot) || (slots.Contains("two-hand") && (slots.Contains("weapon") || slots.Contains("off-hand"))))
                throw new DomainException(DomainErrorCode.InvalidArgument, "装备槽位冲突。");
            var ownedLevel = player.Loadout.Equipment.FirstOrDefault(item => item.ItemId == id)?.EnhancementLevel ?? 0;
            // 强化等级以玩家已持有记录为准，装配命令不得伪造强化进度。
            if (enhancement != ownedLevel)
                throw new DomainException(DomainErrorCode.InvalidArgument, "装配不能修改装备强化等级。");
            equipment.Add(new(id, enhancement));
        }
        var abilities = new List<PlayerAbilitySlot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawAbilities.EnumerateArray())
        {
            var id = raw.TryGetProperty("abilityId", out var idElement) && idElement.ValueKind != JsonValueKind.Null ? idElement.GetString() : null;
            var level = raw.GetProperty("level").GetDouble();
            if (!double.IsFinite(level) || level < 1 || level != Math.Floor(level))
                throw new DomainException(DomainErrorCode.InvalidArgument, "功法等级无效。");
            if (id is not null)
            {
                if (!catalog.Abilities.ContainsKey(id) || !player.Loadout.LearnedAbilities.Contains(new AbilityId(id)) || !ids.Add(id))
                    throw new DomainException(DomainErrorCode.InvalidArgument, "功法未习得或重复装配。");
                Domain.Combat.CombatSkillEngine.ValidateAbility(catalog.Abilities[id]);
                var learnedLevel = player.AbilityLevelFor(new AbilityId(id));
                if (level > learnedLevel)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "装配等级超过已掌握等级。");
            }
            var triggers = raw.TryGetProperty("triggers", out var rules) ? ReadTriggers(rules) : [];
            abilities.Add(new(id, level, triggers));
        }
        player.ReplaceCombatSlots(abilities, equipment);
    }

    private static JsonElement[] ReadTriggers(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > 4)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件最多四条。");
        foreach (var rule in rules.EnumerateArray())
        {
            var dependency = rule.GetProperty("dependency").GetString();
            var condition = rule.GetProperty("condition").GetString();
            var comparator = rule.GetProperty("comparator").GetString();
            var value = rule.GetProperty("value").GetDouble();
            if (dependency is not ("self" or "target-enemy" or "ally-lowest-hp" or "ally-highest-hp" or "enemy-lowest-hp" or "enemy-highest-hp")
                || condition is not ("current-hp" or "current-mana" or "missing-hp" or "missing-mana" or "current-hp-percentage" or "missing-hp-percentage" or "current-mana-percentage" or "missing-mana-percentage" or "buff-stacks" or "stun-status" or "blind-status" or "silence-status")
                || comparator is not ("less-than-equal" or "greater-than-equal" or "is-active" or "is-inactive" or "less-than" or "greater-than")
                || !double.IsFinite(value) || value < 0)
                throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件字段无效。");
        }
        return rules.EnumerateArray().Select(rule => rule.Clone()).ToArray();
    }
}
