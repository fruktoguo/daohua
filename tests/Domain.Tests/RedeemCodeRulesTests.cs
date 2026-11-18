using Domain.Economy;
using Xunit;

namespace Domain.Tests;

/// <summary>
/// 兑换码编码规约与无歧义密码学生码单元测试（总纲 §29.2）。
/// </summary>
public sealed class RedeemCodeRulesTests
{
    [Theory]
    [InlineData("  daohua666  ", "DAOHUA666")]
    [InlineData("vip-2026_ok", "VIP-2026_OK")]
    [InlineData("ABCD", "ABCD")]
    public void TryNormalizeCode_TrimsAndUppercasesValidCode(string raw, string expected)
    {
        Assert.True(RedeemCodeRules.TryNormalizeCode(raw, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ABC")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567")]
    [InlineData("DAO HUA")]
    [InlineData("礼包666")]
    [InlineData("CODE!@#")]
    public void TryNormalizeCode_RejectsOutOfRangeOrDisallowedCharacters(string? raw)
    {
        Assert.False(RedeemCodeRules.TryNormalizeCode(raw, out _));
    }

    [Fact]
    public void GenerateRandomCode_UsesUnambiguousAlphabet_AndAppliesPrefix()
    {
        Assert.True(RedeemCodeRules.TryNormalizePrefix(" tap- ", out var prefix));
        Assert.Equal("TAP-", prefix);

        var allowed = RedeemCodeRules.UnambiguousAlphabet.ToHashSet();
        var generated = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 500; i++)
        {
            var code = RedeemCodeRules.GenerateRandomCode(prefix, randomLength: 10);
            Assert.Equal(14, code.Length);
            Assert.StartsWith("TAP-", code, StringComparison.Ordinal);

            var body = code.AsSpan(prefix.Length);
            foreach (var ch in body)
            {
                Assert.Contains(ch, allowed);
                Assert.False(ch is '0' or 'O' or '1' or 'I');
            }

            Assert.True(RedeemCodeRules.TryNormalizeCode(code, out var normalized));
            Assert.Equal(code, normalized);
            generated.Add(code);
        }

        // 50 bit 熵空间下 500 次采样不应发生重复碰撞。
        Assert.Equal(500, generated.Count);
    }
}
