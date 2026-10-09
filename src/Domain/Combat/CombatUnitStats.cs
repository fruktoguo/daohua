namespace Domain.Combat;

/// <summary>战斗统计单位类别：本机玩家 / 参与会话盟友 / 怪物类型桶。</summary>
public enum CombatStatUnitKind : byte
{
    /// <summary>本机玩家。</summary>
    Self = 0,
    /// <summary>组队共战盟友（共享剧本成员）；斗法对方玩家队伍不入账。</summary>
    Ally = 1,
    /// <summary>怪物类型桶：同定义 ID 的全部怪物合并累计为一个单位。</summary>
    Monster = 2
}

/// <summary>
/// 行动内单个统计单位的战斗累计行：伤害输出/实收/减免按物理·术法分列，
/// 治疗分自动回复与其他治疗。减免 = 命中时原始威力（减免链前）− 实际落账伤害；未命中不产生任何落账。
/// </summary>
public sealed class CombatUnitStatEntry
{
    internal CombatUnitStatEntry(string unitKey, CombatStatUnitKind kind)
    {
        UnitKey = unitKey;
        Kind = kind;
        DisplayName = "";
    }

    /// <summary>单位键：player / ally:{成员下标} / monster:{怪物定义 ID}。</summary>
    public string UnitKey { get; }
    /// <summary>单位类别。</summary>
    public CombatStatUnitKind Kind { get; }
    /// <summary>展示名（道号/成员名/怪物名）。</summary>
    public string DisplayName { get; internal set; }
    /// <summary>造成物理伤害。</summary>
    public double DamageDealtPhysical { get; internal set; }
    /// <summary>造成术法伤害。</summary>
    public double DamageDealtSpell { get; internal set; }
    /// <summary>实收物理伤害。</summary>
    public double DamageTakenPhysical { get; internal set; }
    /// <summary>实收术法伤害。</summary>
    public double DamageTakenSpell { get; internal set; }
    /// <summary>减免物理伤害（原始威力 − 实收）。</summary>
    public double DamageMitigatedPhysical { get; internal set; }
    /// <summary>减免术法伤害。</summary>
    public double DamageMitigatedSpell { get; internal set; }
    /// <summary>自动回复量（战内 5 秒 tick 与行动期间战外自然回复）。</summary>
    public double HealingRegen { get; internal set; }
    /// <summary>其他治疗输出量（技能治疗、气血汲取、药品瞬回；按施放方归属）。</summary>
    public double HealingOther { get; internal set; }
}

/// <summary>单个统计单位累计行的可持久化文档形态；随行动账本报表落库并下发。</summary>
public sealed record CombatUnitStatDocument(
    string UnitKey,
    CombatStatUnitKind Kind,
    string DisplayName,
    double DamageDealtPhysical,
    double DamageDealtSpell,
    double DamageTakenPhysical,
    double DamageTakenSpell,
    double DamageMitigatedPhysical,
    double DamageMitigatedSpell,
    double HealingRegen,
    double HealingOther);

/// <summary>
/// 行动级战斗单位统计簿：挂在行动实例账本（PlayerBattleReport.CombatStats）上随 duel 分段持久化，
/// 覆盖本次行动全部波次与相位；行动出队归档时清空（统计即弃，不进入归档战报）。
/// </summary>
public sealed class CombatUnitStatsBook
{
    private readonly List<CombatUnitStatEntry> _entries = [];
    private readonly Dictionary<string, CombatUnitStatEntry> _byKey = new(StringComparer.Ordinal);

    /// <summary>按登记顺序排列的全部单位累计行。</summary>
    public IReadOnlyList<CombatUnitStatEntry> Entries => _entries;

    /// <summary>自上次 <see cref="ClearDirty"/> 后是否有写入；推进边界据此决定账本标脏与文档重建。</summary>
    internal bool Dirty { get; private set; }

    /// <summary>消费脏标记：标脏与文档重建完成后调用。</summary>
    internal void ClearDirty() => Dirty = false;

    /// <summary>
    /// 战斗单位键映射：本机成员 player、盟友 ally:{成员下标}、
    /// 敌方按怪物定义 ID 折叠为 monster:{defId}（同类型合并计一个单位）；无定义 ID 的敌方沿用原 ID。
    /// </summary>
    public static string KeyOf(CombatantState unit)
        => unit.Id.StartsWith("enemy:", StringComparison.Ordinal) && unit.DefinitionId is { } definitionId
            ? $"monster:{definitionId}"
            : unit.Id;

