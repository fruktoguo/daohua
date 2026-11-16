using System.Net;
using Server.Clean.Social;
using Xunit;

namespace Server.Tests;

public sealed class StickerEndpointsSecurityTests
{
    [Theory]
    [InlineData("http://example.com/a.png", false)]
    [InlineData("https://127.0.0.1/a.png", false)]
    [InlineData("https://[::1]/a.png", false)]
    [InlineData("https://user:pass@example.com/a.png", false)]
    [InlineData("https://example.com:8443/a.png", false)]
    [InlineData("https://localhost/a.png", false)]
    [InlineData("https://printer.local/a.png", false)]
    [InlineData("https://beeimg.us.ci/temp/0125d2b5e5b5af6ab4516d5fc3085b98.png", true)]
    public void TryValidatePublicHttpsUri_EnforcesStrictPublicHttps(string rawUrl, bool expected)
    {
        var ok = StickerEndpoints.TryValidatePublicHttpsUri(rawUrl, out var uri, out _);
        Assert.Equal(expected, ok);
        if (expected) Assert.NotNull(uri);
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:10.10.10.10", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("8.8.8.8", true)]
    public void IsPublicUnicastAddress_BlocksPrivateAndReservedRanges(string ipString, bool expected)
    {
        var ip = IPAddress.Parse(ipString);
        Assert.Equal(expected, StickerEndpoints.IsPublicUnicastAddress(ip));
    }

    [Fact]
    public void DetectSafeRasterMime_RecognizesRasterMagicAndRejectsHtmlOrSvg()
    {
        Assert.Equal("image/png", StickerEndpoints.DetectSafeRasterMime([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]));
        Assert.Equal("image/jpeg", StickerEndpoints.DetectSafeRasterMime([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/gif", StickerEndpoints.DetectSafeRasterMime("GIF89a..."u8));
        Assert.Equal("image/webp", StickerEndpoints.DetectSafeRasterMime("RIFF\x00\x00\x00\x00WEBP"u8));
        Assert.Null(StickerEndpoints.DetectSafeRasterMime("<svg xmlns='http://www.w3.org/2000/svg'></svg>"u8));
        Assert.Null(StickerEndpoints.DetectSafeRasterMime("<html>not found</html>"u8));
    }
}
