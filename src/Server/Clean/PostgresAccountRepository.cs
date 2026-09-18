using System.Text.RegularExpressions;

namespace Server.Clean;

/// <summary>注册结局；失败时 Success=false 且只携带 ErrorCode/Message，成功时附带已签发的会话令牌。</summary>
/// <param name="Success">注册是否成功。</param>
/// <param name="AccountId">新建账号标识。</param>
/// <param name="PlayerId">新建玩家标识。</param>
/// <param name="Account">规范化后的登录账号。</param>
/// <param name="DisplayName">最终生效的道号。</param>
/// <param name="ErrorCode">失败错误码（如 DUPLICATE_ACCOUNT_OR_NAME）。</param>
/// <param name="Message">面向用户的失败描述。</param>
/// <param name="SessionToken">注册即登录签发的会话令牌明文，仅此一次返回给客户端。</param>
public sealed record AccountRegistrationResult(
    bool Success,
    Guid? AccountId,
    Guid? PlayerId,
    string? Account,
    string? DisplayName,
    string? ErrorCode,
    string? Message,
    string? SessionToken = null);

/// <summary>登录结局；凭据无效与账号停用统一返回 INVALID_CREDENTIALS，不暴露账号是否存在。</summary>
/// <param name="Success">登录是否成功。</param>
/// <param name="AccountId">账号标识。</param>
/// <param name="PlayerId">绑定玩家标识。</param>
/// <param name="Account">规范化后的登录账号。</param>
/// <param name="DisplayName">当前道号。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="Message">面向用户的失败描述。</param>
/// <param name="SessionToken">新签发的会话令牌明文，仅此一次返回给客户端。</param>
public sealed record AccountLoginResult(
    bool Success,
    Guid? AccountId,
    Guid? PlayerId,
    string? Account,
    string? DisplayName,
    string? ErrorCode,
    string? Message,
    string? SessionToken = null);

/// <summary>账号、凭据与认证会话的持久化边界。</summary>
public interface IAccountRepository
{
    /// <summary>注册账号并创建绑定玩家，同一事务写入初始会话与注册审计事件。</summary>
    ValueTask<AccountRegistrationResult> RegisterAsync(string account, string password, string? displayName, CancellationToken cancellationToken = default);
    /// <summary>校验账号密码；成功时刷新 last_seen_at 并签发新会话令牌。</summary>
    ValueTask<AccountLoginResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);
    /// <summary>按令牌明文解析有效会话；未撤销、未过期且账号/玩家均启用才返回身份，否则 null。</summary>
    ValueTask<SessionIdentity?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default);
    /// <summary>撤销会话；实际命中行时同事务写入登出审计事件。</summary>
    ValueTask RevokeSessionAsync(string token, CancellationToken cancellationToken = default);
    /// <summary>改写道号；display_name_normalized 唯一索引冲突时返回 false。</summary>
    ValueTask<bool> RenamePlayerAsync(Guid playerId, string displayName, CancellationToken cancellationToken = default);
}

/// <summary>一次有效会话解析出的身份。</summary>
/// <param name="AccountId">会话所属账号。</param>
/// <param name="PlayerId">绑定玩家标识。</param>
/// <param name="Account">登录账号。</param>
/// <param name="DisplayName">当前道号。</param>
public sealed record SessionIdentity(Guid AccountId, Guid PlayerId, string Account, string DisplayName);