    /// <summary>按单位键取或建累计行；类别由键前缀推导，展示名留空待注册回填。</summary>
    public CombatUnitStatEntry Entry(string unitKey)
    {
        if (_byKey.TryGetValue(unitKey, out var entry)) return entry;
        entry = new CombatUnitStatEntry(unitKey, KindOf(unitKey));
        _byKey.Add(unitKey, entry);
        _entries.Add(entry);
        Dirty = true;
        return entry;
    }

    /// <summary>注册单位展示名；已存在的行仅在尚无名字时回填，不覆盖既有名字。</summary>
    public CombatUnitStatEntry Register(string unitKey, string displayName)
    {
        var entry = Entry(unitKey);
        if (entry.DisplayName.Length == 0 && displayName.Length > 0)
        {
            entry.DisplayName = displayName;
            Dirty = true;
        }
        return entry;
    }

    /// <summary>按命中结算记账一次攻防：攻方计伤害输出、守方计实收与减免（减免口径 RawDamage−Damage）。</summary>
    public void RecordHit(CombatantState attacker, CombatantState target, CombatDamageKind kind,
        double damage, double mitigated)
        => RecordHit(KeyOf(attacker), KeyOf(target), kind, damage, mitigated);

    /// <summary>按单位键记账一次攻防；伤害与减免均 ≤0 时（未命中）不产生落账。</summary>
    public void RecordHit(string attackerKey, string targetKey, CombatDamageKind kind,
        double damage, double mitigated)
    {
        if (damage <= 0 && mitigated <= 0) return;
        var attacker = Entry(attackerKey);
        var target = Entry(targetKey);
        if (kind == CombatDamageKind.Spell)
        {
            attacker.DamageDealtSpell += damage;
            target.DamageTakenSpell += damage;
            target.DamageMitigatedSpell += mitigated;
        }
        else
        {
            attacker.DamageDealtPhysical += damage;
            target.DamageTakenPhysical += damage;
            target.DamageMitigatedPhysical += mitigated;
        }
        Dirty = true;
    }

    /// <summary>记账一次治疗：autoRegen=true 记自动回复，否则记其他治疗输出。</summary>
    public void RecordHealing(CombatantState unit, bool autoRegen, double amount)
        => RecordHealing(KeyOf(unit), "", autoRegen, amount);

    /// <summary>按单位键记账一次治疗；displayName 仅在该行尚无名字时回填。</summary>
    public void RecordHealing(string unitKey, string displayName, bool autoRegen, double amount)
    {
        if (amount <= 0) return;
        var entry = displayName.Length > 0 ? Register(unitKey, displayName) : Entry(unitKey);
        if (autoRegen) entry.HealingRegen += amount;
        else entry.HealingOther += amount;
        Dirty = true;
    }

    /// <summary>斗法本方结算并入：把本方单位统计折算到本机 player 行（对方队伍数据不入账）。</summary>
    public void MergeSelf(string displayName, CombatUnitStatDocument stats)
    {
        var entry = Register("player", displayName);
        entry.DamageDealtPhysical += stats.DamageDealtPhysical;
        entry.DamageDealtSpell += stats.DamageDealtSpell;
        entry.DamageTakenPhysical += stats.DamageTakenPhysical;
        entry.DamageTakenSpell += stats.DamageTakenSpell;
        entry.DamageMitigatedPhysical += stats.DamageMitigatedPhysical;
        entry.DamageMitigatedSpell += stats.DamageMitigatedSpell;
        entry.HealingRegen += stats.HealingRegen;
        entry.HealingOther += stats.HealingOther;
    }

    /// <summary>清空全部行（行动归档即弃与账本 rollover 用）。</summary>
    public void Clear()
    {
        if (_entries.Count == 0) return;
        _entries.Clear();
        _byKey.Clear();
        Dirty = true;
    }

    /// <summary>从持久化文档恢复一行（账本恢复路径专用，覆盖式写值）。</summary>
    internal void Import(CombatUnitStatDocument document)
    {
        var entry = Register(document.UnitKey, document.DisplayName);
        entry.DamageDealtPhysical = document.DamageDealtPhysical;
        entry.DamageDealtSpell = document.DamageDealtSpell;
        entry.DamageTakenPhysical = document.DamageTakenPhysical;
        entry.DamageTakenSpell = document.DamageTakenSpell;
        entry.DamageMitigatedPhysical = document.DamageMitigatedPhysical;
        entry.DamageMitigatedSpell = document.DamageMitigatedSpell;
        entry.HealingRegen = document.HealingRegen;
        entry.HealingOther = document.HealingOther;
    }

    private static CombatStatUnitKind KindOf(string unitKey)
        => unitKey == "player" ? CombatStatUnitKind.Self
            : unitKey.StartsWith("ally:", StringComparison.Ordinal) ? CombatStatUnitKind.Ally
            : CombatStatUnitKind.Monster;
}
