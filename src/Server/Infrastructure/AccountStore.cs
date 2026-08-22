// ==============================================================================
// 文件名称: AccountStore.cs
// 主要职责: 提供基于 PostgreSQL 与 PBKDF2 的玩家账号注册、认证与道号管理。
// 核心逻辑 / 定义内容:
//   1. 定义账号实体记录 (AccountRecord) 与认证结果契约 (AuthResult)。
//   2. 实现基于 PBKDF2-SHA256 与密码盐 (Salt) 的安全哈希生成与恒定时间比对。
//   3. 提供账号注册格式校验、角色名唯一性保障与道号修改等数据库操作。
// ==============================================================================

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Server.Infrastructure;

/// <summary>
/// 账号实体数据记录。
/// </summary>
public sealed class AccountRecord
{
    public string Account { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public Guid PlayerId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public long SequenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// 鉴权操作（注册/登录）结果记录。
/// </summary>
public sealed record AuthResult(
    bool Success,
    string? Account,
    string? CharacterName,
    Guid? PlayerId,
    long SequenceId,
    string? Message = null,
    DateTimeOffset? CreatedAt = null);

/// <summary>
/// 账号认证仓储接口 (IAccountStore)。
/// </summary>
public interface IAccountStore
{
    /// <summary>
    /// 注册新账号：执行输入格式校验、密码盐化哈希并持久化入库。
    /// </summary>
    ValueTask<AuthResult> RegisterAsync(
        string account,
        string password,
        string? characterName,
        Func<string, bool> isHostNameTaken,
        Func<long> localSequenceProvider,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 账号登录验证：比对 PBKDF2 密码哈希。
    /// </summary>
    ValueTask<AuthResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);

    /// <summary>
    /// 更新角色的道号/角色名。
    /// </summary>
    ValueTask UpdateCharacterNameAsync(string account, string newName, CancellationToken cancellationToken = default);
}

/// <summary>
/// 账号仓储基础类：提供 PBKDF2-SHA256 密码哈希、随机加盐、恒定时间比对与账号/道号格式校验。
/// </summary>
public abstract class AccountStoreBase
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Pbkdf2Iterations = 120_000;
    private static readonly Regex AccountNamePattern = new(@"^[\u4e00-\u9fa5a-zA-Z0-9_]+$", RegexOptions.Compiled);

    /// <summary>
    /// 校验注册输入的账号、密码与道号格式。
    /// </summary>
    protected static AuthResult? ValidateRegistrationInput(string account, string password, string? characterName, out string cleanAccount, out string cleanName)
    {
        cleanAccount = string.Empty;
        cleanName = string.Empty;
        if (string.IsNullOrWhiteSpace(account))
        {
            return new AuthResult(false, null, null, null, 0, "登录账号不能为空。");
        }

        cleanAccount = account.Trim();
        if (cleanAccount.Length is < 2 or > 20)
        {
            return new AuthResult(false, null, null, null, 0, "登录账号长度须在 2 到 20 个字符之间。");
        }

        if (!AccountNamePattern.IsMatch(cleanAccount))
        {
            return new AuthResult(false, null, null, null, 0, "登录账号仅支持字母、数字、中文和下划线。");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 6 || password.Length > 64)
        {
            return new AuthResult(false, null, null, null, 0, "密码长度须在 6 到 64 个字符之间。");
        }

        cleanName = string.IsNullOrWhiteSpace(characterName) ? cleanAccount : characterName.Trim();
        if (cleanName.Length is < 2 or > 12)
        {
            return new AuthResult(false, null, null, null, 0, "修士道号长度须在 2 到 12 个字符之间。");
        }

        if (!AccountNamePattern.IsMatch(cleanName))
        {
            return new AuthResult(false, null, null, null, 0, "修士道号仅支持中文、字母、数字和下划线。");
        }

        return null;
    }

    /// <summary>
    /// 校验登录输入的非空。
    /// </summary>
    protected static AuthResult? ValidateLoginInput(string account, string password, out string cleanAccount)
    {
        cleanAccount = string.Empty;
        if (string.IsNullOrWhiteSpace(account))
        {
            return new AuthResult(false, null, null, null, 0, "请输入登录账号。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return new AuthResult(false, null, null, null, 0, "请输入登录密码。");
        }

        cleanAccount = account.Trim();
        return null;
    }

    /// <summary>
    /// 生成 16 字节随机盐并基于 PBKDF2-SHA256 计算密码哈希。
    /// </summary>
    protected static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        return (HashPassword(password, saltBytes, Pbkdf2Iterations), Convert.ToBase64String(saltBytes));
    }

    /// <summary>
    /// 使用恒定时间比较验证输入的密码明文与存储哈希。
    /// </summary>
    protected static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var iterations = ParseIterations(storedHash);
        var hash = HashPassword(password, saltBytes, iterations);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(storedHash));
    }

    /// <summary>
    /// 归一化键名（去除前后空白并转大写，用于不区分大小写与空白的唯一性比对）。
    /// </summary>
    protected static string NormalizeKey(string value) => value.Trim().ToUpperInvariant();

    private static string HashPassword(string password, byte[] salt, int iterations)
    {
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations,
            HashAlgorithmName.SHA256,
            HashBytes);
        return $"pbkdf2-sha256${iterations}${Convert.ToBase64String(hashBytes)}";
    }

    private static int ParseIterations(string storedHash)
    {
        var parts = storedHash.Split('$', 3);
        if (parts.Length == 3
            && string.Equals(parts[0], "pbkdf2-sha256", StringComparison.Ordinal)
            && int.TryParse(parts[1], out var parsed)
            && parsed is >= 10_000 and <= 2_000_000)
        {
            return parsed;
        }

        throw new InvalidDataException("账号密码哈希格式不受当前 PostgreSQL 存储支持。");
    }
}

