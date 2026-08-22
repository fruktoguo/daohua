// ==============================================================================
// 文件名称: Identifiers.cs
// 主要职责: 定义修仙领域强类型标识符（Strongly Typed IDs）。
// 核心逻辑 / 定义内容:
//   1. 提供 PlayerId、ItemId、AbilityId、ActionId、EncounterId 等强类型值对象（readonly record struct）。
//   2. 保证编译期类型安全与零运行时堆分配，杜绝基本类型偏执。
// ==============================================================================

namespace Domain.Common;

/// <summary>
/// 玩家实体全局唯一强类型标识符。
/// </summary>
/// <param name="Value">底层 GUID 原始值。</param>
public readonly record struct PlayerId(Guid Value)
{
    /// <summary>生成一个新的随机 PlayerId。</summary>
    public static PlayerId New() => new(Guid.NewGuid());

    /// <summary>空的 PlayerId。</summary>
    public static PlayerId Empty => new(Guid.Empty);

    /// <summary>是否为空标识符。</summary>
    public bool IsEmpty => Value == Guid.Empty;

    /// <summary>转换为 GUID 字符串表示。</summary>
    public override string ToString() => Value.ToString();
}

/// <summary>
/// 物品/道具/货币全局强类型标识符。
/// </summary>
/// <param name="Value">底层字符串标识。</param>
public readonly record struct ItemId(string Value)
{
    /// <summary>空的 ItemId。</summary>
    public static ItemId Empty => new(string.Empty);

    /// <summary>是否为有效非空白标识符。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    /// <summary>转换为字符串表示。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// 战斗技能与功法全局强类型标识符。
/// </summary>
/// <param name="Value">底层字符串标识。</param>
public readonly record struct AbilityId(string Value)
{
    /// <summary>空的 AbilityId。</summary>
    public static AbilityId Empty => new(string.Empty);

    /// <summary>是否为有效非空白标识符。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    /// <summary>转换为字符串表示。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// 动作原型全局强类型标识符。
/// </summary>
/// <param name="Value">底层字符串标识。</param>
public readonly record struct ActionId(string Value)
{
    /// <summary>空的 ActionId。</summary>
    public static ActionId Empty => new(string.Empty);

    /// <summary>是否为有效非空白标识符。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    /// <summary>转换为字符串表示。</summary>
    public override string ToString() => Value ?? string.Empty;
}

/// <summary>
/// 战斗遭遇与副本全局强类型标识符。
/// </summary>
/// <param name="Value">底层字符串标识。</param>
public readonly record struct EncounterId(string Value)
{
    /// <summary>空的 EncounterId。</summary>
    public static EncounterId Empty => new(string.Empty);

    /// <summary>是否为有效非空白标识符。</summary>
    public bool IsValid => !string.IsNullOrWhiteSpace(Value);

    /// <summary>转换为字符串表示。</summary>
    public override string ToString() => Value ?? string.Empty;
}
