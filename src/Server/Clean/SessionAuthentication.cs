namespace Server.Clean;

/// <summary>
/// 会话认证方案的常量定义。
/// </summary>
public static class SessionAuthenticationDefaults
{
    /// <summary>认证方案名，注册为默认 authenticate/challenge scheme。</summary>
    public const string Scheme = "DaohuaSession";
    /// <summary>承载不透明会话令牌的 HttpOnly Cookie 名；任何取值变更必须与前端同源 cookie 约定一致。</summary>
    public const string CookieName = "daohua_session";
}

/// <summary>
/// 不透明会话令牌认证处理器：优先读 HttpOnly Cookie，兼容 Bearer 头（非浏览器客户端）；
/// 令牌经 IAccountRepository.ValidateSessionAsync 换成账号/玩家声明（player_id、display_name）。
/// </summary>
/// <param name="accounts">账号仓储，负责会话令牌校验与身份解析。</param>
/// <param name="options">认证框架选项监视器。</param>
/// <param name="logger">日志工厂。</param>
/// <param name="encoder">URL 编码器（框架必需）。</param>
public sealed class SessionAuthenticationHandler(
    IAccountRepository accounts,
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>
    /// 认证入口：优先读会话 Cookie，非浏览器客户端回退 Bearer 头；令牌经账号仓储校验后
    /// 构造含 player_id/display_name/sequence_id 声明的票据。无令牌返回 NoResult（按匿名走挑战），
    /// 令牌无效或过期返回 Fail。
    /// </summary>
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
            new Claim("display_name", identity.DisplayName),
            new Claim("sequence_id", identity.SequenceId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        };
        var schemeName = Scheme.Name ?? SessionAuthenticationDefaults.Scheme;
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, schemeName));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, schemeName));
    }
}

/// <summary>
/// 会话认证的服务装配扩展。
/// </summary>
public static class SessionAuthenticationExtensions
{
    /// <summary>
    /// 注册 DaohuaSession 为默认认证与挑战方案，并启用授权。
    /// </summary>
    /// <param name="services">服务集合。</param>
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
