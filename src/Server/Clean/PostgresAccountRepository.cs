using System.Text.RegularExpressions;
using Server.Clean.AccountAbuse;

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
/// <param name="SequenceId">角色序号（identity.players.player_seq 自增编号）。</param>
public sealed record AccountRegistrationResult(
    bool Success,
    Guid? AccountId,
    Guid? PlayerId,
    string? Account,
    string? DisplayName,
    string? ErrorCode,
    string? Message,
    string? SessionToken = null,
    long? SequenceId = null);

/// <summary>改密结局；失败时 Success=false 且只携带 ErrorCode/Message（INVALID_CREDENTIALS=旧密码错误，INVALID_PASSWORD=新密码不合法）。</summary>
/// <param name="Success">改密是否成功。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="Message">面向用户的失败描述。</param>
public sealed record AccountChangePasswordResult(bool Success, string? ErrorCode, string? Message);

/// <summary>登录结局；凭据无效与账号停用统一返回 INVALID_CREDENTIALS，不暴露账号是否存在。</summary>
/// <param name="Success">登录是否成功。</param>
/// <param name="AccountId">账号标识。</param>
/// <param name="PlayerId">绑定玩家标识。</param>
/// <param name="Account">规范化后的登录账号。</param>
/// <param name="DisplayName">当前道号。</param>
/// <param name="ErrorCode">失败错误码。</param>
/// <param name="Message">面向用户的失败描述。</param>
/// <param name="SessionToken">新签发的会话令牌明文，仅此一次返回给客户端。</param>
/// <param name="SequenceId">角色序号（identity.players.player_seq 自增编号）。</param>
public sealed record AccountLoginResult(
    bool Success,
    Guid? AccountId,
    Guid? PlayerId,
    string? Account,
    string? DisplayName,
    string? ErrorCode,
    string? Message,
    string? SessionToken = null,
    long? SequenceId = null);

/// <summary>账号、凭据与认证会话的持久化边界。</summary>
public interface IAccountRepository
{
    /// <summary>注册账号并创建绑定玩家，同一事务执行风控并写入初始会话与注册审计事件。</summary>
    ValueTask<AccountRegistrationResult> RegisterAsync(string account, string password, string? displayName, AccountRiskSignals? riskSignals,
        CancellationToken cancellationToken = default, AccountRiskPolicy? policySnapshot = null);
    /// <summary>校验账号密码后，同一事务执行风控；通过才刷新 last_seen_at 并签发新会话。</summary>
    ValueTask<AccountLoginResult> LoginAsync(string account, string password, AccountRiskSignals? riskSignals,
        CancellationToken cancellationToken = default, AccountRiskPolicy? policySnapshot = null);
    /// <summary>按令牌明文解析有效会话；未撤销、未过期且账号/玩家均启用才返回身份，否则 null。</summary>
    ValueTask<SessionIdentity?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default);
    /// <summary>撤销会话；实际命中行时同事务写入登出审计事件。</summary>
    ValueTask RevokeSessionAsync(string token, CancellationToken cancellationToken = default);
    /// <summary>改写道号；display_name_normalized 唯一索引冲突时返回 false。</summary>
    ValueTask<bool> RenamePlayerAsync(Guid playerId, string displayName, CancellationToken cancellationToken = default);
    /// <summary>
    /// 校验当前密码后改写凭据（新随机盐与现行迭代数），同事务吊销该账号除 keepSessionTokenHash
    /// 指向会话外的全部存活会话并写改密审计事件；旧密码错误返回 INVALID_CREDENTIALS。
    /// </summary>
    ValueTask<AccountChangePasswordResult> ChangePasswordAsync(
        Guid accountId,
        string currentPassword,
        string newPassword,
        byte[]? keepSessionTokenHash,
        CancellationToken cancellationToken = default);
}

/// <summary>一次有效会话解析出的身份。</summary>
/// <param name="AccountId">会话所属账号。</param>
/// <param name="PlayerId">绑定玩家标识。</param>
/// <param name="Account">登录账号。</param>
/// <param name="DisplayName">当前道号。</param>
/// <param name="SequenceId">角色序号（identity.players.player_seq 自增编号）。</param>
public sealed record SessionIdentity(Guid AccountId, Guid PlayerId, string Account, string DisplayName, long SequenceId);

