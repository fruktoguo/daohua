using Domain.Combat;

namespace Domain.GameData;

/// <summary>追忆副本波次类型：普通妖阵 / 精英守卫 / 关底妖王。</summary>
public enum DungeonWaveKind : byte
{
    /// <summary>常规妖兽波次。</summary>
    Normal = 0,
    /// <summary>精英守卫波次（标准第 3、6、9 波）。</summary>
    Elite = 1,
    /// <summary>关底妖王波次（标准第 10 波）。</summary>
    Boss = 2
}

/// <summary>追忆副本单波次固定敌阵配表定义。</summary>
public sealed record DungeonWaveDefinition(
    int WaveIndex,
    DungeonWaveKind Kind,
    IReadOnlyList<string> EnemyIds);

/// <summary>追忆副本普通档（d=0, 30 体）通关额外基准奖励。</summary>
public sealed record DungeonBaseRewards(
    double Experience,
    double SpiritStones);

/// <summary>追忆副本第 10 波通关独占战利品条目。</summary>
/// <param name="ItemId">产出物品或装备 ID。</param>
/// <param name="Kind">条目类别：guaranteed（保底特殊材料，数量按 Total(d) 期望缩放）或 rare（低概率成品/奇珍，按耗体份额抽次 × 效率掉率缩放）。</param>
/// <param name="MinQuantity">基础最小数量。</param>
/// <param name="MaxQuantity">基础最大数量。</param>
/// <param name="DropChance">基准单次掉率 (0, 1]。</param>
/// <param name="MinDifficulty">最低解锁掉落难度（0..10，默认 0 全档可出）。</param>
public sealed record DungeonLootEntry(
    ItemId ItemId,
    string Kind,
    double MinQuantity,
    double MaxQuantity,
    double DropChance,
    int MinDifficulty = 0);

/// <summary>追忆副本静态配表定义（config/game-data/dungeons/*.json）。</summary>
public sealed record DungeonDefinition(
    string Id,
    string Name,
    string Description,
    string NodeId,
    int Tier,
    int BaseLevel,
    IReadOnlyList<double> RecommendedValuation,
    double WaveRestSeconds,
    IReadOnlyList<DungeonWaveDefinition> Waves,
    DungeonBaseRewards BaseRewards,
    IReadOnlyList<DungeonLootEntry> ClearLootTable,
    IReadOnlyList<string>? Affixes = null,
    int MinMonsterLevel = 1)
{
    /// <summary>追忆副本开局入场前准备读条时长（秒）。</summary>
    public const double InitialSearchSeconds = 5.0;

    /// <summary>追忆副本战内倒地复活读条时长（秒；仅场上仍有存活队友时生效，全员倒地即刻判负）。</summary>
    public const double InBattleRespawnSeconds = 15.0;

    /// <summary>总波数（标准追忆副本为 10 波）。</summary>
    public int TotalWaves => Waves.Count;

    /// <summary>按 1-based 波次序号获取对应波次定义。</summary>
    public DungeonWaveDefinition GetWave(int waveNumber)
    {
        if (waveNumber < 1 || waveNumber > Waves.Count)
            throw new DomainException(DomainErrorCode.InvalidArgument, $"追忆副本 {Id} 波次 {waveNumber} 超出范围 [1, {Waves.Count}]。");
        return Waves[waveNumber - 1];
    }

    /// <summary>获取指定难度的推荐身家估值；缺省回落末项或 0。</summary>
    public double RecommendedValuationFor(int difficulty)
    {
        if (RecommendedValuation.Count == 0) return 0;
        var index = Math.Clamp(DungeonDifficultyRules.Clamp(difficulty), 0, RecommendedValuation.Count - 1);
        return RecommendedValuation[index];
    }
}
