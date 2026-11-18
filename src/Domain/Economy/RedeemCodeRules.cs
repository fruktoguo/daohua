using System.Security.Cryptography;

namespace Domain.Economy;

/// <summary>
/// 兑换码领域规范与密码学生码工具（总纲 §29）：
/// - 口令归一化：去首尾空白、ASCII 小写转大写，允许字符集 [A-Z0-9_-]，长度 4..32；
/// - 单次码生成：32 字符无歧义字母表（剔除 0/O/1/I），5-bit 位掩码零模偏采样。
/// </summary>
public static class RedeemCodeRules
{
    /// <summary>兑换码最短合法长度。</summary>
    public const int MinCodeLength = 4;

    /// <summary>兑换码最长合法长度。</summary>
    public const int MaxCodeLength = 32;

    /// <summary>单次码自定义前缀最大长度。</summary>
    public const int MaxPrefixLength = 8;

    /// <summary>单次码随机主体默认长度（10 位 × 5 bit = 50 bit 熵）。</summary>
    public const int DefaultRandomBodyLength = 10;

    /// <summary>单批次最少生成单次码数量。</summary>
    public const int MinSingleUseCount = 1;

    /// <summary>单批次最多生成单次码数量。</summary>
    public const int MaxSingleUseCount = 5000;

    /// <summary>单个批次最多配置的奖励槽位数。</summary>
    public const int MaxRewardsPerBatch = 12;

    /// <summary>批次标题最大长度。</summary>
    public const int MaxTitleLength = 120;

    /// <summary>配置过期天数的最大上限。</summary>
    public const int MaxExpireDays = 365;

    /// <summary>通用口令码模式标识。</summary>
    public const string ModeUniversal = "universal";

    /// <summary>一码一用批次单次码模式标识。</summary>
    public const string ModeSingleUse = "single_use";

    /// <summary>经济账本白名单交易类型（归属 ops.* 保留 30 天）。</summary>
    public const string LedgerTransactionType = "ops.redeem";

    /// <summary>32 字符无歧义字母表（剔除 0/O/1/I，长度恰好为 2^5）。</summary>
    public const string UnambiguousAlphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

    /// <summary>
    /// 尝试归一化用户或运营输入的兑换码：
    /// 去首尾空白、ASCII 小写转大写，校验长度 [4, 32] 且仅含 [A-Z0-9_-]。
    /// </summary>
    public static bool TryNormalizeCode(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var span = raw.AsSpan().Trim();
        if (span.Length < MinCodeLength || span.Length > MaxCodeLength) return false;

        Span<char> buffer = stackalloc char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch is >= 'a' and <= 'z')
                ch = (char)(ch - 32);

            if (!IsAllowedCodeChar(ch))
                return false;

            buffer[i] = ch;
        }

        normalized = new string(buffer);
        return true;
    }

    /// <summary>
    /// 尝试归一化单次码可选前缀：为空或纯空白时输出空串；非空时长度 1..8 且仅含 [A-Z0-9_-]。
    /// </summary>
    public static bool TryNormalizePrefix(string? raw, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        var span = raw.AsSpan().Trim();
        if (span.Length > MaxPrefixLength) return false;

        Span<char> buffer = stackalloc char[span.Length];
        for (var i = 0; i < span.Length; i++)
        {
            var ch = span[i];
            if (ch is >= 'a' and <= 'z')
                ch = (char)(ch - 32);

            if (!IsAllowedCodeChar(ch))
                return false;

            buffer[i] = ch;
        }

        normalized = new string(buffer);
        return true;
    }

    /// <summary>
    /// 使用密码学安全随机数生成单枚无歧义兑换码；若提供非空前缀且未以 '-' 或 '_' 结尾，则直接拼接随机主体。
    /// </summary>
    public static string GenerateRandomCode(string normalizedPrefix = "", int randomLength = DefaultRandomBodyLength)
    {
        if (randomLength < 6 || randomLength > 24)
            throw new ArgumentOutOfRangeException(nameof(randomLength), "随机码主体长度必须在 6..24 之间。");
        if (normalizedPrefix.Length + randomLength > MaxCodeLength)
            throw new ArgumentOutOfRangeException(nameof(normalizedPrefix), "前缀加随机主体总长度超出上限。");

        Span<byte> randomBytes = stackalloc byte[randomLength];
        RandomNumberGenerator.Fill(randomBytes);

        var totalLength = normalizedPrefix.Length + randomLength;
        Span<char> output = stackalloc char[totalLength];
        normalizedPrefix.AsSpan().CopyTo(output);

        for (var i = 0; i < randomLength; i++)
        {
            output[normalizedPrefix.Length + i] = UnambiguousAlphabet[randomBytes[i] & 0x1F];
        }

        return new string(output);
    }

    private static bool IsAllowedCodeChar(char ch)
        => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_';
}
