using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 斗法紧凑行动录制器：在 DuelScriptEngine.Resolve 期间挂载到 encounter.ActionRecorder，
/// 观察全部战斗出手、技能/物品/阵法/特质/周期效果，并输出双方各自视角的紧凑记录。
/// 严禁修改任何战斗状态或调用随机数。
/// </summary>
public sealed class DuelActionRecorder : ICombatActionRecorder
{
    private sealed class MutableAction(
        double atSeconds,
        int actor,
        DuelRecordActionKind kind,
        int? defIndex,
        int? overrideIndex)
    {
        public double AtSeconds { get; } = atSeconds;
        public int Actor { get; } = actor;
        public DuelRecordActionKind Kind { get; } = kind;
        public int? DefIndex { get; } = defIndex;
        public int? OverrideIndex { get; } = overrideIndex;
        public List<DuelRecordEffectDocument> Effects { get; } = [];
    }

    private readonly List<DuelRecordUnitDocument> _units = [];
    private readonly Dictionary<string, int> _unitIndexById = new(StringComparer.Ordinal);
    private readonly List<string> _defs = [];
    private readonly Dictionary<string, int> _defIndexById = new(StringComparer.Ordinal);
    private readonly List<MutableAction> _actions = [];
    private MutableAction? _active;
    private int _scopeDepth;
    private bool _truncated;

    /// <summary>以发起方（player）和应战方（enemy:0）初始化参战单位表。</summary>
    public DuelActionRecorder(DuelCombatant initiator, DuelCombatant defender)
    {
        RegisterUnit("player", new DuelRecordUnitDocument(initiator.DisplayName, IsAlly: true, initiator.PlayerId.Value.ToString("N")));
        RegisterUnit("enemy:0", new DuelRecordUnitDocument(defender.DisplayName, IsAlly: false, defender.PlayerId.Value.ToString("N")));
    }

    private void RegisterUnit(string combatantId, DuelRecordUnitDocument unit)
    {
        if (_unitIndexById.ContainsKey(combatantId) || _units.Count >= DuelBattleRecordLimits.MaxUnitsPerRecord) return;
        _unitIndexById[combatantId] = _units.Count;
        _units.Add(unit);
    }

    private int UnitIndex(CombatantState unit)
    {
        if (_unitIndexById.TryGetValue(unit.Id, out var idx)) return idx;
        if (_units.Count >= DuelBattleRecordLimits.MaxUnitsPerRecord) return 0;
        idx = _units.Count;
        _unitIndexById[unit.Id] = idx;
        _units.Add(new DuelRecordUnitDocument(unit.DefinitionId ?? unit.Id, IsAlly: unit.IsPlayer));
        return idx;
    }

    private int? InternDef(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var normalized = id.Length <= 128 ? id : id[..128];
        if (_defIndexById.TryGetValue(normalized, out var idx)) return idx;
        if (_defs.Count >= DuelBattleRecordLimits.MaxDefsPerRecord) return null;
        idx = _defs.Count;
        _defIndexById[normalized] = idx;
        _defs.Add(normalized);
        return idx;
    }

    /// <inheritdoc />
    public void BeginAction(double atSeconds, CombatantState actor, DuelRecordActionKind kind,
        string? defId = null, string? overrideName = null)
    {
        if (_scopeDepth++ > 0) return;
        if (_actions.Count >= DuelBattleRecordLimits.MaxActionsPerRecord)
        {
            _truncated = true;
            _active = null;
            return;
        }
        _active = new MutableAction(
            Math.Round(Math.Max(0, atSeconds), 3),
            UnitIndex(actor),
            kind,
            InternDef(defId),
            InternDef(overrideName));
    }

    /// <inheritdoc />
    public void EndAction()
    {
        if (_scopeDepth <= 0) return;
        _scopeDepth--;
        if (_scopeDepth > 0 || _active is null) return;
        if (_active.Effects.Count > 0 && _actions.Count < DuelBattleRecordLimits.MaxActionsPerRecord)
            _actions.Add(_active);
        _active = null;
    }

    private void AppendEffect(DuelRecordEffectDocument effect)
    {
        if (_active is null || _active.Effects.Count >= DuelBattleRecordLimits.MaxEffectsPerAction) return;
        _active.Effects.Add(effect);
    }

