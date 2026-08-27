using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class AccountPolicyTests
{
    [Fact]
    public void PasswordLengthPolicyIsSixToTwentyFour()
    {
        Assert.Equal(6, PostgresAccountRepository.MinPasswordLength);
        Assert.Equal(24, PostgresAccountRepository.MaxPasswordLength);
        Assert.InRange("123456".Length, PostgresAccountRepository.MinPasswordLength, PostgresAccountRepository.MaxPasswordLength);
        Assert.InRange(new string('x', 24).Length, PostgresAccountRepository.MinPasswordLength, PostgresAccountRepository.MaxPasswordLength);
        Assert.True("12345".Length < PostgresAccountRepository.MinPasswordLength);
        Assert.True(new string('x', 25).Length > PostgresAccountRepository.MaxPasswordLength);
    }
}
