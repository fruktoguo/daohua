namespace Domain.Common;

/// <summary>不可变的强类型标识符基类约定。</summary>
public interface IIdentifier
{
    /// <summary>标识原始值（字符串或 GUID 的规范文本）。</summary>
    string Value { get; }
}

/// <summary>用户账号标识，对应认证鉴权主体，跨角色唯一。</summary>
public readonly record struct AccountId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static AccountId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>修仙者角色聚合唯一标识，对应单 Fiber 运行时与独立存档生命周期。</summary>
public readonly record struct PlayerId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static PlayerId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>配表动作/行动定义标识（如采集、修炼、炼丹、探索等）。</summary>
public readonly record struct ActionId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    /// <summary>标识字符串是否满足长度与格式约束。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>配表道具/物品类型唯一标识（如灵石、草药、丹药、装备模板）。</summary>
public readonly record struct ItemId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    /// <summary>标识字符串是否满足长度与格式约束。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>功法/术法/神通定义唯一标识。</summary>
public readonly record struct AbilityId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    /// <summary>标识字符串是否满足长度与格式约束。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>非同质化物品（如独立属性装备、动态法宝）的唯一实例标识。</summary>
public readonly record struct InstanceId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static InstanceId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>坊市交易挂单唯一标识，在订单簿撮合与落盘时唯一索引。</summary>
public readonly record struct OrderId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static OrderId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>经济账本资产变动流水唯一标识，用于对账与双分录凭证。</summary>
public readonly record struct TransactionId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static TransactionId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>资产预扣结/冻结租约标识（如挂单冻结灵石、制造锁定材料）。</summary>
public readonly record struct ReservationId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static ReservationId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>后台补算或批处理作业调度任务标识。</summary>
public readonly record struct JobId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    /// <summary>生成随机新标识。</summary>
    public static JobId New() => new(Guid.NewGuid());
    /// <summary>标识是否为空默认值。</summary>
    public bool IsEmpty => Value == Guid.Empty;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value.ToString("N");
}

/// <summary>客户端指令幂等键，防止网络抖动与重复重试导致动作二次结算。</summary>
public readonly record struct IdempotencyKey(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    /// <summary>标识字符串是否满足长度与格式约束。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 200;
    /// <summary>输出标识的规范文本形式。</summary>
    public override string ToString() => Value ?? string.Empty;
}

