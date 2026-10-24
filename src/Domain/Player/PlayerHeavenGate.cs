using Domain.Combat;

namespace Domain.Player;

/// <summary>
/// 五行灵根归属。取值顺序与 <see cref="CombatElement"/> 的五行段保持一一对应，
/// 便于把灵根点数直接并入对应五行增伤 / 减伤乘区。
/// </summary>
public enum LinggenElement : byte
{
    /// <summary>金灵根。</summary>
    Metal = 1,
    /// <summary>木灵根。</summary>
    Wood = 2,
    /// <summary>水灵根。</summary>
    Water = 3,
    /// <summary>火灵根。</summary>
    Fire = 4,
    /// <summary>土灵根。</summary>
    Earth = 5
}

/// <summary>
/// 五行灵根点数（开天门抽取结果与玩家最终确认的结果共用同一形状）。
/// 每系点数为 [0, <see cref="MaxPointsPerElement"/>] 的整数，总值受抽取档位约束。
/// </summary>
public readonly record struct LinggenValues(int Metal, int Wood, int Water, int Fire, int Earth)
{
    /// <summary>单系灵根点数上限（阵盘雷达图刻度）。</summary>
    public const int MaxPointsPerElement = 100;

    /// <summary>读取指定五行点数。</summary>
    public int Of(LinggenElement element) => element switch
    {
        LinggenElement.Metal => Metal,
        LinggenElement.Wood => Wood,
        LinggenElement.Water => Water,
        LinggenElement.Fire => Fire,
        LinggenElement.Earth => Earth,
        _ => 0
    };

    /// <summary>空灵根：五系全 0，表示尚未取得灵根（语义命名，避免调用处直接写 default）。</summary>
    public static LinggenValues Empty => default;

    /// <summary>五行点数总和。</summary>
    public int Total => Metal + Wood + Water + Fire + Earth;

    /// <summary>有效灵根条数（点数 &gt; 0 的系别数量）。</summary>
    public int EffectiveCount
    {
        get
        {
            var count = 0;
            if (Metal > 0) count++;
            if (Wood > 0) count++;
            if (Water > 0) count++;
            if (Fire > 0) count++;
            if (Earth > 0) count++;
            return count;
        }
    }

    /// <summary>是否为空灵根。</summary>
    public bool IsEmpty => Total == 0;

    /// <summary>按五行枚举读取点数，供属性解析逐系并入乘区。</summary>
    public int Of(CombatElement element) => element switch
    {
        CombatElement.Metal => Metal,
        CombatElement.Wood => Wood,
        CombatElement.Water => Water,
        CombatElement.Fire => Fire,
        CombatElement.Earth => Earth,
        _ => 0
    };

    /// <summary>把五行唯一点数改写为给定值，其余保持不变。</summary>
    public LinggenValues With(LinggenElement element, int value) => element switch
    {
        LinggenElement.Metal => this with { Metal = value },
        LinggenElement.Wood => this with { Wood = value },
        LinggenElement.Water => this with { Water = value },
        LinggenElement.Fire => this with { Fire = value },
        LinggenElement.Earth => this with { Earth = value },
        _ => this
    };

    /// <summary>校验点数是否全部落在合法区间内（供持久化恢复边界使用）。</summary>
    public bool IsValid => Metal is >= 0 and <= MaxPointsPerElement
        && Wood is >= 0 and <= MaxPointsPerElement
        && Water is >= 0 and <= MaxPointsPerElement
        && Fire is >= 0 and <= MaxPointsPerElement
        && Earth is >= 0 and <= MaxPointsPerElement;
}

/// <summary>
/// 开天门与灵根状态：叩天门境界满修为后的必经流程。
/// 未入天门时突破被阻断；入天门后可继续逆天改命重洗灵根，累计改命次数永久保留。
/// </summary>
public sealed class PlayerHeavenGateState
{
    /// <summary>开天门抽取的原始结果（本局总值与初始分布）；未开天门为 null。</summary>
    public LinggenValues? RolledValues { get; set; }

    /// <summary>玩家确认生效的灵根；未入天门为 null。</summary>
    public LinggenValues? ConfirmedValues { get; set; }

    /// <summary>是否已完成入天门（灵根正式生效）。</summary>
    public bool Entered { get; set; }

    /// <summary>逆天改命累计次数：永久保留，不随突破或重洗重置。</summary>
    public int RerollCount { get; set; }

    /// <summary>本地状态版本号：任何写入自增，供属性缓存失效检测。</summary>
    public long Version { get; private set; }

    /// <summary>生效灵根（未入天门时为空灵根）。</summary>
    public LinggenValues EffectiveValues => Entered ? ConfirmedValues ?? LinggenValues.Empty : LinggenValues.Empty;

    /// <summary>写入生效灵根并推进版本号。</summary>
    public void Apply(LinggenValues? rolled, LinggenValues? confirmed, bool entered, int rerollCount)
    {
        RolledValues = rolled;
        ConfirmedValues = confirmed;
        Entered = entered;
        RerollCount = Math.Max(0, rerollCount);
        Version = checked(Version + 1);
    }

    /// <summary>恢复快照时同步版本号（仅持久化恢复边界使用）。</summary>
    internal void RestoreVersion(long version) => Version = version;

    /// <summary>清空状态回到未开天门（不重置累计改命次数以外的任何累计账本时使用）。</summary>
    public void ClearKeepRerollCount()
    {
        Apply(null, null, false, RerollCount);
    }
}
