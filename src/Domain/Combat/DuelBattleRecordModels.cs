using System.Text.Json.Serialization;
using Domain.Player;

namespace Domain.Combat;

/// <summary>
/// 斗法紧凑行动记录容量常量：
/// 单个行动实例最多滚动保留 20 场 PVP 斗法，单场最多记录 1000 次行动，避免存档与传输膨胀。
/// </summary>
public static class DuelBattleRecordLimits
{
    /// <summary>单个行动实例账本保留的最近斗法场次上限。</summary>
    public const int MaxRecordsPerAction = 20;
    /// <summary>单场斗法最多记录的行动次数上限。</summary>
    public const int MaxActionsPerRecord = 1000;
    /// <summary>单次行动最多挂载的衍生/主效果条数上限。</summary>
    public const int MaxEffectsPerAction = 64;
    /// <summary>单场记录参战单位表上限。</summary>
    public const int MaxUnitsPerRecord = 16;
    /// <summary>单场记录实体 ID 字典池上限。</summary>
    public const int MaxDefsPerRecord = 256;
}

/// <summary>斗法记录中的行动大类。</summary>
public enum DuelRecordActionKind : byte
{
    /// <summary>普通攻击。</summary>
    BasicAttack = 0,
    /// <summary>功法技能。</summary>
    Skill = 1,
    /// <summary>战斗消耗品（丹药/符箓）。</summary>
    Item = 2,
    /// <summary>阵法激活或布阵。</summary>
    Formation = 3,
    /// <summary>特质独立触发（如开战特质/燃魂每秒灼烧）。</summary>
    Trait = 4,
    /// <summary>周期自然回复与阵盘维持消耗。</summary>
    Periodic = 5
}

/// <summary>单次行动下的具体效果条目类别。</summary>
public enum DuelRecordEffectKind : byte
{
    /// <summary>直接伤害（物理 / 术法 / 真实 + 五行）。</summary>
    Damage = 0,
    /// <summary>直接或周期气血治疗。</summary>
    Heal = 1,
    /// <summary>直接或周期灵力回复。</summary>
    ManaRestore = 2,
    /// <summary>授予或刷新战内状态（Buff/Debuff）。</summary>
    BuffGrant = 3,
    /// <summary>命中气血汲取回复。</summary>
    HpDrain = 4,
    /// <summary>命中灵力汲取。</summary>
    ManaDrain = 5,
    /// <summary>受击伤害反弹。</summary>
    Reflect = 6,
    /// <summary>特质命中衍生爆发（如蓄意）。</summary>
    TraitBurst = 7,
    /// <summary>受击触发阵盘反击（如天雷阵/血煞阵）。</summary>
    FormationStrike = 8,
    /// <summary>阵盘周期维持扣蓝/扣血。</summary>
    UpkeepCost = 9,
    /// <summary>目标在此次效果后气血归零倒地。</summary>
    Downed = 10
}

/// <summary>命中与结算判定标记位。</summary>
[Flags]
public enum DuelRecordHitMark : byte
{
    /// <summary>无特殊标记。</summary>
    None = 0,
    /// <summary>命中。</summary>
    Hit = 1 << 0,
    /// <summary>被闪避。</summary>
    Dodge = 1 << 1,
    /// <summary>暴击。</summary>
    Crit = 1 << 2,
    /// <summary>破招。</summary>
    Break = 1 << 3,
    /// <summary>化解。</summary>
    Parry = 1 << 4
}

