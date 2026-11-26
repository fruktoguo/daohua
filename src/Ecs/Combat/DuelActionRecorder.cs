using Domain.Combat;

namespace DaoHua.Ecs;

/// <summary>
/// 斗法紧凑行动录制器：在 DuelScriptEngine.Resolve 期间挂载到 encounter.ActionRecorder，
/// 观察全部战斗出手、技能/物品/阵法/特质/周期效果，并按任意阵营任意成员视角输出紧凑记录
/// （原生支持 1~5 vs 1~5 多方队伍斗法，且严禁修改任何战斗状态或调用随机数）。
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

    private readonly record struct RegisteredUnit(
        CombatSide Side,
        int SideIndex,
        DuelRecordUnitDocument Document);

    private readonly List<RegisteredUnit> _units = [];
    private readonly Dictionary<string, int> _unitIndexById = new(StringComparer.Ordinal);
    private readonly List<string> _defs = [];
    private readonly Dictionary<string, int> _defIndexById = new(StringComparer.Ordinal);
    private readonly List<MutableAction> _actions = [];
    private MutableAction? _active;
    private int _scopeDepth;
    private bool _truncated;

    /// <summary>以 1v1 发起方和应战方初始化参战单位表（向既有单测兼容）。</summary>
    public DuelActionRecorder(DuelCombatant initiator, DuelCombatant defender)
    {
        ArgumentNullException.ThrowIfNull(initiator);
        ArgumentNullException.ThrowIfNull(defender);
        RegisterUnit("player", CombatSide.SideA, 0,
            new DuelRecordUnitDocument(initiator.DisplayName, IsAlly: true, initiator.PlayerId.Value.ToString("N")));
        RegisterUnit("enemy:0", CombatSide.SideB, 0,
            new DuelRecordUnitDocument(defender.DisplayName, IsAlly: false, defender.PlayerId.Value.ToString("N")));
    }

    /// <summary>以对称双阵营全员（各 1~5 名修士）初始化参战单位表，单位 ID 对齐 <c>sideA:{i}</c> / <c>sideB:{i}</c>。</summary>
    public DuelActionRecorder(IReadOnlyList<DuelCombatant> sideA, IReadOnlyList<DuelCombatant> sideB)
    {
        ArgumentNullException.ThrowIfNull(sideA);
        ArgumentNullException.ThrowIfNull(sideB);
        for (var i = 0; i < sideA.Count; i++)
        {
            RegisterUnit($"sideA:{i}", CombatSide.SideA, i,
                new DuelRecordUnitDocument(sideA[i].DisplayName, IsAlly: true, sideA[i].PlayerId.Value.ToString("N")));
        }
        for (var i = 0; i < sideB.Count; i++)
        {
            RegisterUnit($"sideB:{i}", CombatSide.SideB, i,
                new DuelRecordUnitDocument(sideB[i].DisplayName, IsAlly: false, sideB[i].PlayerId.Value.ToString("N")));
        }
    }

    private void RegisterUnit(string combatantId, CombatSide side, int sideIndex, DuelRecordUnitDocument unit)
    {
        if (_unitIndexById.ContainsKey(combatantId) || _units.Count >= DuelBattleRecordLimits.MaxUnitsPerRecord) return;
        _unitIndexById[combatantId] = _units.Count;
        _units.Add(new RegisteredUnit(side, sideIndex, unit));
    }

    private int UnitIndex(CombatantState unit)
    {
        if (_unitIndexById.TryGetValue(unit.Id, out var idx)) return idx;
        if (_units.Count >= DuelBattleRecordLimits.MaxUnitsPerRecord) return 0;
        idx = _units.Count;
        _unitIndexById[unit.Id] = idx;
        _units.Add(new RegisteredUnit(unit.Side, unit.SideIndex,
            new DuelRecordUnitDocument(unit.DefinitionId ?? unit.Id, IsAlly: unit.Side == CombatSide.SideA)));
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

    /// <summary>捕获当前开启的行动句柄，供同刻同时生效波次在波末提交实际治疗量或联合击倒标记。</summary>
    internal object? CaptureActiveAction() => _active;

    /// <summary>向指定已捕获行动句柄追加波末落账的实际治疗/吸血效果。</summary>
    internal void AppendDeferredHeal(object? actionHandle, CombatantState source, CombatantState target,
        DuelRecordEffectKind kind, double amount, string? defId = null)
    {
        if (actionHandle is not MutableAction action || !double.IsFinite(amount) || amount <= 1e-9) return;
        if (action.Effects.Count >= DuelBattleRecordLimits.MaxEffectsPerAction) return;
        var wasEmpty = action.Effects.Count == 0;
        action.Effects.Add(new DuelRecordEffectDocument(
            UnitIndex(source), UnitIndex(target), kind,
            Value: Math.Round(amount, 2),
            DefIndex: InternDef(defId)));
        if (wasEmpty && !_actions.Contains(action) && _actions.Count < DuelBattleRecordLimits.MaxActionsPerRecord)
            _actions.Add(action);
    }

    /// <summary>向指定已捕获行动句柄追加波末联合致死后的倒地标记。</summary>
    internal void AppendDeferredDowned(object? actionHandle, CombatantState source, CombatantState target)
    {
        if (actionHandle is not MutableAction action) return;
        if (action.Effects.Count >= DuelBattleRecordLimits.MaxEffectsPerAction) return;
        action.Effects.Add(new DuelRecordEffectDocument(
            UnitIndex(source), UnitIndex(target), DuelRecordEffectKind.Downed));
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

    /// <summary>生成 1v1 观看方视角的紧凑斗法记录（兼容既有单测入口）。</summary>
    public DuelBattleRecordDocument BuildRecord(CombatOutcome outcome, double durationSeconds, bool viewerIsInitiator)
        => BuildRecord(outcome, durationSeconds, viewerIsInitiator ? CombatSide.SideA : CombatSide.SideB, viewerMemberIndex: 0);

    /// <summary>
    /// 生成指定阵营指定成员视角的紧凑斗法记录：
    /// 确保 <c>Units[0]</c> 恒为观看者本人、其后为全部本方队友（<c>IsAlly = true</c>）、最后为全部对手（<c>IsAlly = false</c>）。
    /// </summary>
    public DuelBattleRecordDocument BuildRecord(
        CombatOutcome outcome,
        double durationSeconds,
        CombatSide viewerSide,
        int viewerMemberIndex = 0)
    {
        var count = _units.Count;
        var oldToNew = new int[count];
        var units = new List<DuelRecordUnitDocument>(count);

        // 1. 观看者本人置顶（下标 0）
        var viewerOldIndex = -1;
        for (var i = 0; i < count; i++)
        {
            if (_units[i].Side == viewerSide && _units[i].SideIndex == viewerMemberIndex)
            {
                viewerOldIndex = i;
                break;
            }
        }
        if (viewerOldIndex >= 0)
        {
            oldToNew[viewerOldIndex] = units.Count;
            units.Add(_units[viewerOldIndex].Document with { IsAlly = true });
        }

        // 2. 同阵营其余队友（IsAlly = true）
        for (var i = 0; i < count; i++)
        {
            if (i == viewerOldIndex || _units[i].Side != viewerSide) continue;
            oldToNew[i] = units.Count;
            units.Add(_units[i].Document with { IsAlly = true });
        }

        // 3. 敌对阵营全员（IsAlly = false）
        for (var i = 0; i < count; i++)
        {
            if (_units[i].Side == viewerSide) continue;
            oldToNew[i] = units.Count;
            units.Add(_units[i].Document with { IsAlly = false });
        }

        int MapIndex(int index) => (uint)index < (uint)oldToNew.Length ? oldToNew[index] : index;

        var actions = new DuelRecordActionDocument[_actions.Count];
        for (var i = 0; i < _actions.Count; i++)
        {
            var src = _actions[i];
            var fx = new DuelRecordEffectDocument[src.Effects.Count];
            for (var j = 0; j < src.Effects.Count; j++)
            {
                var e = src.Effects[j];
                fx[j] = e with { Source = MapIndex(e.Source), Target = MapIndex(e.Target) };
            }
            actions[i] = new DuelRecordActionDocument(
                src.AtSeconds,
                MapIndex(src.Actor),
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
