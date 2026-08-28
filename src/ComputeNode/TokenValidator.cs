namespace DaoHua.ComputeNode;

internal static class TokenValidator
{
    private const int MaxTokenLength = 512;

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
