namespace Server.Clean.AccountAbuse;

/// <summary>注册请求携带的风险信号。信号只用于服务端归一化与哈希匹配，原始值不落库。</summary>
public sealed record AccountRiskSignals(
    string? DeviceConfigHash = null,
    string? DeviceId = null,
    string? IpAddress = null,
    string? NetworkHash = null,
    string? PersonKey = null,
    string? UserAgent = null,
    bool IsHeadless = false);

/// <summary>账号风控模块的动态策略；运维端可以替换注册表中的实例而无需重启进程。</summary>
public sealed record AccountRiskPolicy(
    bool Enabled = true,
    bool RequireDeviceSignal = true,
    int MaxAccountsPerDevice = 1,
    int MaxAccountsPerPerson = 2)
{
    /// <summary>是否在注册入口执行风控；关闭时仍保留观测与运维统计。</summary>
    public bool RegistrationEnabled { get; init; } = true;
    /// <summary>软信号达到该分数后的处置阈值。</summary>
    public int RiskThreshold { get; init; } = 80;
    /// <summary>软风险达到阈值时的处置：allow、review 或 deny。</summary>
    public string HighRiskAction { get; init; } = "deny";
    /// <summary>可单独开关的检测模块。</summary>
    public IReadOnlyList<AccountRiskModulePolicy> Modules { get; init; } = DefaultModules;

    private static readonly IReadOnlyList<AccountRiskModulePolicy> DefaultModules =
    [
        new("deviceConfigHash", "设备配置", true, 45, 2),
        new("deviceId", "设备标识", true, 100, 1),
        new("ipAddress", "来源 IP", true, 40, 2),
        new("network", "网络环境", true, 15, 2),
        new("browserFingerprint", "设备指纹", true, 40, 2),
        new("registrationVelocity", "注册频率", true, 80, 3),
        new("accountAssociation", "账号关联上限", true, 100, 2),
        new("tabGuard", "标签页互斥", true, 100, 1),
        new("singleSession", "单账号互踢", true, 100, 1),
        new("onlineDeviceQuota", "设备在线配额", true, 100, 2),
    ];
    /// <summary>当前内测默认策略：一个安装标识注册一个账号，同一关联主体最多两个账号。</summary>
    public static AccountRiskPolicy Default { get; } = new();

    /// <summary>校验策略边界，防止运维配置把限制误改成无效值。</summary>
    public void Validate()
    {
        if (MaxAccountsPerDevice is < 1 or > 16)
            throw new InvalidOperationException("AccountRisk:MaxAccountsPerDevice 必须在 1..16。");
        if (MaxAccountsPerPerson is < 1 or > 32)
            throw new InvalidOperationException("AccountRisk:MaxAccountsPerPerson 必须在 1..32。");
        if (RiskThreshold is < 0 or > 100)
            throw new InvalidOperationException("AccountRisk:RiskThreshold 必须在 0..100。");
        if (HighRiskAction is not ("allow" or "review" or "deny"))
            throw new InvalidOperationException("AccountRisk:HighRiskAction 必须是 allow、review 或 deny。");
        if (Modules is null || Modules.Count != DefaultModules.Count || Modules.Any(module => module is null) ||
            Modules.Select(module => module.Id).Distinct(StringComparer.Ordinal).Count() != DefaultModules.Count ||
            DefaultModules.Any(expected => Modules.All(module => module.Id != expected.Id)))
            throw new InvalidOperationException("AccountRisk:Modules 数量无效。");
        foreach (var module in Modules)
        {
            if (string.IsNullOrWhiteSpace(module.Name) || module.Name.Length > 64 || module.Weight is < 0 or > 100 || module.Threshold is < 1 or > 100)
                throw new InvalidOperationException("AccountRisk:Modules 包含无效项。");
        }
    }
}

/// <summary>一个可动态开关的账号检测模块及其软风险权重。</summary>
public sealed record AccountRiskModulePolicy(string Id, string Name, bool Enabled, int Weight, int Threshold);