/// <summary>
/// 基于 PostgreSQL 的账号仓储实现 (PostgresAccountStore)。
/// <para>
/// 职责：
/// 1. 将账号凭据与归一化键持久化于 accounts 表。
/// 2. 依赖 PostgreSQL 唯一索引约束排重，捕获 UniqueViolation (23505) 错误返回业务友好提示。
/// 3. 利用 PostgreSQL 自动递增主键 sequence_id 为每个账号分配单调序列号。
/// </para>
/// </summary>
public sealed class PostgresAccountStore(NpgsqlDataSource dataSource) : AccountStoreBase, IAccountStore
{
    public async ValueTask<AuthResult> RegisterAsync(
        string account,
        string password,
        string? characterName,
        Func<string, bool> isHostNameTaken,
        Func<long> localSequenceProvider,
        CancellationToken cancellationToken = default)
    {
        _ = localSequenceProvider;
        var validation = ValidateRegistrationInput(account, password, characterName, out var cleanAccount, out var cleanName);
        if (validation is not null) return validation;

        if (isHostNameTaken(cleanName))
        {
            return new AuthResult(false, null, null, null, 0, "该修士道号已被其他道友使用，请更换道号。");
        }

        var (hash, salt) = HashPassword(password);
        var playerId = Guid.NewGuid();
        try
        {
            await using var command = dataSource.CreateCommand(
                """
                INSERT INTO accounts (
                    player_id, account, account_normalized, password_hash, salt,
                    character_name, character_name_normalized)
                VALUES (
                    @playerId, @account, @accountNormalized, @passwordHash, @salt,
                    @characterName, @characterNameNormalized)
                RETURNING sequence_id, created_at
                """);
            command.Parameters.AddWithValue("playerId", playerId);
            command.Parameters.AddWithValue("account", cleanAccount);
            command.Parameters.AddWithValue("accountNormalized", NormalizeKey(cleanAccount));
            command.Parameters.AddWithValue("passwordHash", hash);
            command.Parameters.AddWithValue("salt", salt);
            command.Parameters.AddWithValue("characterName", cleanName);
            command.Parameters.AddWithValue("characterNameNormalized", NormalizeKey(cleanName));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("PostgreSQL 账号注册未返回序列号。 ");
            }

            var sequenceId = reader.GetInt64(0);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(1);
            return new AuthResult(true, cleanAccount, cleanName, playerId, sequenceId, "注册成功，欢迎踏入仙途！", createdAt);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return new AuthResult(false, null, null, null, 0, "登录账号或修士道号已被注册，请更换。 ");
        }
    }

    public async ValueTask<AuthResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        var validation = ValidateLoginInput(account, password, out var cleanAccount);
        if (validation is not null) return validation;

        await using var command = dataSource.CreateCommand(
            """
            SELECT account, password_hash, salt, player_id, character_name, sequence_id, created_at
            FROM accounts
            WHERE account_normalized = @accountNormalized
            """);
        command.Parameters.AddWithValue("accountNormalized", NormalizeKey(cleanAccount));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new AuthResult(false, null, null, null, 0, "账号不存在，请先注册道身。");
        }

        var storedAccount = reader.GetString(0);
        var passwordHash = reader.GetString(1);
        var salt = reader.GetString(2);
        var playerId = reader.GetGuid(3);
        var characterName = reader.GetString(4);
        var sequenceId = reader.GetInt64(5);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(6);
        if (!VerifyPassword(password, passwordHash, salt))
        {
            return new AuthResult(false, null, null, null, 0, "登录密码错误，请重新输入。 ");
        }

        return new AuthResult(true, storedAccount, characterName, playerId, sequenceId, "登录成功，仙道畅通！", createdAt);
    }

    public async ValueTask UpdateCharacterNameAsync(string account, string newName, CancellationToken cancellationToken = default)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE accounts
            SET character_name = @characterName,
                character_name_normalized = @characterNameNormalized,
                updated_at = NOW()
            WHERE account_normalized = @accountNormalized
            """);
        command.Parameters.AddWithValue("characterName", newName.Trim());
        command.Parameters.AddWithValue("characterNameNormalized", NormalizeKey(newName));
        command.Parameters.AddWithValue("accountNormalized", NormalizeKey(account));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
