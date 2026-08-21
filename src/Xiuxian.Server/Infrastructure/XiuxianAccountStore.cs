using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Xiuxian.Server.Infrastructure;

public sealed class XiuxianAccountRecord
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
    string? Message = null);

public sealed class XiuxianAccountStore
{
    private readonly string _filePath;
    private readonly ConcurrentDictionary<string, XiuxianAccountRecord> _accounts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _saveLock = new();

    public XiuxianAccountStore(XiuxianStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _filePath = Path.Combine(paths.SnapshotDirectory, "accounts.json");
        Load();
    }

    public int AccountCount => _accounts.Count;

    public bool HasAccount(string account) => _accounts.ContainsKey(account.Trim());

    public bool TryGetAccount(string account, out XiuxianAccountRecord? record) =>
        _accounts.TryGetValue(account.Trim(), out record);

    public bool IsCharacterNameTaken(string characterName)
    {
        var clean = characterName.Trim();
        return _accounts.Values.Any(a => string.Equals(a.CharacterName, clean, StringComparison.OrdinalIgnoreCase));
    }

    public AuthResult Register(
        string account,
        string password,
        string? characterName,
        Func<string, bool> isHostNameTaken,
        Func<long> nextSequenceId)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return new AuthResult(false, null, null, null, 0, "登录账号不能为空。");
        }

        var cleanAccount = account.Trim();
        if (cleanAccount.Length is < 2 or > 20)
        {
            return new AuthResult(false, null, null, null, 0, "登录账号长度须在 2 到 20 个字符之间。");
        }

        if (!Regex.IsMatch(cleanAccount, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
        {
            return new AuthResult(false, null, null, null, 0, "登录账号仅支持字母、数字、中文和下划线。");
        }

        if (string.IsNullOrWhiteSpace(password) || password.Length < 6 || password.Length > 32)
        {
            return new AuthResult(false, null, null, null, 0, "密码长度须在 6 到 32 个字符之间。");
        }

        if (_accounts.ContainsKey(cleanAccount))
        {
            return new AuthResult(false, null, null, null, 0, "该登录账号已被注册，请直接登录或更换账号。");
        }

        var cleanName = string.IsNullOrWhiteSpace(characterName) ? cleanAccount : characterName.Trim();
        if (cleanName.Length is < 2 or > 12)
        {
            return new AuthResult(false, null, null, null, 0, "修士道号长度须在 2 到 12 个字符之间。");
        }

        if (!Regex.IsMatch(cleanName, @"^[\u4e00-\u9fa5a-zA-Z0-9_]+$"))
        {
            return new AuthResult(false, null, null, null, 0, "修士道号仅支持中文、字母、数字和下划线。");
        }

        if (IsCharacterNameTaken(cleanName) || isHostNameTaken(cleanName))
        {
            return new AuthResult(false, null, null, null, 0, "该修士道号已被其他道友使用，请更换道号。");
        }

        var saltBytes = RandomNumberGenerator.GetBytes(16);
        var salt = Convert.ToBase64String(saltBytes);
        var hash = HashPassword(password, saltBytes);

        var record = new XiuxianAccountRecord
        {
            Account = cleanAccount,
            PasswordHash = hash,
            Salt = salt,
            PlayerId = Guid.NewGuid(),
            CharacterName = cleanName,
            SequenceId = nextSequenceId(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        if (!_accounts.TryAdd(cleanAccount, record))
        {
            return new AuthResult(false, null, null, null, 0, "该登录账号已被注册。");
        }

        Save();
        return new AuthResult(true, record.Account, record.CharacterName, record.PlayerId, record.SequenceId, "注册成功，欢迎踏入仙途！");
    }

    public AuthResult Login(string account, string password)
    {
        if (string.IsNullOrWhiteSpace(account))
        {
            return new AuthResult(false, null, null, null, 0, "请输入登录账号。");
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            return new AuthResult(false, null, null, null, 0, "请输入登录密码。");
        }

        var cleanAccount = account.Trim();
        if (!_accounts.TryGetValue(cleanAccount, out var record))
        {
            return new AuthResult(false, null, null, null, 0, "账号不存在，请先注册道身。");
        }

        var saltBytes = Convert.FromBase64String(record.Salt);
        var computedHash = HashPassword(password, saltBytes);

        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computedHash),
            Encoding.UTF8.GetBytes(record.PasswordHash)))
        {
            return new AuthResult(false, null, null, null, 0, "登录密码错误，请重新输入。");
        }

        return new AuthResult(true, record.Account, record.CharacterName, record.PlayerId, record.SequenceId, "登录成功，仙道畅通！");
    }

    public void UpdateCharacterName(string account, string newName)
    {
        if (_accounts.TryGetValue(account.Trim(), out var record))
        {
            record.CharacterName = newName.Trim();
            Save();
        }
    }

    private static string HashPassword(string password, byte[] salt)
    {
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password,
            salt,
            iterations: 10_000,
            hashAlgorithm: HashAlgorithmName.SHA256,
            outputLength: 32);
        return Convert.ToBase64String(hashBytes);
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        try
        {
            var json = File.ReadAllText(_filePath);
            var list = JsonSerializer.Deserialize<List<XiuxianAccountRecord>>(json);
            if (list is not null)
            {
                foreach (var item in list)
                {
                    _accounts[item.Account] = item;
                }
            }
        }
        catch
        {
            // 容错处理
        }
    }

    private void Save()
    {
        lock (_saveLock)
        {
            try
            {
                var list = _accounts.Values.ToList();
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                var temp = $"{_filePath}.{Guid.NewGuid():N}.tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, _filePath, overwrite: true);
            }
            catch
            {
                // 忽略非致命 IO 异常
            }
        }
    }
}
