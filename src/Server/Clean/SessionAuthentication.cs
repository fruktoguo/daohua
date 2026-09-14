namespace Server.Clean;

/// <summary>
/// 提供 SessionAuthenticationDefaults 相关功能的静态容器。
/// </summary>
public static class SessionAuthenticationDefaults
{
    /// <summary>
    /// 表示常量 Scheme。
    /// </summary>
    public const string Scheme = "DaohuaSession";
    /// <summary>
    /// 表示常量 CookieName。
    /// </summary>
    public const string CookieName = "daohua_session";
}

/// <summary>
/// 表示 SessionAuthenticationHandler 类。
/// </summary>
/// <param name="accounts">参数 accounts。</param>
/// <param name="options">服务端配置选项。</param>
/// <param name="logger">日志记录器。</param>
/// <param name="encoder">参数 encoder。</param>
public sealed class SessionAuthenticationHandler(
    IAccountRepository accounts,
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = Request.Cookies[SessionAuthenticationDefaults.CookieName];
        if (string.IsNullOrWhiteSpace(token) && Request.Headers.Authorization.Count > 0)
        {
            var value = Request.Headers.Authorization.ToString();
            if (value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) token = value[7..].Trim();
        }
        if (string.IsNullOrWhiteSpace(token)) return AuthenticateResult.NoResult();

        var identity = await accounts.ValidateSessionAsync(token, Context.RequestAborted);
        if (identity is null) return AuthenticateResult.Fail("会话无效或已过期。");
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, identity.AccountId.ToString("D")),
            new Claim("player_id", identity.PlayerId.ToString("D")),
            new Claim(ClaimTypes.Name, identity.Account),
            new Claim("display_name", identity.DisplayName)
        };
        var schemeName = Scheme.Name ?? SessionAuthenticationDefaults.Scheme;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, schemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, schemeName));
    }
}

/// <summary>
/// 提供 SessionAuthenticationExtensions 相关功能的静态容器。
/// </summary>
public static class SessionAuthenticationExtensions
{
    /// <summary>
    /// 添加DaohuaAuthentication。
    /// </summary>
    /// <param name="services">参数 services。</param>
    /// <returns>返回 IServiceCollection 类型的结果。</returns>
    public static IServiceCollection AddDaohuaAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = SessionAuthenticationDefaults.Scheme;
                options.DefaultChallengeScheme = SessionAuthenticationDefaults.Scheme;
            })
            .AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>(SessionAuthenticationDefaults.Scheme, _ => { });
        services.AddAuthorization();
        return services;
    }
}
