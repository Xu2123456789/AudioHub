using System.Buffers.Binary;
using System.Text;

namespace AudioHub.Core.Discovery;

/// <summary>
/// Wire format for asking "which AudioHub is out there?" over the local network.
/// </summary>
/// <remarks>
/// <para>
/// Two messages on UDP port 6979, following the "phone asks, hub answers" shape:
/// the phone broadcasts a <see cref="DiscoveryMessageType.Probe"/> to the subnet broadcast
/// address, and every hub that hears it replies with a unicast
/// <see cref="DiscoveryMessageType.Announce"/> straight back to whoever asked.
/// </para>
/// <para>
/// Why this direction rather than the hub announcing itself to everyone: receiving a broadcast
/// depends on how the phone's Wi-Fi handles multicast and broadcast, and that varies by ROM and
/// by power-saving state, which means a failure looks like silence and is very hard to prove from
/// the outside. Sending a broadcast and then receiving an ordinary unicast reply is standard
/// behaviour everywhere, with no extra permission on Android and no extra lock to hold.
/// </para>
/// <para>
/// Every field is fixed-width and little-endian except the hub id, which deliberately uses the
/// RFC 4122 big-endian layout so that another platform can read it without knowing how .NET
/// happens to arrange a GUID in memory.
/// </para>
/// </remarks>
public static class DiscoveryProtocol
{
    /// <summary>UDP port hubs listen on for probes.</summary>
    public const int Port = 6_979;

    /// <summary>Largest datagram accepted. Generous for the format, small enough to be obviously not an attack.</summary>
    public const int MaxDatagramBytes = 512;

    private const int HeaderBytes = 10;
    private const int Version = 1;

    /// <summary>Marks our packets. 'A','H','U','B'.</summary>
    public static ReadOnlySpan<byte> Magic => [0x41, 0x48, 0x55, 0x42];

