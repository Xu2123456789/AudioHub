using System.Buffers.Binary;
using AudioHub.Core.Discovery;

namespace AudioHub.Tests;

/// <summary>
/// Pins the discovery wire format.
/// </summary>
/// <remarks>
/// Every parse here consumes bytes that came off the network, so each rejection test is worth as
/// much as each round trip: something that accepts a malformed packet today will be handed
/// attacker-controlled bytes by whoever finds the port. The traps are written out field by field
/// rather than generated, so a future change to the layout has to be made deliberately.
/// </remarks>
public class DiscoveryProtocolTests
{
    private static readonly Guid SampleHubId = Guid.Parse("8f2c1d4e-5b6a-4c8d-9e0f-1a2b3c4d5e6f");

    [Fact]
    public void Probe_RoundTrips()
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(0xCAFEBABE);

        Assert.True(DiscoveryProtocol.TryParse(packet, out DiscoveryMessage message, out string? rejection), rejection);

        Assert.Equal(DiscoveryMessageType.Probe, message.Type);
        Assert.Equal(0xCAFEBABEu, message.Token);
    }

    [Fact]
    public void Announce_RoundTripsEveryField()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(
            token: 0x12345678,
            hubId: SampleHubId,
            vbanPort: 6_980,
            securityMode: SecurityMode.Optional,
            name: "dell-g15");

        Assert.True(DiscoveryProtocol.TryParse(packet, out DiscoveryMessage message, out string? rejection), rejection);

        Assert.Equal(DiscoveryMessageType.Announce, message.Type);
        Assert.Equal(0x12345678u, message.Token);
        Assert.Equal(SampleHubId, message.HubId);
        Assert.Equal(6_980, message.VbanPort);
        Assert.Equal(SecurityMode.Optional, message.SecurityMode);
        Assert.Equal("dell-g15", message.Name);
    }

    [Fact]
    public void Announce_WithNoName_RoundTrips()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(1u, SampleHubId, 6_980, SecurityMode.None, string.Empty);

        Assert.True(DiscoveryProtocol.TryParse(packet, out DiscoveryMessage message, out _));

        Assert.Equal(string.Empty, message.Name);
    }

    /// <summary>
    /// The cross-platform trap. Another language's UUID type arranges its bytes differently from
    /// how .NET lays out a GUID, so the hub id travels in RFC 4122 big-endian order. If this ever
    /// reverts to little-endian it will still round trip in C#, pass everything else, and then
    /// disagree with Android the moment both are plugged together.
    /// </summary>
    [Fact]
    public void HubId_TravelsAsBigEndianRfc4122()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, 6_980, SecurityMode.None, string.Empty);

        // Header is 10 bytes; payload starts with a 4-byte token, so the id sits at offset 14 and
        // must begin with the same bytes the canonical text form starts with.
        ReadOnlySpan<byte> idBytes = packet.AsSpan(14, 16);

        Assert.Equal(0x8f, idBytes[0]);
        Assert.Equal(0x2c, idBytes[1]);
        Assert.Equal(0x1d, idBytes[2]);
        Assert.Equal(0x4e, idBytes[3]);

        // And reading it back through Guid.Parse of the same string agrees.
        Assert.Equal(SampleHubId, new Guid(idBytes, bigEndian: true));
    }

    /// <summary>
    /// The cross-language anchor. This exact byte string is asserted on the Kotlin side too, so
    /// the two implementations cannot drift apart unnoticed: if either changes its layout, one of
    /// the two tests goes red even though each still round-trips happily on its own.
    /// </summary>
    [Fact]
    public void Announce_LayoutIsByteForByteStable()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(
            token: 0x12345678,
            hubId: SampleHubId,
            vbanPort: 6_980,
            securityMode: SecurityMode.Optional,
            name: "hub");

        Assert.Equal(
            "41485542010200001B00785634128F2C1D4E5B6A4C8D9E0F1A2B3C4D5E6F441B0103687562",
            Convert.ToHexString(packet));
    }

    [Fact]
    public void WrongMagic_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(1u);
        packet[0] = 0x00;

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("魔数", rejection);
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(1u);
        packet[4] = 99;

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("版本", rejection);
    }

    [Fact]
    public void UnknownMessageType_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(1u);
        packet[5] = 0x7F;

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("类型", rejection);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(9)]
    public void TruncatedPacket_IsRejected(int length)
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(1u)[..length];

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.NotNull(rejection);
    }

    [Fact]
    public void WrongDeclaredPayloadLength_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildProbe(1u);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), 200);

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("载荷长度", rejection);
    }

    [Fact]
    public void OversizedPacket_IsRejected()
    {
        byte[] packet = new byte[DiscoveryProtocol.MaxDatagramBytes + 1];
        DiscoveryProtocol.Magic.CopyTo(packet);

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("太长", rejection);
    }

    [Fact]
    public void AnnounceShorterThanItsFixedPart_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, 6_980, SecurityMode.None, string.Empty);
        Array.Resize(ref packet, packet.Length - 4);

        // Tuple does not match either, so anything that does not check length first will read
        // past the end or mis-parse rather than reject.
        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.NotNull(rejection);
    }

    [Fact]
    public void UnknownSecurityMode_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, 6_980, SecurityMode.None, string.Empty);
        packet[10 + 22] = 200;

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("安全模式", rejection);
    }

    [Fact]
    public void WrongDeclaredNameLength_IsRejected()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, 6_980, SecurityMode.None, "abc");
        packet[10 + 23] = 42;

        Assert.False(DiscoveryProtocol.TryParse(packet, out _, out string? rejection));
        Assert.Contains("名字长度", rejection);
    }

    [Theory]
    [InlineData(SecurityMode.None)]
    [InlineData(SecurityMode.Optional)]
    [InlineData(SecurityMode.Required)]
    public void EverySecurityMode_RoundTrips(SecurityMode mode)
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, 6_980, mode, "hub");

        Assert.True(DiscoveryProtocol.TryParse(packet, out DiscoveryMessage message, out _));
        Assert.Equal(mode, message.SecurityMode);
    }

    [Fact]
    public void Port_IsCarriedAsSixteenBits()
    {
        byte[] packet = DiscoveryProtocol.BuildAnnounce(0u, SampleHubId, vbanPort: 65_535, SecurityMode.None, string.Empty);

        Assert.True(DiscoveryProtocol.TryParse(packet, out DiscoveryMessage message, out _));
        Assert.Equal(65_535, message.VbanPort);
    }
}