/// <summary>账号与会话的关系存储；密码明文和 token 明文永不入库。</summary>
public sealed class PostgresAccountRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresAccountRepository> logger,
    IOutboxRepository outbox) : IAccountRepository
{
    /// <summary>密码长度下限。</summary>
    public const int MinPasswordLength = 6;
    /// <summary>密码长度上限。</summary>
    public const int MaxPasswordLength = 24;
    private const int Pbkdf2Iterations = 600_000;
    private static readonly Regex AccountPattern = new("^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 注册账号：同一 Serializable 事务内写账号、玩家、初始会话与 outbox 注册事件，
    /// 任何一步失败整体回滚；账号名或道号撞唯一索引映射为 DUPLICATE_ACCOUNT_OR_NAME。
    /// </summary>
    /// <param name="account">登录账号，入库前统一小写规范化。</param>
    /// <param name="password">明文密码，PBKDF2-SHA512 哈希后入库，明文不落盘。</param>
    /// <param name="displayName">初始道号；为空时回退为规范化账号名。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时携带新会话令牌的注册结局。</returns>
    public async ValueTask<AccountRegistrationResult> RegisterAsync(
        string account,
        string password,
        string? displayName,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccount = NormalizeAccount(account);
        var normalizedName = NormalizeDisplayName(displayName, normalizedAccount);
        if (!AccountPattern.IsMatch(normalizedAccount))
            return FailureRegistration("INVALID_ACCOUNT", "账号须为 3-32 位字母、数字或下划线。");
        if (string.IsNullOrEmpty(password) || password.Length is < MinPasswordLength or > MaxPasswordLength)
            return FailureRegistration("INVALID_PASSWORD", $"密码长度须在 {MinPasswordLength}-{MaxPasswordLength} 个字符之间。");
        if (normalizedName.Length is < 1 or > 64)
            return FailureRegistration("INVALID_DISPLAY_NAME", "角色名长度无效。");

        var accountId = Guid.NewGuid();
        var playerId = Guid.NewGuid();
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 64);
        var now = DateTimeOffset.UtcNow;
        var sessionToken = CreateSessionToken();
        var sessionTokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(sessionToken));

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        try
        {
            await using (var accountCommand = new NpgsqlCommand("""
                INSERT INTO identity.accounts
                    (account_id, login_normalized, password_hash, password_salt,
                     password_iterations, status, created_at, updated_at)
                VALUES ($1, $2, $3, $4, $5, 1, $6, $6)
                """, connection, transaction))
            {
                accountCommand.Parameters.Add(new NpgsqlParameter { Value = accountId });
                accountCommand.Parameters.Add(new NpgsqlParameter { Value = normalizedAccount });
                accountCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = hash });
                accountCommand.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = salt });
                accountCommand.Parameters.Add(new NpgsqlParameter { Value = Pbkdf2Iterations });
                accountCommand.Parameters.Add(new NpgsqlParameter { Value = now });
                await accountCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var playerCommand = new NpgsqlCommand("""
                INSERT INTO identity.players
                    (player_id, account_id, display_name, display_name_normalized,
                     created_at, last_seen_at, status)
                VALUES ($1, $2, $3, $4, $5, $5, 1)
                """, connection, transaction))
            {
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = playerId });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = accountId });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = normalizedName });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = NormalizeNameForLookup(normalizedName) });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = now });
                await playerCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertSessionAsync(connection, transaction, accountId, sessionTokenHash, now, now.Add(options.SessionLifetime), cancellationToken);

            await outbox.AppendAsync(connection, transaction, accountId, "account",
                "auth.account.registered",
                JsonSerializer.SerializeToUtf8Bytes(new { accountId, playerId, account = normalizedAccount }),
                now, cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return FailureRegistration("DUPLICATE_ACCOUNT_OR_NAME", "账号或角色名已存在。");
        }

        logger.LogInformation("新账号注册成功 accountId={AccountId} playerId={PlayerId}", accountId, playerId);
        return new AccountRegistrationResult(true, accountId, playerId, normalizedAccount, normalizedName, null, null, sessionToken);
    }

    /// <summary>
    /// 校验登录凭据并签发新会话。哈希比对用固定时间比较，且停用账号与密码错误
    /// 走同一失败出口，避免响应差异泄露账号状态。
    /// </summary>
    /// <param name="account">登录账号。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>成功时携带新会话令牌的登录结局。</returns>
    public async ValueTask<AccountLoginResult> LoginAsync(
        string account,
        string password,
        CancellationToken cancellationToken = default)
    {
        var normalizedAccount = NormalizeAccount(account);
        if (!AccountPattern.IsMatch(normalizedAccount) || string.IsNullOrEmpty(password))
            return FailureLogin("INVALID_CREDENTIALS", "账号或密码错误。");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.account_id, a.password_hash, a.password_salt, a.password_iterations,
                   p.player_id, a.status, p.display_name
            FROM identity.accounts a
            JOIN identity.players p ON p.account_id = a.account_id
            WHERE a.login_normalized = $1
            LIMIT 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { Value = normalizedAccount });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return FailureLogin("INVALID_CREDENTIALS", "账号或密码错误。");

        var accountId = reader.GetGuid(0);
        var hash = reader.GetFieldValue<byte[]>(1);
        var salt = reader.GetFieldValue<byte[]>(2);
        var iterations = reader.GetInt32(3);
        var playerId = reader.GetGuid(4);
        var status = reader.GetInt16(5);
        var name = reader.GetString(6);
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, hash.Length);
        if (status != 1 || !CryptographicOperations.FixedTimeEquals(candidate, hash))
            return FailureLogin("INVALID_CREDENTIALS", "账号或密码错误。");

        await reader.DisposeAsync();
        await using (var update = new NpgsqlCommand("UPDATE identity.players SET last_seen_at = now() WHERE player_id = $1", connection))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var token = await CreateSessionAsync(accountId, playerId, options.SessionLifetime, cancellationToken);
        return new AccountLoginResult(true, accountId, playerId, normalizedAccount, name, null, null, token);
    }

    /// <summary>按令牌哈希查找未撤销且未过期的会话；每次认证请求调用，令牌明文只用于哈希比对。</summary>
    public async ValueTask<SessionIdentity?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.account_id, p.player_id, a.login_normalized, p.display_name
            FROM identity.auth_sessions s
            JOIN identity.accounts a ON s.account_id = a.account_id
            JOIN identity.players p ON p.account_id = a.account_id
            WHERE s.token_hash = $1 AND s.account_id = a.account_id
              AND s.revoked_at IS NULL AND s.expires_at > now()
              AND a.status = 1 AND p.status = 1
            LIMIT 1
            """, connection);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = tokenHash });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new SessionIdentity(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3))
            : null;
    }

    /// <summary>按令牌哈希撤销会话；命中未撤销行时同事务写登出审计事件，令牌不存在或已撤销时静默返回。</summary>
    public async ValueTask RevokeSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return;
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(
            "UPDATE identity.auth_sessions SET revoked_at = now() WHERE token_hash = $1 AND revoked_at IS NULL RETURNING account_id",
            connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = tokenHash });
        var revokedAccountId = await command.ExecuteScalarAsync(cancellationToken) as Guid?;
        if (revokedAccountId.HasValue)
        {
            var now = DateTimeOffset.UtcNow;
            await outbox.AppendAsync(connection, transaction, revokedAccountId.Value, "account",
                "auth.account.logout",
                JsonSerializer.SerializeToUtf8Bytes(new { accountId = revokedAccountId.Value }),
                now, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// 原子改写道号并依赖 display_name_normalized 唯一索引完成查重；冲突返回 false。
    /// </summary>
    public async ValueTask<bool> RenamePlayerAsync(Guid playerId, string displayName, CancellationToken cancellationToken = default)
    {
        var normalized = displayName.Trim();
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand("""
                UPDATE identity.players
                SET display_name = $2, display_name_normalized = $3, last_seen_at = $4
                WHERE player_id = $1
                """, connection);
            command.Parameters.Add(new NpgsqlParameter { Value = playerId });
            command.Parameters.Add(new NpgsqlParameter { Value = normalized });
            command.Parameters.Add(new NpgsqlParameter { Value = NormalizeNameForLookup(normalized) });
            command.Parameters.Add(new NpgsqlParameter { Value = DateTimeOffset.UtcNow });
            return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    private async Task<string> CreateSessionAsync(Guid accountId, Guid playerId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        var token = CreateSessionToken();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var now = DateTimeOffset.UtcNow;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await InsertSessionAsync(connection, transaction, accountId, hash, now, now.Add(lifetime), cancellationToken);
        await outbox.AppendAsync(connection, transaction, accountId, "account",
            "auth.account.login",
            JsonSerializer.SerializeToUtf8Bytes(new { accountId, playerId }),
            now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return token;
    }

    private static async Task InsertSessionAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid accountId,
        byte[] tokenHash,
        DateTimeOffset issuedAt,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO identity.auth_sessions
                (session_id, account_id, token_hash, issued_at, expires_at, last_used_at)
            VALUES ($1, $2, $3, $4, $5, $4)
            """, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.NewGuid() });
        command.Parameters.Add(new NpgsqlParameter { Value = accountId });
        command.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = tokenHash });
        command.Parameters.Add(new NpgsqlParameter { Value = issuedAt });
        command.Parameters.Add(new NpgsqlParameter { Value = expiresAt });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateSessionToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static string NormalizeAccount(string value) => (value ?? string.Empty).Trim().ToLowerInvariant();
    private static string NormalizeDisplayName(string? value, string account) => string.IsNullOrWhiteSpace(value) ? account : value.Trim();
    private static string NormalizeNameForLookup(string value) => value.Trim().ToUpperInvariant();
    private static AccountRegistrationResult FailureRegistration(string code, string message) => new(false, null, null, null, null, code, message);
    private static AccountLoginResult FailureLogin(string code, string message) => new(false, null, null, null, null, code, message);
}
