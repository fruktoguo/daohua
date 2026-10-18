using Domain.Common;
using Domain.Economy;
using Domain.GameData;
using Domain.Player;

namespace Server.Clean;

/// <summary>客户端只能装配自己持有的装备与已经习得的功法，不能修改成长数据。</summary>
public static class LoadoutCommand
{
    /// <summary>
    /// 校验 loadout.replace 载荷并整体替换玩家的功法槽、装备槽与功法触发条件。
    /// JSON 形状与取值错误统一翻译成 InvalidArgument DomainException，
    /// 避免把反序列化细节直接泄露给协议层。
    /// </summary>
    /// <param name="payload">客户端命令载荷（equipment、abilitySlots 数组与 abilityTriggers 功法触发条件 map）</param>
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
        // 装备槽上限 24：战斗槽位与七个技艺工具槽（tool-<技艺>）合计约 18 个，留扩容余量；
        // 功法槽位仍维持 16。
        if (!payload.TryGetProperty("equipment", out var rawEquipment) || rawEquipment.ValueKind != JsonValueKind.Array
            || !payload.TryGetProperty("abilitySlots", out var rawAbilities) || rawAbilities.ValueKind != JsonValueKind.Array
            || rawEquipment.GetArrayLength() > 24 || rawAbilities.GetArrayLength() > 16)
            throw new DomainException(DomainErrorCode.InvalidArgument, "装配槽位载荷无效。");
        var equipment = new List<PlayerEquipmentSlot>();
        var slots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in rawEquipment.EnumerateArray())
        {
            var id = raw.GetProperty("itemId").GetString() ?? throw new JsonException("装备 ID 不能为空。");
            var enhancement = raw.GetProperty("enhancementLevel").GetDouble();
            if (!double.IsFinite(enhancement) || enhancement < 0 || enhancement != Math.Floor(enhancement))
                throw new DomainException(DomainErrorCode.InvalidArgument, "装备强化等级无效。");
            // 强化进度由资产变体承载：+N 装备对应 AssetKey(itemId, "+N") 独立余额行，
            // 变体余额即强化持有证明——装配只校验余额，客户端上报等级不再是信任输入。
            var heldKey = enhancement > 0
                ? new AssetKey(new ItemId(id), FormattableString.Invariant($"+{enhancement:0}"))
                : new AssetKey(new ItemId(id));
            var lockedInEnhance = player.Actions.Count(a => a.Intent.ActionId.Value == "enhance.equipment"
                && string.Equals(a.Intent.TargetItemId, id, StringComparison.Ordinal)
                && (a.EnhancementCurrentLevel ?? (a.Intent.Parameters is not null && a.Intent.Parameters.TryGetValue("initialLevel", out var initLvl) ? (int)Math.Floor(initLvl) : 0)) == (int)enhancement);
            if (!catalog.RawEquipments.TryGetValue(id, out var definition)
                || wallet.Get(heldKey) - lockedInEnhance < 1)
                throw new DomainException(DomainErrorCode.InvalidArgument, "装备不存在、未持有或正处于强化锁定中。");
            var slot = definition.GetProperty("slot").GetString()!;
            // 双手武器独占武器与副手两个槽位，与任一单手槽共存即冲突。
            if (!slots.Add(slot) || (slots.Contains("two-hand") && (slots.Contains("weapon") || slots.Contains("off-hand"))))
                throw new DomainException(DomainErrorCode.InvalidArgument, "装备槽位冲突。");
            equipment.Add(new(id, enhancement));
        }
        var abilities = new List<PlayerAbilitySlot>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var slotIndex = 0;
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
                var ability = catalog.Abilities[id];
                Domain.Combat.SkillTriggerEvaluator.ValidateAbility(ability);
                var learnedLevel = player.AbilityLevelFor(new AbilityId(id));
                if (level > learnedLevel)
                    throw new DomainException(DomainErrorCode.InvalidArgument, "装配等级超过已掌握等级。");
                // 槽位与功法类别互斥：0 号内功位只收心法内功（discipline=internal），
                // 其余术法位只收神通术法；缺省 discipline 一律按术法处理。
                var discipline = ability.TryGetProperty("discipline", out var disciplineElement)
                    && disciplineElement.ValueKind == JsonValueKind.String
                    ? disciplineElement.GetString() : "spell";
                if ((slotIndex == 0) != (discipline == "internal"))
                    throw new DomainException(DomainErrorCode.InvalidArgument,
                        slotIndex == 0 ? "内功位只能装配心法内功。" : "术法位只能装配神通术法。");
                // 装配等级恒等于已参悟等级，自动对齐真实等级，防止旧客户端上报滞后。
                level = learnedLevel;
            }
            abilities.Add(new(id, level));
            slotIndex++;
        }
        // 触发条件跟随功法而非槽位：按功法 ID 收成全量 map，键必须指向已习得功法（不要求当前装配）。
        Dictionary<string, JsonElement[]>? abilityTriggers = null;
        if (payload.TryGetProperty("abilityTriggers", out var rawTriggers))
        {
            if (rawTriggers.ValueKind != JsonValueKind.Object)
                throw new DomainException(DomainErrorCode.InvalidArgument, "功法触发条件载荷无效。");
            abilityTriggers = new Dictionary<string, JsonElement[]>(StringComparer.Ordinal);
            foreach (var entry in rawTriggers.EnumerateObject())
            {
                if (!player.Loadout.LearnedAbilities.Contains(new AbilityId(entry.Name)))
                    throw new DomainException(DomainErrorCode.InvalidArgument, "功法未习得，无法配置触发条件。");
                var triggers = ReadTriggers(entry.Value);
                if (triggers.Length > 0) abilityTriggers[entry.Name] = triggers;
            }
        }
        player.ReplaceCombatSlots(abilities, equipment, abilityTriggers);
    }

    /// <summary>功法触发条件解析：字段词表校验复用领域层统一实现（SkillTriggerEvaluator）。</summary>
    private static JsonElement[] ReadTriggers(JsonElement rules)
    {
        if (rules.ValueKind != JsonValueKind.Array || rules.GetArrayLength() > 4)
            throw new DomainException(DomainErrorCode.InvalidArgument, "触发条件最多四条。");
        foreach (var rule in rules.EnumerateArray())
            Domain.Combat.SkillTriggerEvaluator.ValidateTrigger(rule);
        return rules.EnumerateArray().Select(rule => rule.Clone()).ToArray();
    }
}