/// <summary>进程内动态策略提供器；实际持久化由运维端接入配置仓储完成。</summary>
public sealed class AccountRiskPolicyRegistry
{
    private AccountRiskPolicy _current;

    /// <summary>创建策略注册表。</summary>
    public AccountRiskPolicyRegistry(AccountRiskPolicy? initial = null)
    {
        _current = initial ?? AccountRiskPolicy.Default;
        _current.Validate();
    }

    /// <summary>读取当前策略快照。</summary>
    public AccountRiskPolicy Current => Volatile.Read(ref _current);

    /// <summary>替换当前策略，并在替换前校验边界。</summary>
    public void Replace(AccountRiskPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        policy.Validate();
        Volatile.Write(ref _current, policy);
    }
}

/// <summary>风控信号类型；字符串值属于持久化协议，修改需同步 schema 与运维查询。</summary>
public static class AccountRiskSignalKind
{
    /// <summary>浏览器/设备配置组合哈希，仅作为软关联维度。</summary>
    public const string DeviceConfig = "device_config";
    /// <summary>设备安装标识，硬限制维度。</summary>
    public const string DeviceId = "device_id";
    /// <summary>来源 IP，仅作为软关联维度。</summary>
    public const string Ip = "ip";
    /// <summary>网络标识（例如网段或网络环境哈希），仅作为软关联维度。</summary>
    public const string Network = "network";
    /// <summary>操作者稳定身份键，最多允许绑定两个账号。</summary>
    public const string Person = "person";
    /// <summary>客户端标识摘要，仅作为审计辅助维度。</summary>
    public const string UserAgent = "user_agent";
    /// <summary>无头浏览器或自动化脚本运行特征，直接硬拦截。</summary>
    public const string Headless = "headless";
}

/// <summary>注册风控拒绝原因，供 HTTP 与运维面板使用。</summary>
public static class AccountRiskErrorCodes
{
    /// <summary>同一硬设备信号已绑定其他账号。</summary>
    public const string DeviceAlreadyRegistered = "DEVICE_ALREADY_REGISTERED";
    /// <summary>同一稳定身份已达到账号上限。</summary>
    public const string PersonAccountLimit = "PERSON_ACCOUNT_LIMIT";
    /// <summary>风控策略开启但请求没有可用设备信号。</summary>
    public const string RiskSignalRequired = "RISK_SIGNAL_REQUIRED";
    /// <summary>检测到无头浏览器或自动化环境。</summary>
    public const string HeadlessBrowserBlocked = "HEADLESS_BROWSER_BLOCKED";
}

/// <summary>单条信号的持久化哈希；只暴露摘要前缀给运维视图。</summary>
public sealed record AccountRiskSignalHash(string Kind, string Hash);

/// <summary>注册风控判定的可审计结果。</summary>
public sealed record AccountRiskDecision(
    bool Allowed,
    string? ErrorCode,
    string? Message,
    IReadOnlyList<AccountRiskSignalHash> Signals,
    int RiskScore = 0,
    string Action = "allow",
    IReadOnlyList<string>? Reasons = null);

/// <summary>近期风险事件的脱敏读模型。</summary>
public sealed record AccountRiskEventSummary(
    Guid EventId,
    DateTimeOffset OccurredAt,
    string Kind,
    string Action,
    int RiskScore,
    string? AccountKey,
    string? DeviceKey,
    string? NetworkKey,
    IReadOnlyList<string> Reasons);

/// <summary>按稳定主体聚合的脱敏关联摘要。</summary>
public sealed record AccountRiskAssociationSummary(
    string AssociationKey,
    int AccountCount,
    IReadOnlyList<string> AccountKeys,
    int DeviceCount,
    int NetworkCount,
    int MaxRiskScore,
    DateTimeOffset LastSeenAt);

/// <summary>账号检测面板统计。</summary>
public sealed record AccountRiskStats(
    long AccountCount,
    long LinkedPersonCount,
    long LinkedDeviceCount,
    long RegistrationsToday,
    long AllowedToday,
    long ReviewedToday,
    long BlockedToday,
    long HighRiskToday,
    long RecentEventCount);
