namespace DaoHua.ComputeNode;

internal static class TokenValidator
{
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

        if (string.IsNullOrWhiteSpace(supplied)) return false;
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        var configuredBytes = Encoding.UTF8.GetBytes(configuredToken);
        return CryptographicOperations.FixedTimeEquals(suppliedBytes, configuredBytes);
    }
}