/// <summary>斗法记录中的参战单位条目。</summary>
public sealed record DuelRecordUnitDocument(
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("ally"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool IsAlly = false,
    [property: JsonPropertyName("pid"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? PlayerId = null)
{
    /// <summary>校验参战单位字段合法性。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Name) && Name.Length <= 64
        && (PlayerId is null || PlayerId.Length <= 64);
}

/// <summary>单次行动下的一条紧凑效果明细（短键名存储，不存文本）。</summary>
public sealed record DuelRecordEffectDocument(
    [property: JsonPropertyName("s")] int Source,
    [property: JsonPropertyName("tg")] int Target,
    [property: JsonPropertyName("ek")] DuelRecordEffectKind Kind,
    [property: JsonPropertyName("dk"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] CombatDamageKind DamageKind = CombatDamageKind.Physical,
    [property: JsonPropertyName("el"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] CombatElement Element = CombatElement.None,
    [property: JsonPropertyName("fl"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] DuelRecordHitMark Flags = DuelRecordHitMark.None,
    [property: JsonPropertyName("v"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] double Value = 0,
    [property: JsonPropertyName("df"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DefIndex = null)
{
    /// <summary>校验效果明细索引、枚举与数值有效性。</summary>
    public bool IsValid(int unitCount, int defCount)
        => Source >= 0 && Source < unitCount
            && Target >= 0 && Target < unitCount
            && Enum.IsDefined(Kind)
            && Enum.IsDefined(DamageKind)
            && Enum.IsDefined(Element)
            && double.IsFinite(Value) && Value >= 0
            && (DefIndex is null || (DefIndex.Value >= 0 && DefIndex.Value < defCount));
}

/// <summary>单次行动记录（含行动主体、源定义索引及合并挂载的全部效果）。</summary>
public sealed record DuelRecordActionDocument(
    [property: JsonPropertyName("t")] double AtSeconds,
    [property: JsonPropertyName("a")] int Actor,
    [property: JsonPropertyName("k")] DuelRecordActionKind Kind,
    [property: JsonPropertyName("d"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DefIndex = null,
    [property: JsonPropertyName("o"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? OverrideIndex = null,
    [property: JsonPropertyName("fx")] IReadOnlyList<DuelRecordEffectDocument>? Effects = null)
{
    /// <summary>校验行动条目索引、时钟与效果列表完整性。</summary>
    public bool IsValid(int unitCount, int defCount)
        => double.IsFinite(AtSeconds) && AtSeconds >= 0
            && Actor >= 0 && Actor < unitCount
            && Enum.IsDefined(Kind)
            && (DefIndex is null || (DefIndex.Value >= 0 && DefIndex.Value < defCount))
            && (OverrideIndex is null || (OverrideIndex.Value >= 0 && OverrideIndex.Value < defCount))
            && Effects is not null && Effects.Count <= DuelBattleRecordLimits.MaxEffectsPerAction
            && Effects.All(effect => effect is not null && effect.IsValid(unitCount, defCount));
}

/// <summary>
/// 单场 PVP 斗法紧凑记录：以单位下标表（u）+ 实体 ID 字典池（defs）+ 行动流（acts）落盘，
/// 彻底省去服务端拼接长文本，由前端按本地化词表与图标组件拼装渲染。
/// </summary>
public sealed record DuelBattleRecordDocument(
    [property: JsonPropertyName("id")] string DuelId,
    [property: JsonPropertyName("at")] double OccurredAtUnixSeconds,
    [property: JsonPropertyName("dur")] double DurationSeconds,
    [property: JsonPropertyName("out")] CombatOutcome Outcome,
    [property: JsonPropertyName("ff"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Forfeited = false,
    [property: JsonPropertyName("tr"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Truncated = false,
    [property: JsonPropertyName("u")] IReadOnlyList<DuelRecordUnitDocument>? Units = null,
    [property: JsonPropertyName("defs")] IReadOnlyList<string>? Defs = null,
    [property: JsonPropertyName("acts")] IReadOnlyList<DuelRecordActionDocument>? Actions = null)
{
    /// <summary>校验单场斗法紧凑记录不变量（防畸形存档或超限注入）。</summary>
    public bool IsValid
    {
        get
        {
            if (string.IsNullOrWhiteSpace(DuelId) || DuelId.Length > 64) return false;
            if (!double.IsFinite(OccurredAtUnixSeconds) || OccurredAtUnixSeconds < 0) return false;
            if (!double.IsFinite(DurationSeconds) || DurationSeconds < 0) return false;
            if (!Enum.IsDefined(Outcome)) return false;
            if (Units is null || Units.Count == 0 || Units.Count > DuelBattleRecordLimits.MaxUnitsPerRecord) return false;
            if (Defs is null || Defs.Count > DuelBattleRecordLimits.MaxDefsPerRecord) return false;
            if (Actions is null || Actions.Count > DuelBattleRecordLimits.MaxActionsPerRecord) return false;
            for (var i = 0; i < Units.Count; i++)
                if (Units[i] is null || !Units[i].IsValid) return false;
            for (var i = 0; i < Defs.Count; i++)
                if (string.IsNullOrWhiteSpace(Defs[i]) || Defs[i].Length > 128) return false;
            for (var i = 0; i < Actions.Count; i++)
                if (Actions[i] is null || !Actions[i].IsValid(Units.Count, Defs.Count)) return false;
            return true;
        }
    }

    /// <summary>
    /// 按观看视角终局状态与可选时间上限切片（用于实时斗法防剧透切片与弃战截断）。
    /// </summary>
    public DuelBattleRecordDocument ForViewerSlice(
        CombatOutcome outcome,
        bool forfeited,
        double occurredAtUnixSeconds,
        double? maxAtSeconds = null)
    {
        if (maxAtSeconds is null || Actions is null || Actions.Count == 0)
        {
            return this with
            {
                Outcome = outcome,
                Forfeited = forfeited,
                OccurredAtUnixSeconds = occurredAtUnixSeconds
            };
        }

        var cutoff = Math.Max(0, maxAtSeconds.Value);
        var sliced = new List<DuelRecordActionDocument>(Actions.Count);
        for (var i = 0; i < Actions.Count; i++)
        {
            var action = Actions[i];
            if (action.AtSeconds > cutoff + 1e-9) break;
            sliced.Add(action);
        }

        return this with
        {
            Outcome = outcome,
            Forfeited = forfeited,
            OccurredAtUnixSeconds = occurredAtUnixSeconds,
            DurationSeconds = Math.Min(DurationSeconds, Math.Round(cutoff, 3)),
            Truncated = Truncated && sliced.Count >= DuelBattleRecordLimits.MaxActionsPerRecord,
            Actions = sliced
        };
    }
}

/// <summary>
/// 战斗行动紧凑录制器接口：仅在挂接了斗法录制器时采集已结算动作与衍生效果，
/// 严禁影响随机数序列、属性面板或任何战斗裁决逻辑。
/// </summary>
public interface ICombatActionRecorder
{
    /// <summary>开启一次单位行动作用域；嵌套调用时外层已开启作用域保持不变。</summary>
    void BeginAction(double atSeconds, CombatantState actor, DuelRecordActionKind kind,
        string? defId = null, string? overrideName = null);

    /// <summary>结束最近开启的行动作用域并收口落入行动列表。</summary>
    void EndAction();

    /// <summary>记录一次直接伤害、反伤、特质爆发或阵盘反击伤害。</summary>
    void RecordDamage(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        CombatDamageKind damageKind, CombatElement element, DuelRecordHitMark flags,
        double damage, string? defId = null, bool targetDowned = false);

    /// <summary>记录一次气血治疗或吸血效果。</summary>
    void RecordHeal(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        double amount, string? defId = null);

    /// <summary>记录一次灵力回复或灵力汲取效果。</summary>
    void RecordMana(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        double amount, string? defId = null);

    /// <summary>记录一次阵盘/周期维持消耗。</summary>
    void RecordUpkeep(CombatantState unit, double amount, string defId);

    /// <summary>记录一次状态（Buff/Debuff）授予或叠层；未在行动作用域内时自动开启独立行动条目。</summary>
    void RecordBuff(CombatantState source, CombatantState target, string buffId, double stacks,
        DuelRecordActionKind fallbackKind = DuelRecordActionKind.Trait, double atSeconds = 0, string? fallbackDefId = null);
}