/// <summary>账号与会话的关系存储；密码明文和 token 明文永不入库。</summary>
public sealed class PostgresAccountRepository(
    NpgsqlDataSource dataSource,
    ServerOptions options,
    ILogger<PostgresAccountRepository> logger,
    IOutboxRepository outbox,
    AccountRiskPolicyRegistry riskPolicyRegistry,
    AccountRiskHasher riskHasher) : IAccountRepository
{
    /// <summary>密码长度下限。</summary>
    public const int MinPasswordLength = 6;
    /// <summary>密码长度上限。</summary>
    public const int MaxPasswordLength = 24;
    private const int Pbkdf2Iterations = 600_000;
    /// <summary>
    /// 会话校验内存缓存 TTL：握手与连续 API 的重复认证不再每次打三表 JOIN。
    /// 吊销即时性不依赖 TTL——<see cref="RevokeSessionAsync"/> 提交后主动失效缓存项；
    /// 代价是「绕过本仓储直改 auth_sessions」的吊销最长延迟一个 TTL，当前代码不存在该路径。
    /// </summary>
    private static readonly TimeSpan SessionCacheTtl = TimeSpan.FromSeconds(3);
    /// <summary>缓存容量阈值：超过即整体清空，避免异常流量下令牌键无限膨胀。</summary>
    private const int SessionCacheLimit = 4096;
    /// <summary>token_hash(hex) → (身份, 缓存截止时刻)；截止时刻不超过会话自身 expires_at。</summary>
    private readonly ConcurrentDictionary<string, (SessionIdentity Identity, DateTimeOffset ExpiresAt)> _sessionCache = new();
    private static readonly Regex AccountPattern = new("^[a-zA-Z0-9_]{3,32}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 注册账号：同一 ReadCommitted 事务内执行风控并写账号、玩家、初始会话与 outbox 注册事件，
    /// 任何一步失败整体回滚；账号名或道号撞唯一索引映射为 DUPLICATE_ACCOUNT_OR_NAME。
    /// </summary>
    /// <param name="account">登录账号，入库前统一小写规范化。</param>
    /// <param name="password">明文密码，PBKDF2-SHA512 哈希后入库，明文不落盘。</param>
    /// <param name="displayName">初始道号；为空时回退为规范化账号名。</param>
    /// <param name="riskSignals">注册设备与身份信号；服务端规范化后只保存 HMAC 摘要。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="policySnapshot">宿主已读取的可信策略快照，确保该次注册不受并发策略替换影响。</param>
    /// <returns>成功时携带新会话令牌的注册结局。</returns>
    public async ValueTask<AccountRegistrationResult> RegisterAsync(
        string account,
        string password,
        string? displayName,
        AccountRiskSignals? riskSignals,
        CancellationToken cancellationToken = default,
        AccountRiskPolicy? policySnapshot = null)
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
        var riskPolicy = policySnapshot ?? riskPolicyRegistry.Current;
        var riskHashes = riskHasher.Hash(riskSignals, riskPolicy);

        long playerSeq;
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        try
        {
            // 风控按信号取得事务锁；后续查询可见前一个持锁注册刚提交的关联行。
            var decision = await PostgresAccountRiskRepository.EvaluateAsync(
                connection, transaction, accountId, riskHashes, riskPolicy, cancellationToken);
            if (!decision.Allowed)
            {
                // 此时尚未建账号；提交拒绝审计保留无信号和额度拒绝的证据。
                await PostgresAccountRiskRepository.AppendEventAsync(
                    connection, transaction, null, decision, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return FailureRegistration(decision.ErrorCode!, decision.Message!);
            }

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
                RETURNING player_seq
                """, connection, transaction))
            {
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = playerId });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = accountId });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = normalizedName });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = NormalizeNameForLookup(normalizedName) });
                playerCommand.Parameters.Add(new NpgsqlParameter { Value = now });
                playerSeq = (long)(await playerCommand.ExecuteScalarAsync(cancellationToken))!;
            }

            await InsertSessionAsync(connection, transaction, accountId, sessionTokenHash, now, now.Add(options.SessionLifetime), cancellationToken);

            if (riskHashes.Count > 0)
                await PostgresAccountRiskRepository.PersistSignalsAsync(connection, transaction, accountId, riskHashes, now, cancellationToken);
            await PostgresAccountRiskRepository.AppendEventAsync(connection, transaction, accountId, decision, now, cancellationToken);

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
        return new AccountRegistrationResult(true, accountId, playerId, normalizedAccount, normalizedName, null, null, sessionToken, playerSeq);
    }

    /// <summary>
    /// 校验登录凭据后，在同一事务内执行风控、保存关联并签发会话和成功事件。
    /// 风控拒绝只提交拒绝审计；停用账号与密码错误走同一出口，避免泄露账号状态。
    /// </summary>
    /// <param name="account">登录账号。</param>
    /// <param name="password">明文密码。</param>
    /// <param name="riskSignals">登录设备与身份信号；缺失时由当前策略判定并记录审计。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="policySnapshot">宿主已读取的可信策略快照。</param>
    /// <returns>成功时携带新会话令牌的登录结局。</returns>
    public async ValueTask<AccountLoginResult> LoginAsync(
        string account,
        string password,
        AccountRiskSignals? riskSignals,
        CancellationToken cancellationToken = default,
        AccountRiskPolicy? policySnapshot = null)
    {
        var normalizedAccount = NormalizeAccount(account);
        if (!AccountPattern.IsMatch(normalizedAccount) || string.IsNullOrEmpty(password))
            return FailureLogin("INVALID_CREDENTIALS", "账号或密码错误。");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.account_id, a.password_hash, a.password_salt, a.password_iterations,
                   p.player_id, a.status, p.display_name, p.player_seq
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
        var playerSeq = reader.GetInt64(7);
        var candidate = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA512, hash.Length);
        if (status != 1 || !CryptographicOperations.FixedTimeEquals(candidate, hash))
            return FailureLogin("INVALID_CREDENTIALS", "账号或密码错误。");

        await reader.DisposeAsync();
        var riskPolicy = policySnapshot ?? riskPolicyRegistry.Current;
        var riskHashes = riskHasher.Hash(riskSignals, riskPolicy);
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, cancellationToken);
        var decision = await PostgresAccountRiskRepository.EvaluateAsync(
            connection, transaction, accountId, riskHashes, riskPolicy, cancellationToken, registration: false);
        await PostgresAccountRiskRepository.AppendEventAsync(connection, transaction, accountId, decision, now, cancellationToken, "login");
        if (!decision.Allowed)
        {
            await transaction.CommitAsync(cancellationToken);
            return FailureLogin(decision.ErrorCode!, decision.Message!);
        }

        await PostgresAccountRiskRepository.PersistSignalsAsync(connection, transaction, accountId, riskHashes, now, cancellationToken);
        await using (var update = new NpgsqlCommand("UPDATE identity.players SET last_seen_at = now() WHERE player_id = $1", connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = playerId });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        var token = CreateSessionToken();
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        await InsertSessionAsync(connection, transaction, accountId, tokenHash, now, now.Add(options.SessionLifetime), cancellationToken);
        await outbox.AppendAsync(connection, transaction, accountId, "account",
            "auth.account.login", JsonSerializer.SerializeToUtf8Bytes(new { accountId, playerId }), now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AccountLoginResult(true, accountId, playerId, normalizedAccount, name, null, null, token, playerSeq);
    }

    /// <summary>
    /// 按令牌哈希查找未撤销且未过期的会话；每次认证请求调用，令牌明文只用于哈希比对。
    /// 命中内存缓存（短 TTL 且不超过会话 expires_at）时直接返回，未命中才落库；
    /// 只缓存肯定结果，无效/过期/已撤销令牌每次都查库，不会把失败结果固化。
    /// </summary>
    public async ValueTask<SessionIdentity?> ValidateSessionAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        var cacheKey = Convert.ToHexString(tokenHash);
        var now = DateTimeOffset.UtcNow;
        if (_sessionCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > now)
            return cached.Identity;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT a.account_id, p.player_id, a.login_normalized, p.display_name, s.expires_at, p.player_seq
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
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var identity = new SessionIdentity(reader.GetGuid(0), reader.GetGuid(1), reader.GetString(2), reader.GetString(3), reader.GetInt64(5));
        // 缓存截止取「TTL 上限」与「会话自身过期时刻」的较早者，过期语义不被缓存放宽。
        var sessionExpiresAt = reader.GetFieldValue<DateTimeOffset>(4);
        var cacheUntil = now.Add(SessionCacheTtl);
        if (sessionExpiresAt < cacheUntil) cacheUntil = sessionExpiresAt;
        if (_sessionCache.Count >= SessionCacheLimit) _sessionCache.Clear();
        _sessionCache[cacheKey] = (identity, cacheUntil);
        return identity;
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
        // 提交成功后主动失效缓存项：登出/吊销对已缓存令牌的即时性不依赖 TTL。
        _sessionCache.TryRemove(Convert.ToHexString(tokenHash), out _);
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
            var renamed = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
            if (renamed)
            {
                // 道号会进入会话声明：改名成功后剔除该玩家的缓存身份，避免 /session 回旧道号。
                foreach (var pair in _sessionCache)
                {
                    if (pair.Value.Identity.PlayerId == playerId)
                        _sessionCache.TryRemove(pair.Key, out _);
                }
            }
            return renamed;
        }
        catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    /// <summary>
    /// 修改密码：先按库存哈希固定时间校验当前密码，再在同一 Serializable 事务内更新凭据、
    /// 吊销除当前会话外的全部存活会话并写审计事件；提交后剔除该账号的会话缓存条目
    /// （当前会话行未被吊销，下次校验会重新落库并回填缓存）。
    /// </summary>
    public async ValueTask<AccountChangePasswordResult> ChangePasswordAsync(
        Guid accountId,
        string currentPassword,
        string newPassword,
        byte[]? keepSessionTokenHash,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length is < MinPasswordLength or > MaxPasswordLength)
            return new AccountChangePasswordResult(false, "INVALID_PASSWORD", $"新密码长度须在 {MinPasswordLength}-{MaxPasswordLength} 个字符之间。");

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        byte[] hash;
        byte[] salt;
        int iterations;
        await using (var query = new NpgsqlCommand("""
            SELECT password_hash, password_salt, password_iterations
            FROM identity.accounts
            WHERE account_id = $1 AND status = 1
            """, connection))
        {
            query.Parameters.Add(new NpgsqlParameter { Value = accountId });
            await using var reader = await query.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new AccountChangePasswordResult(false, "INVALID_CREDENTIALS", "当前密码错误。");
            hash = reader.GetFieldValue<byte[]>(0);
            salt = reader.GetFieldValue<byte[]>(1);
            iterations = reader.GetInt32(2);
        }

        var candidate = Rfc2898DeriveBytes.Pbkdf2(currentPassword ?? string.Empty, salt, iterations, HashAlgorithmName.SHA512, hash.Length);
        if (!CryptographicOperations.FixedTimeEquals(candidate, hash))
            return new AccountChangePasswordResult(false, "INVALID_CREDENTIALS", "当前密码错误。");

        var newSalt = RandomNumberGenerator.GetBytes(16);
        var newHash = Rfc2898DeriveBytes.Pbkdf2(newPassword, newSalt, Pbkdf2Iterations, HashAlgorithmName.SHA512, 64);
        var now = DateTimeOffset.UtcNow;

        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        await using (var update = new NpgsqlCommand("""
            UPDATE identity.accounts
            SET password_hash = $2, password_salt = $3, password_iterations = $4, updated_at = $5
            WHERE account_id = $1
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter { Value = accountId });
            update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = newHash });
            update.Parameters.Add(new NpgsqlParameter { NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea, Value = newSalt });
            update.Parameters.Add(new NpgsqlParameter { Value = Pbkdf2Iterations });
            update.Parameters.Add(new NpgsqlParameter { Value = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var revoke = new NpgsqlCommand("""
            UPDATE identity.auth_sessions
            SET revoked_at = $2
            WHERE account_id = $1 AND revoked_at IS NULL AND ($3::bytea IS NULL OR token_hash <> $3)
            """, connection, transaction))
        {
            revoke.Parameters.Add(new NpgsqlParameter { Value = accountId });
            revoke.Parameters.Add(new NpgsqlParameter { Value = now });
            revoke.Parameters.Add(new NpgsqlParameter
            {
                NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Bytea,
                Value = (object?)keepSessionTokenHash ?? DBNull.Value,
            });
            await revoke.ExecuteNonQueryAsync(cancellationToken);
        }

        await outbox.AppendAsync(connection, transaction, accountId, "account",
            "auth.account.password_changed",
            JsonSerializer.SerializeToUtf8Bytes(new { accountId }),
            now, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        foreach (var pair in _sessionCache)
        {
            if (pair.Value.Identity.AccountId == accountId)
                _sessionCache.TryRemove(pair.Key, out _);
        }
        logger.LogInformation("账号改密完成 accountId={AccountId}", accountId);
        return new AccountChangePasswordResult(true, null, null);
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
    /// <summary>道号查重归一化口径：去首尾空白后转大写；好友检索必须与注册/改名使用同一口径。</summary>
    /// <param name="value">原始道号。</param>
    /// <returns>归一化后的查重键。</returns>
    public static string NormalizeNameForLookup(string value) => value.Trim().ToUpperInvariant();
    private static AccountRegistrationResult FailureRegistration(string code, string message) => new(false, null, null, null, null, code, message);
    private static AccountLoginResult FailureLogin(string code, string message) => new(false, null, null, null, null, code, message);
}
