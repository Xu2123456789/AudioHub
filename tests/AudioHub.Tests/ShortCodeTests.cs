using System.Net;
using AudioHub.Core.Connection;

namespace AudioHub.Tests;

/// <summary>
/// Pins the six-character address code.
/// </summary>
/// <remarks>
/// This is a cross-language value: the PC prints it and the phone decodes it, in two separate
/// implementations. The golden vectors below were produced by a third implementation (a short
/// Python script) precisely so that "both sides agree" means something. If one side ever drifts,
/// the failure is silent — the phone would dial a valid-looking but wrong address — so these
/// constants, not a round-trip test alone, are the real guard.
/// </remarks>
public sealed class ShortCodeTests
{
    [Theory]
    [InlineData("10.20.30.40", "0M3RM2")]
    [InlineData("192.168.1.5", "80042R")]
    [InlineData("172.20.3.4", "G40C29")]
    [InlineData("10.0.0.1", "00000P")]
    [InlineData("10.255.255.255", "7ZZZZY")]
    public void Encode_MatchesThePythonGoldenVectors(string address, string expected)
    {
        Assert.True(ShortCode.TryEncode(address, out string code));
        Assert.Equal(expected, code);
    }

    [Theory]
    [InlineData("10.20.30.40", "0M3RM2")]
    [InlineData("192.168.1.5", "80042R")]
    [InlineData("172.20.3.4", "G40C29")]
    public void Decode_ReadsTheGoldenVectorsBack(string expectedAddress, string code)
    {
        Assert.True(ShortCode.TryDecode(code, out IPAddress? address));
        Assert.Equal(expectedAddress, address!.ToString());
    }

    [Fact]
    public void RoundTrip_CoversEveryClass()
    {
        string[] addresses = ["10.1.2.3", "192.168.0.1", "172.16.0.1", "172.31.255.254", "10.0.0.1"];

        foreach (string text in addresses)
        {
            Assert.True(ShortCode.TryEncode(text, out string code), text);
            Assert.Equal(ShortCode.Length, code.Length);
            Assert.True(ShortCode.TryDecode(code, out IPAddress? decoded), code);
            Assert.Equal(text, decoded!.ToString());
        }
    }

    [Theory]
    [InlineData("0m3rm2")]
    [InlineData("0M3-RM2")]
    [InlineData(" 0m3 rm2 ")]
    public void Decode_ToleratesCaseSeparatorsAndSpaces(string typed)
    {
        Assert.True(ShortCode.TryDecode(typed, out IPAddress? address));
        Assert.Equal("10.20.30.40", address!.ToString());
    }

    [Fact]
    public void Decode_FoldsTheCharactersCrockfordRemoves()
    {
        // 8OO42R reads as 80042R: O is removed from the alphabet because it looks like zero.
        Assert.True(ShortCode.TryDecode("8OO42R", out IPAddress? address));
        Assert.Equal("192.168.1.5", address!.ToString());
    }

    [Fact]
    public void Decode_RejectsASingleMistypedCharacter()
    {
        // Last character changed: the checksum, not the address range, is what catches this.
        Assert.False(ShortCode.TryDecode("0M3RM3", out _));
    }

    [Fact]
    public void Decode_RejectsWrongLengthAndIllegalCharacters()
    {
        Assert.False(ShortCode.TryDecode("0M3RM", out _));
        Assert.False(ShortCode.TryDecode("0M3RM2X", out _));
        Assert.False(ShortCode.TryDecode("0M3RM!", out _));
        Assert.False(ShortCode.TryDecode("", out _));
        Assert.False(ShortCode.TryDecode(null, out _));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("192.169.1.1")]
    [InlineData("172.32.1.1")]
    [InlineData("172.15.1.1")]
    [InlineData("100.64.0.1")]
    public void Encode_RefusesAddressesTheLayoutCannotRepresent(string address)
    {
        Assert.False(ShortCode.TryEncode(address, out _));
    }

    [Fact]
    public void Encode_RefusesIpv6RatherThanTruncatingIt()
    {
        Assert.False(ShortCode.TryEncode(IPAddress.IPv6Loopback, out _));
    }
}