    /// <summary>Builds a probe asking any hub on the subnet to identify itself.</summary>
    /// <param name="token">
    /// Random value echoed back in the reply. Lets a phone tell which answer belongs to which
    /// request when several hubs reply, and stops a stale reply from being mistaken for a fresh one.
    /// </param>
    public static byte[] BuildProbe(uint token)
    {
        byte[] packet = new byte[HeaderBytes + 4];
        WriteHeader(packet, DiscoveryMessageType.Probe, payloadLength: 4);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(HeaderBytes), token);
        return packet;
    }

    /// <summary>Builds the reply a hub sends back to whoever probed it.</summary>
    public static byte[] BuildAnnounce(uint token, Guid hubId, int vbanPort, SecurityMode securityMode, string name)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(Truncate(name));
        byte[] packet = new byte[HeaderBytes + 24 + nameBytes.Length];

        WriteHeader(packet, DiscoveryMessageType.Announce, payloadLength: 24 + nameBytes.Length);

        Span<byte> payload = packet.AsSpan(HeaderBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(payload, token);
        hubId.TryWriteBytes(payload[4..20], bigEndian: true, out _);
        BinaryPrimitives.WriteUInt16LittleEndian(payload[20..22], (ushort)vbanPort);
        payload[22] = (byte)securityMode;
        payload[23] = (byte)nameBytes.Length;
        nameBytes.CopyTo(payload[24..]);

        return packet;
    }

    /// <summary>
    /// Reads one datagram. Returns false with a reason rather than throwing, because everything
    /// arriving here comes from the network and throwing on malformed input is how a stranger
    /// gets to crash an audio pipeline.
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> packet, out DiscoveryMessage message, out string? rejection)
    {
        message = default;
        rejection = null;

        if (packet.Length > MaxDatagramBytes)
        {
            rejection = $"包太长（{packet.Length} 字节，上限 {MaxDatagramBytes}）";
            return false;
        }

        if (packet.Length < HeaderBytes)
        {
            rejection = $"连包头都不够长（{packet.Length} 字节）";
            return false;
        }

        if (!packet[..4].SequenceEqual(Magic))
        {
            rejection = "魔数不对（不是 AudioHub 的包）";
            return false;
        }

        byte version = packet[4];
        if (version != Version)
        {
            rejection = $"版本不符（收到 {version}，要求 {Version}）";
            return false;
        }

        DiscoveryMessageType type = (DiscoveryMessageType)packet[5];
        int payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(packet[8..10]);

        if (packet.Length != HeaderBytes + payloadLength)
        {
            rejection = $"声明的载荷长度 {payloadLength} 与实际 {packet.Length - HeaderBytes} 不符";
            return false;
        }

        ReadOnlySpan<byte> payload = packet[HeaderBytes..];

        switch (type)
        {
            case DiscoveryMessageType.Probe:
                if (payloadLength < 4)
                {
                    rejection = "询问包缺随机标识";
                    return false;
                }

                message = new DiscoveryMessage(
                    type,
                    BinaryPrimitives.ReadUInt32LittleEndian(payload),
                    Guid.Empty,
                    0,
                    SecurityMode.None,
                    string.Empty);
                return true;

            case DiscoveryMessageType.Announce:
                return TryParseAnnounce(payload, out message, out rejection);

            default:
                rejection = $"未知的消息类型 {packet[5]}";
                return false;
        }
    }

    private static bool TryParseAnnounce(
        ReadOnlySpan<byte> payload,
        out DiscoveryMessage message,
        out string? rejection)
    {
        message = default;
        rejection = null;

        if (payload.Length < 24)
        {
            rejection = $"应答包太短（{payload.Length} 字节，至少要 24）";
            return false;
        }

        SecurityMode securityMode = (SecurityMode)payload[22];
        if (securityMode is not (SecurityMode.None or SecurityMode.Optional or SecurityMode.Required))
        {
            rejection = $"未知的安全模式标记 {payload[22]}";
            return false;
        }

        int nameLength = payload[23];
        if (payload.Length != 24 + nameLength)
        {
            rejection = $"声明的名字长度 {nameLength} 与实际 {payload.Length - 24} 不符";
            return false;
        }

        string name = nameLength == 0 ? string.Empty : Encoding.UTF8.GetString(payload[24..]);

        message = new DiscoveryMessage(
            DiscoveryMessageType.Announce,
            BinaryPrimitives.ReadUInt32LittleEndian(payload),
            new Guid(payload[4..20], bigEndian: true),
            BinaryPrimitives.ReadUInt16LittleEndian(payload[20..22]),
            securityMode,
            name);
        return true;
    }

    private static void WriteHeader(Span<byte> packet, DiscoveryMessageType type, int payloadLength)
    {
        Magic.CopyTo(packet);
        packet[4] = Version;
        packet[5] = (byte)type;
        packet[6] = 0;
        packet[7] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(packet[8..10], (ushort)payloadLength);
    }

    private static string Truncate(string name)
    {
        // Leaves room inside MaxDatagramBytes for the shortest possible header per character.
        const int maxNameBytes = MaxDatagramBytes - HeaderBytes - 24;
        byte[] bytes = Encoding.UTF8.GetBytes(name ?? string.Empty);
        return bytes.Length <= maxNameBytes
            ? name ?? string.Empty
            : Encoding.UTF8.GetString(bytes, 0, maxNameBytes);
    }
}

/// <summary>Kind of discovery message.</summary>
public enum DiscoveryMessageType : byte
{
    /// <summary>Phone asking which hubs are present.</summary>
    Probe = 1,

    /// <summary>Hub answering a probe.</summary>
    Announce = 2,
}

/// <summary>
/// What a hub expects of senders, advertised so the phone can ask for a key before it starts.
/// </summary>
public enum SecurityMode : byte
{
    /// <summary>Plain VBAN accepted. The current, pre-encryption behaviour.</summary>
    None = 0,

    /// <summary>Encrypted accepted, plain still tolerated while a pairing rolls out.</summary>
    Optional = 1,

    /// <summary>Only encrypted accepted.</summary>
    Required = 2,
}

/// <summary>A parsed discovery message.</summary>
/// <param name="Type">Which message it was.</param>
/// <param name="Token">Echo value from the probe.</param>
/// <param name="HubId">Identity of the hub that answered.</param>
/// <param name="VbanPort">Port the hub is receiving VBAN audio on.</param>
/// <param name="SecurityMode">What the hub expects of senders.</param>
/// <param name="Name">Human-readable hub name, typically the machine name.</param>
public readonly record struct DiscoveryMessage(
    DiscoveryMessageType Type,
    uint Token,
    Guid HubId,
    int VbanPort,
    SecurityMode SecurityMode,
    string Name);
