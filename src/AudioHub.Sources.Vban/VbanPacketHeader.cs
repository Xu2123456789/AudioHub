using System.Buffers.Binary;
using AudioHub.Core;

namespace AudioHub.Sources.Vban;

/// <summary>
/// Sub-protocol selector carried in the top three bits of the sample-rate byte.
/// </summary>
public enum VbanSubProtocol : byte
{
    /// <summary>Native PCM audio. The only kind this project consumes.</summary>
    Audio = 0x00,

    /// <summary>Serial data, including MIDI.</summary>
    Serial = 0x20,

    /// <summary>Text commands.</summary>
    Text = 0x40,

    /// <summary>Audio and RT packet services.</summary>
    Service = 0x60,

    /// <summary>Vendor-defined.</summary>
    User = 0xE0,
}

/// <summary>
/// Codec selector carried in the top four bits of the format byte.
/// </summary>
public enum VbanCodec : byte
{
    /// <summary>Uncompressed PCM, which is what the free VBAN protocol base covers.</summary>
    Pcm = 0x00,

    /// <summary>VB-Audio AOIP codec.</summary>
    Vbca = 0x10,

    /// <summary>VB-Audio VOIP codec.</summary>
    Vbcv = 0x20,

    /// <summary>Vendor-defined codec.</summary>
    User = 0xF0,
}

/// <summary>
/// Sample representation carried in the low three bits of the format byte.
/// </summary>
/// <remarks>
/// The indexes come from the VBAN specification revision 13. Note that the last two have
/// no documented storage width anywhere in that document — see
/// <see cref="VbanPacketHeader.TryGetPayloadSize"/> for what we do about that.
/// </remarks>
public enum VbanDataType : byte
{
    /// <summary>8-bit unsigned integer, silence at 128.</summary>
    Byte8 = 0,

    /// <summary>16-bit signed integer.</summary>
    Int16 = 1,

    /// <summary>24-bit signed integer.</summary>
    Int24 = 2,

    /// <summary>32-bit signed integer.</summary>
    Int32 = 3,

    /// <summary>32-bit IEEE float.</summary>
    Float32 = 4,

    /// <summary>64-bit IEEE float.</summary>
    Float64 = 5,

    /// <summary>12-bit integer. Storage width is not documented in the specification.</summary>
    Bits12 = 6,

    /// <summary>10-bit integer. Storage width is not documented in the specification.</summary>
    Bits10 = 7,
}

