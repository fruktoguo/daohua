namespace Server.Clean;

public static class SessionAuthenticationDefaults
{
    public const string Scheme = "DaohuaSession";
    public const string CookieName = "daohua_session";
}

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

public static class SessionAuthenticationExtensions
{
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
