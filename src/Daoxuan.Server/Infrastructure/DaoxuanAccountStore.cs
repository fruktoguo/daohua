using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;

namespace Daoxuan.Server.Infrastructure;

public sealed class DaoxuanAccountRecord
{
    public string Account { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Salt { get; set; } = string.Empty;
    public Guid PlayerId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public long SequenceId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed record AuthResult(
    bool Success,
    string? Account,
    string? CharacterName,
    Guid? PlayerId,
    long SequenceId,
    string? Message = null,
    DateTimeOffset? CreatedAt = null);

public interface IDaoxuanAccountStore
{
    ValueTask<AuthResult> RegisterAsync(
        string account,
        string password,
        string? characterName,
        Func<string, bool> isHostNameTaken,
        Func<long> localSequenceProvider,
        CancellationToken cancellationToken = default);

    ValueTask<AuthResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default);

    ValueTask UpdateCharacterNameAsync(string account, string newName, CancellationToken cancellationToken = default);
}

public abstract class DaoxuanAccountStoreBase
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int Pbkdf2Iterations = 120_000;
    private static readonly Regex AccountNamePattern = new(@"^[\u4e00-\u9fa5a-zA-Z0-9_]+$", RegexOptions.Compiled);

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

    protected static (string Hash, string Salt) HashPassword(string password)
    {
        var saltBytes = RandomNumberGenerator.GetBytes(SaltBytes);
        return (HashPassword(password, saltBytes, Pbkdf2Iterations), Convert.ToBase64String(saltBytes));
    }

    protected static bool VerifyPassword(string password, string storedHash, string storedSalt)
    {
        var saltBytes = Convert.FromBase64String(storedSalt);
        var iterations = ParseIterations(storedHash);
        var hash = HashPassword(password, saltBytes, iterations);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(hash),
            Encoding.UTF8.GetBytes(storedHash));
    }

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

        // 兼容开发期旧 JSON 存档；新注册一律使用带参数的格式。
        return 10_000;
    }
}

public sealed class DaoxuanLocalAccountStore : DaoxuanAccountStoreBase, IDaoxuanAccountStore
{
    private readonly string _filePath;
    private readonly ILogger<DaoxuanLocalAccountStore> _logger;
    private readonly ConcurrentDictionary<string, DaoxuanAccountRecord> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();

    public DaoxuanLocalAccountStore(DaoxuanStoragePaths paths, ILogger<DaoxuanLocalAccountStore> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _filePath = Path.Combine(paths.SnapshotDirectory, "accounts.json");
        Directory.CreateDirectory(paths.SnapshotDirectory);
        Load();
    }

    public ValueTask<AuthResult> RegisterAsync(
        string account,
        string password,
        string? characterName,
        Func<string, bool> isHostNameTaken,
        Func<long> localSequenceProvider,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateRegistrationInput(account, password, characterName, out var cleanAccount, out var cleanName);
        if (validation is not null) return ValueTask.FromResult(validation);

        if (_accounts.ContainsKey(cleanAccount))
        {
            return ValueTask.FromResult(new AuthResult(false, null, null, null, 0, "该登录账号已被注册，请直接登录或更换账号。"));
        }

        if (IsCharacterNameTaken(cleanName) || isHostNameTaken(cleanName))
        {
            return ValueTask.FromResult(new AuthResult(false, null, null, null, 0, "该修士道号已被其他道友使用，请更换道号。"));
        }

        var (hash, salt) = HashPassword(password);
        var record = new DaoxuanAccountRecord
        {
            Account = cleanAccount,
            PasswordHash = hash,
            Salt = salt,
            PlayerId = Guid.NewGuid(),
            CharacterName = cleanName,
            SequenceId = localSequenceProvider(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (!_accounts.TryAdd(cleanAccount, record))
        {
            return ValueTask.FromResult(new AuthResult(false, null, null, null, 0, "该登录账号已被注册。"));
        }

        Save();
        return ValueTask.FromResult(new AuthResult(true, record.Account, record.CharacterName, record.PlayerId, record.SequenceId, "注册成功，欢迎踏入仙途！", record.CreatedAt));
    }

    public ValueTask<AuthResult> LoginAsync(string account, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = ValidateLoginInput(account, password, out var cleanAccount);
        if (validation is not null) return ValueTask.FromResult(validation);

        if (!_accounts.TryGetValue(cleanAccount, out var record))
        {
            return ValueTask.FromResult(new AuthResult(false, null, null, null, 0, "账号不存在，请先注册道身。"));
        }

        if (!VerifyPassword(password, record.PasswordHash, record.Salt))
        {
            return ValueTask.FromResult(new AuthResult(false, null, null, null, 0, "登录密码错误，请重新输入。"));
        }

        return ValueTask.FromResult(new AuthResult(true, record.Account, record.CharacterName, record.PlayerId, record.SequenceId, "登录成功，仙道畅通！", record.CreatedAt));
    }

    public ValueTask UpdateCharacterNameAsync(string account, string newName, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_accounts.TryGetValue(account.Trim(), out var record))
        {
            record.CharacterName = newName.Trim();
            Save();
        }

        return ValueTask.CompletedTask;
    }

    private bool IsCharacterNameTaken(string characterName)
    {
        var clean = characterName.Trim();
        return _accounts.Values.Any(a => string.Equals(a.CharacterName, clean, StringComparison.OrdinalIgnoreCase));
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<DaoxuanAccountRecord>>(json);
            if (list is null) return;
            foreach (var item in list.Where(item => !string.IsNullOrWhiteSpace(item.Account)))
            {
                _accounts[item.Account] = item;
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException or FormatException)
        {
            _logger.LogError(exception, "加载本地账号文件失败 {Path}", _filePath);
            throw;
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var list = _accounts.Values.OrderBy(record => record.SequenceId).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = false });
            var temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(exception, "保存本地账号文件失败 {Path}", _filePath);
                throw;
            }
            finally
            {
                try
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}

public sealed class DaoxuanPostgresAccountStore(NpgsqlDataSource dataSource) : DaoxuanAccountStoreBase, IDaoxuanAccountStore
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