/// <summary>
/// The 28-byte header that opens every VBAN packet.
/// </summary>
/// <remarks>
/// <para>
/// Layout, straight from VBAN Protocol Specification revision 13, little-endian and packed:
/// four bytes of "VBAN", one byte carrying a five-bit sample-rate index plus a three-bit
/// sub-protocol, one byte for (sample count - 1), one byte for (channel count - 1), one byte
/// carrying a three-bit data type plus a four-bit codec, sixteen bytes of stream name,
/// and a 32-bit frame counter.
/// </para>
/// <para>
/// The specification's own reference check is <c>header.vban == 'NABV'</c>. That is not a
/// typo: reading those four bytes as a little-endian <c>uint32</c> reverses them, so the
/// multi-character literal has to be written backwards. Comparing the bytes individually,
/// as we do here, reads naturally and avoids the trap.
/// </para>
/// </remarks>
public readonly record struct VbanPacketHeader(
    VbanSubProtocol Protocol,
    int SampleRate,
    int SampleCount,
    int Channels,
    VbanDataType DataType,
    VbanCodec Codec,
    string StreamName,
    uint FrameCounter)
{
    /// <summary>Size of the VBAN header, in bytes.</summary>
    public const int Size = 28;

    /// <summary>Largest UDP payload VBAN is meant to use, to stay inside a 1500-byte MTU.</summary>
    public const int MaxPacketBytes = 1464;

    /// <summary>
    /// Sample rate table indexed by <c>format_SR &amp; 0x1F</c>, verbatim from the specification.
    /// Indexes past the end are undefined and must be rejected rather than extrapolated.
    /// </summary>
    private static readonly int[] SampleRateTable =
    [
        6_000, 12_000, 24_000, 48_000, 96_000, 192_000, 384_000,
        8_000, 16_000, 32_000, 64_000, 128_000, 256_000, 512_000,
        11_025, 22_050, 44_100, 88_200, 176_400, 352_800, 705_600,
    ];

    /// <summary>
    /// Reads a header from the front of an incoming packet.
    /// </summary>
    /// <param name="packet">The whole received datagram.</param>
    /// <param name="header">The parsed header on success, or default on failure.</param>
    /// <param name="rejectionReason">
    /// Why the packet was refused, or null on success. Worth keeping: a misbehaving sender
    /// on the same port is otherwise invisible, and the alternative is silence with no clue.
    /// </param>
    /// <returns>True when the packet carries native PCM audio we can decode.</returns>
    public static bool TryRead(
        ReadOnlySpan<byte> packet,
        out VbanPacketHeader header,
        out string? rejectionReason)
    {
        header = default;

        if (packet.Length < Size)
        {
            rejectionReason = $"Datagram is {packet.Length} bytes; the VBAN header alone occupies {Size}.";
            return false;
        }

        if (packet[0] != (byte)'V' || packet[1] != (byte)'B' || packet[2] != (byte)'A' || packet[3] != (byte)'N')
        {
            rejectionReason = "Missing the 'VBAN' four-character magic.";
            return false;
        }

        byte srAndProtocol = packet[4];
        var protocol = (VbanSubProtocol)(srAndProtocol & 0xE0);
        int srIndex = srAndProtocol & 0x1F;

        if (protocol != VbanSubProtocol.Audio)
        {
            rejectionReason = $"Sub-protocol 0x{(byte)protocol:X2} ({protocol}) is not the audio protocol.";
            return false;
        }

        if (srIndex >= SampleRateTable.Length)
        {
            rejectionReason = $"Sample rate index {srIndex} is undefined (valid range is 0 to {SampleRateTable.Length - 1}).";
            return false;
        }

        // Both counts travel as n-1, so zero means one. The specification is explicit.
        int sampleCount = packet[5] + 1;
        int channels = packet[6] + 1;

        byte formatBit = packet[7];
        var dataType = (VbanDataType)(formatBit & 0x07);
        var codec = (VbanCodec)(formatBit & 0xF0);

        if ((formatBit & 0x08) != 0)
        {
            rejectionReason = "Bit 3 of the format byte is set; the specification requires it to be zero.";
            return false;
        }

        if (codec != VbanCodec.Pcm)
        {
            rejectionReason = $"Codec 0x{(byte)codec:X2} ({codec}) is not native PCM. License terms for the VB-Audio codecs are not covered by the free protocol base.";
            return false;
        }

        string streamName = ReadStreamName(packet.Slice(8, 16));
        uint frameCounter = BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(24, 4));

        header = new VbanPacketHeader(
            protocol,
            SampleRateTable[srIndex],
            sampleCount,
            channels,
            dataType,
            codec,
            streamName,
            frameCounter);

        rejectionReason = null;
        return true;
    }

    /// <summary>
    /// Maps the VBAN data type onto the Core streamlined enum.
    /// </summary>
    /// <param name="dataType">Value from the packet.</param>
    /// <param name="sampleType">Matching Core type when one exists.</param>
    /// <returns>
    /// False for the 12-bit and 10-bit types. The specification lists them but never prints
    /// the <c>VBAN_SampleSize</c> array it uses to look up their storage width, and no other
    /// part of the document says how many bytes they occupy. Guessing would either read past
    /// the payload or desynchronise the stream, so those packets are refused and counted.
    /// </returns>
    public static bool TryGetSampleType(VbanDataType dataType, out PcmSampleType sampleType)
    {
        sampleType = dataType switch
        {
            VbanDataType.Byte8 => PcmSampleType.Unsigned8,
            VbanDataType.Int16 => PcmSampleType.Int16,
            VbanDataType.Int24 => PcmSampleType.Int24,
            VbanDataType.Int32 => PcmSampleType.Int32,
            VbanDataType.Float32 => PcmSampleType.Float32,
            VbanDataType.Float64 => PcmSampleType.Float64,
            _ => PcmSampleType.Unsigned8,
        };

        return dataType is not (VbanDataType.Bits12 or VbanDataType.Bits10);
    }

    /// <summary>
    /// Computes how many bytes this header's audio payload must occupy.
    /// </summary>
    /// <param name="header">A header that parsed successfully.</param>
    /// <param name="byteCount">Expected payload size.</param>
    /// <returns>False when the data type has no documented width.</returns>
    public static bool TryGetPayloadSize(in VbanPacketHeader header, out int byteCount)
    {
        if (!TryGetSampleType(header.DataType, out PcmSampleType sampleType))
        {
            byteCount = 0;
            return false;
        }

        byteCount = PcmConverter.BytesPerSample(sampleType) * header.Channels * header.SampleCount;
        return true;
    }

    private static string ReadStreamName(ReadOnlySpan<byte> field)
    {
        // Names are ASCII, NUL-padded, and not guaranteed to terminate exactly at 16.
        Span<char> chars = stackalloc char[16];
        int length = 0;

        for (int i = 0; i < field.Length; i++)
        {
            if (field[i] == 0)
            {
                break;
            }

            chars[length++] = (char)field[i];
        }

        return length == 0 ? string.Empty : new string(chars[..length]);
    }
}
