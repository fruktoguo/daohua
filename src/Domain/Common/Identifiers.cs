namespace Domain.Common;

/// <summary>不可变的强类型标识符基类约定。</summary>
public interface IIdentifier
{
    string Value { get; }
}

public readonly record struct AccountId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static AccountId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct PlayerId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static PlayerId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ActionId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct ItemId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct AbilityId(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 128;
    public override string ToString() => Value ?? string.Empty;
}

public readonly record struct InstanceId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static InstanceId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct OrderId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static OrderId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct TransactionId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static TransactionId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct ReservationId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static ReservationId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct JobId(Guid Value) : IIdentifier
{
    string IIdentifier.Value => Value.ToString("N");
    public static JobId New() => new(Guid.NewGuid());
    public bool IsEmpty => Value == Guid.Empty;
    public override string ToString() => Value.ToString("N");
}

public readonly record struct IdempotencyKey(string Value) : IIdentifier
{
    string IIdentifier.Value => Value;
    public bool IsValid => !string.IsNullOrWhiteSpace(Value) && Value.Length <= 200;
    public override string ToString() => Value ?? string.Empty;
}
