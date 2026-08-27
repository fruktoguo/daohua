using System.Xml.Linq;
using Server.Clean;
using Xunit;

namespace Server.Tests;

public sealed class DataProtectionKeyRepositoryTests
{
    [Fact]
    public void KeyXmlMetadata_IsParsedAndInvalidIdRejected()
    {
        var id = Guid.NewGuid();
        var xml = XElement.Parse($"<key id=\"{id:D}\"><creationDate>2026-08-27T00:00:00Z</creationDate><expirationDate>2026-11-27T00:00:00Z</expirationDate></key>");
        Assert.Equal(id, PostgresDataProtectionKeyRepository.ReadKeyId(xml));
        Assert.Equal(DateTimeOffset.Parse("2026-08-27T00:00:00Z"), PostgresDataProtectionKeyRepository.ReadDate(xml, "creationDate"));
        Assert.Equal(DateTimeOffset.Parse("2026-11-27T00:00:00Z"), PostgresDataProtectionKeyRepository.ReadDate(xml, "expirationDate"));
        Assert.Throws<InvalidDataException>(() => PostgresDataProtectionKeyRepository.ReadKeyId(new XElement("key")));
    }
}
