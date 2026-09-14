namespace DaoHua.ComputeNode;

/// <summary>
/// 计算节点内部令牌校验器。
/// 使用固定时间摘要比较，防止通过响应时间侧信道推断 token 长度。
/// </summary>
internal static class TokenValidator
{
    /// <summary>允许的最大令牌长度。</summary>
    private const int MaxTokenLength = 512;

    /// <summary>
    /// 校验请求是否携带有效的内部计算令牌。
    /// </summary>
    /// <param name="request">当前 HTTP 请求。</param>
    /// <param name="configuredToken">已配置的令牌原文。</param>
    /// <returns>如果令牌有效则返回 <see langword="true"/>；否则返回 <see langword="false"/>。</returns>
    public static bool IsValid(HttpRequest request, string configuredToken)
    {
        var supplied = request.Headers[ComputeContract.TokenHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(supplied))
        {
            var authorization = request.Headers.Authorization.FirstOrDefault();
            if (authorization?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true)
            {
                supplied = authorization[7..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(supplied)
            || supplied.Length > MaxTokenLength
            || string.IsNullOrWhiteSpace(configuredToken)
            || configuredToken.Length > MaxTokenLength)
            return false;

        // 先做固定长度摘要再比较，避免 FixedTimeEquals 因原始 token 长度不同而提前返回。
        var suppliedDigest = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var configuredDigest = SHA256.HashData(Encoding.UTF8.GetBytes(configuredToken));
        return CryptographicOperations.FixedTimeEquals(suppliedDigest, configuredDigest);
    }
}
