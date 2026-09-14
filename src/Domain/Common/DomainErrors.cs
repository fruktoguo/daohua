namespace Domain.Common;

/// <summary>
/// 纯领域层统一错误代码，表达业务规则不满足或契约冲突。
/// 不得包含传输协议（如 HTTP 4xx/5xx）或存储实现（如 SQL 错误）语义。
/// </summary>
public enum DomainErrorCode
{
    /// <summary>入参格式、范围或空值不符合业务预期</summary>
    InvalidArgument,
    /// <summary>核心业务不变量被破坏（严重业务异常）</summary>
    InvariantViolation,
    /// <summary>请求的实体、节点或配置未找到</summary>
    NotFound,
    /// <summary>并发冲突、版本过期或状态抢占失败</summary>
    Conflict,
    /// <summary>资产或代币余额不足以扣减</summary>
    InsufficientBalance,
    /// <summary>状态机流转不合法（例如不在运行态无法结算）</summary>
    InvalidTransition,
    /// <summary>唯一性约束冲突（如重复添加相同标识）</summary>
    Duplicate,
    /// <summary>租约、订单或 Buff 已过期</summary>
    Expired
}

/// <summary>
/// 领域异常基类，携带强类型领域错误码，用于上层捕获并映射为业务协议或日志。
/// </summary>
public sealed class DomainException : Exception
{
    /// <summary>
    /// 初始化 Domain异常。
    /// </summary>
    /// <param name="code">code 参数。</param>
    /// <param name="message">message 参数。</param>
    public DomainException(DomainErrorCode code, string message) : base(message) => Code = code;
    /// <summary>获取或设置 代码 的值。</summary>
    public DomainErrorCode Code { get; }
}

/// <summary>
/// 领域前置断言守卫工具，快速失败并保障领域不变量。
/// </summary>
public static class DomainGuard
{
    /// <summary>
    /// 当条件为真时触发领域异常
    /// </summary>
    public static void Against(bool condition, DomainErrorCode code, string message)
    {
        if (condition) throw new DomainException(code, message);
    }

    /// <summary>
    /// 确保引用非空，否则抛出参数无效异常
    /// </summary>
    public static T NotNull<T>(T? value, string name) where T : class =>
        value ?? throw new DomainException(DomainErrorCode.InvalidArgument, $"{name} 不能为空。");
}