    /// <inheritdoc />
    public void RecordDamage(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        CombatDamageKind damageKind, CombatElement element, DuelRecordHitMark flags,
        double damage, string? defId = null, bool targetDowned = false)
    {
        if (_active is null) return;
        var sourceIdx = UnitIndex(source);
        var targetIdx = UnitIndex(target);
        var rounded = double.IsFinite(damage) && damage > 0 ? Math.Round(damage, 2) : 0;
        AppendEffect(new DuelRecordEffectDocument(
            sourceIdx, targetIdx, kind, damageKind, element, flags, rounded, InternDef(defId)));
        if (targetDowned)
        {
            AppendEffect(new DuelRecordEffectDocument(
                sourceIdx, targetIdx, DuelRecordEffectKind.Downed));
        }
    }

    /// <inheritdoc />
    public void RecordHeal(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        double amount, string? defId = null)
    {
        if (_active is null || !double.IsFinite(amount) || amount <= 1e-9) return;
        AppendEffect(new DuelRecordEffectDocument(
            UnitIndex(source), UnitIndex(target), kind,
            Value: Math.Round(amount, 2),
            DefIndex: InternDef(defId)));
    }

    /// <inheritdoc />
    public void RecordMana(CombatantState source, CombatantState target, DuelRecordEffectKind kind,
        double amount, string? defId = null)
    {
        if (_active is null || !double.IsFinite(amount) || amount <= 1e-9) return;
        AppendEffect(new DuelRecordEffectDocument(
            UnitIndex(source), UnitIndex(target), kind,
            Value: Math.Round(amount, 2),
            DefIndex: InternDef(defId)));
    }

    /// <inheritdoc />
    public void RecordUpkeep(CombatantState unit, double amount, string defId)
    {
        if (_active is null || !double.IsFinite(amount) || amount <= 1e-9) return;
        var idx = UnitIndex(unit);
        AppendEffect(new DuelRecordEffectDocument(
            idx, idx, DuelRecordEffectKind.UpkeepCost,
            Value: Math.Round(amount, 2),
            DefIndex: InternDef(defId)));
    }

    /// <inheritdoc />
    public void RecordBuff(CombatantState source, CombatantState target, string buffId, double stacks,
        DuelRecordActionKind fallbackKind = DuelRecordActionKind.Trait, double atSeconds = 0, string? fallbackDefId = null)
    {
        if (string.IsNullOrWhiteSpace(buffId) || !double.IsFinite(stacks) || stacks <= 0) return;
        var openedStandalone = _active is null && _scopeDepth == 0;
        if (openedStandalone)
            BeginAction(atSeconds, source, fallbackKind, fallbackDefId);
        AppendEffect(new DuelRecordEffectDocument(
            UnitIndex(source), UnitIndex(target), DuelRecordEffectKind.BuffGrant,
            Value: Math.Round(stacks, 2),
            DefIndex: InternDef(buffId)));
        if (openedStandalone)
            EndAction();
    }

    /// <summary>生成指定观看方视角的紧凑斗法记录（确保 Units[0] 恒为本方，Units[1] 恒为对手）。</summary>
    public DuelBattleRecordDocument BuildRecord(CombatOutcome outcome, double durationSeconds, bool viewerIsInitiator)
    {
        var units = new List<DuelRecordUnitDocument>(_units.Count);
        if (viewerIsInitiator || _units.Count < 2)
        {
            units.AddRange(_units);
        }
        else
        {
            units.Add(_units[1] with { IsAlly = true });
            units.Add(_units[0] with { IsAlly = false });
            for (var i = 2; i < _units.Count; i++)
                units.Add(_units[i] with { IsAlly = !_units[i].IsAlly });
        }

        int FlipIndex(int index) => viewerIsInitiator ? index : index switch { 0 => 1, 1 => 0, _ => index };

        var actions = new DuelRecordActionDocument[_actions.Count];
        for (var i = 0; i < _actions.Count; i++)
        {
            var src = _actions[i];
            var fx = new DuelRecordEffectDocument[src.Effects.Count];
            for (var j = 0; j < src.Effects.Count; j++)
            {
                var e = src.Effects[j];
                fx[j] = viewerIsInitiator ? e : e with { Source = FlipIndex(e.Source), Target = FlipIndex(e.Target) };
            }
            actions[i] = new DuelRecordActionDocument(
                src.AtSeconds,
                FlipIndex(src.Actor),
                src.Kind,
                src.DefIndex,
                src.OverrideIndex,
                fx);
        }

        return new DuelBattleRecordDocument(
            "pending",
            0,
            Math.Round(Math.Max(0, durationSeconds), 3),
            outcome,
            Forfeited: false,
            Truncated: _truncated,
            Units: units,
            Defs: _defs.ToArray(),
            Actions: actions);
    }
}
