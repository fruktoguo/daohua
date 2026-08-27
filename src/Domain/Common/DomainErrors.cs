namespace Domain.Common;

public enum DomainErrorCode
{
    InvalidArgument,
    InvariantViolation,
    NotFound,
    Conflict,
    InsufficientBalance,
    InvalidTransition,
    Duplicate,
    Expired
}

public sealed class DomainException : Exception
{
    public DomainException(DomainErrorCode code, string message) : base(message) => Code = code;
    public DomainErrorCode Code { get; }
}

public static class DomainGuard
{
    public static void Against(bool condition, DomainErrorCode code, string message)
    {
        if (condition) throw new DomainException(code, message);
    }

    public static T NotNull<T>(T? value, string name) where T : class =>
        value ?? throw new DomainException(DomainErrorCode.InvalidArgument, $"{name} 不能为空。");
}
