using System.Buffers.Binary;
using System.Text;
using AudioHub.Core;
using AudioHub.Sources.Vban;
using Xunit;

namespace AudioHub.Tests;

/// <summary>
/// Tests VBAN header parsing against VBAN Protocol Specification revision 13.
/// </summary>
/// <remarks>
/// The sample rate table, the zero-based field encoding and the bit masks are all quoted
/// from that document; these tests pin them so a future edit cannot silently drift.
/// </remarks>
public sealed class VbanPacketHeaderTests
{
    [Fact]
    public void AWellFormedStereoPacketParses()
    {
        byte[] packet = BuildPacket(
            srIndex: 3,
            channels: 2,
            dataType: VbanDataType.Float32,
            sampleCount: 256,
            streamName: "Stream1",
            frameCounter: 41,
            payloadLength: 2048);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out string? reason), reason);

        Assert.Equal(VbanSubProtocol.Audio, header.Protocol);
        Assert.Equal(48_000, header.SampleRate);
        Assert.Equal(256, header.SampleCount);
        Assert.Equal(2, header.Channels);
        Assert.Equal(VbanDataType.Float32, header.DataType);
        Assert.Equal(VbanCodec.Pcm, header.Codec);
        Assert.Equal("Stream1", header.StreamName);
        Assert.Equal(41u, header.FrameCounter);
    }

    [Theory]
    [InlineData(0, 6_000)]
    [InlineData(3, 48_000)]
    [InlineData(7, 8_000)]
    [InlineData(14, 11_025)]
    [InlineData(16, 44_100)]
    [InlineData(20, 705_600)]
    public void SampleRateIndexesMatchTheSpecificationTable(int index, int expectedHz)
    {
        byte[] packet = BuildPacket(srIndex: index, channels: 2, dataType: VbanDataType.Int16, sampleCount: 4, streamName: "x", frameCounter: 0, payloadLength: 16);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out string? reason), reason);
        Assert.Equal(expectedHz, header.SampleRate);
    }

    [Theory]
    [InlineData(21)]
    [InlineData(25)]
    [InlineData(31)]
    public void UndefinedSampleRateIndexesAreRejected(int index)
    {
        byte[] packet = BuildPacket(srIndex: index, channels: 2, dataType: VbanDataType.Int16, sampleCount: 4, streamName: "x", frameCounter: 0, payloadLength: 16);

        Assert.False(VbanPacketHeader.TryRead(packet, out _, out string? reason));
        Assert.Contains("undefined", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SampleAndChannelCountsAreStoredMinusOne()
    {
        byte[] packet = BuildPacket(3, channels: 1, VbanDataType.Int16, sampleCount: 1, payloadLength: 2);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out string? reason), reason);

        // The specification stores n-1, so a byte of zero means one of each.
        Assert.Equal(1, header.SampleCount);
        Assert.Equal(1, header.Channels);
    }

    [Fact]
    public void TwoHundredFiftySixSamplesAndTwoHundredFiftySixChannelsAreReachable()
    {
        byte[] packet = new byte[VbanPacketHeader.Size];
        WriteCommonHeader(packet, srIndex: 3, nbs: 255, nbc: 255, dataType: VbanDataType.Int16, reserved: false, codec: 0x00);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out _));

        Assert.Equal(256, header.SampleCount);
        Assert.Equal(256, header.Channels);
    }

    [Fact]
    public void MissingMagicIsRejected()
    {
        byte[] packet = BuildPacket(3, 2, VbanDataType.Int16, 4, payloadLength: 16);
        packet[0] = (byte)'X';

        Assert.False(VbanPacketHeader.TryRead(packet, out _, out string? reason));
        Assert.Contains("magic", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ShortDatagramsAreRejectedRatherThanReadPastTheEnd()
    {
        byte[] packet = BuildPacket(3, 2, VbanDataType.Int16, 4, payloadLength: 16);
        byte[] truncated = packet[..20];

        Assert.False(VbanPacketHeader.TryRead(truncated, out _, out string? reason));
        Assert.Contains("bytes", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x20, "Serial")]
    [InlineData(0x40, "Text")]
    [InlineData(0x60, "Service")]
    public void NonAudioSubProtocolsAreRejected(byte protocol, string expectedMention)
    {
        byte[] packet = new byte[VbanPacketHeader.Size];
        WriteCommonHeader(packet, srIndex: 3, nbs: 3, nbc: 1, dataType: VbanDataType.Int16, reserved: false, codec: 0x00);
        packet[4] = protocol;

        Assert.False(VbanPacketHeader.TryRead(packet, out _, out string? reason));
        Assert.Contains(expectedMention, reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ReservedFormatBitMustBeZero()
    {
        byte[] packet = new byte[VbanPacketHeader.Size];
        WriteCommonHeader(packet, srIndex: 3, nbs: 3, nbc: 1, dataType: VbanDataType.Int16, reserved: true, codec: 0x00);

        Assert.False(VbanPacketHeader.TryRead(packet, out _, out string? reason));
        Assert.Contains("zero", reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0x10, "Vbca")]
    [InlineData(0x20, "Vbcv")]
    [InlineData(0xF0, "User")]
    public void NonPcmCodecsAreRejected(byte codec, string expectedMention)
    {
        byte[] packet = new byte[VbanPacketHeader.Size];
        WriteCommonHeader(packet, srIndex: 3, nbs: 3, nbc: 1, dataType: VbanDataType.Int16, reserved: false, codec: codec);

        Assert.False(VbanPacketHeader.TryRead(packet, out _, out string? reason));
        Assert.Contains(expectedMention, reason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PayloadSizeFollowsFromTypeChannelsAndSamples()
    {
        VbanPacketHeader header = new(VbanSubProtocol.Audio, 48_000, 256, 2, VbanDataType.Float32, VbanCodec.Pcm, "x", 0);

        Assert.True(VbanPacketHeader.TryGetPayloadSize(header, out int size));
        Assert.Equal(4 * 2 * 256, size);
    }

    [Theory]
    [InlineData(VbanDataType.Bits12)]
    [InlineData(VbanDataType.Bits10)]
    public void DataTypesWithUndocumentedWidthAreRefusedRatherThanGuessed(VbanDataType dataType)
    {
        // The specification lists these types but never prints the VBAN_SampleSize array it
        // indexes for their width. Guessing would either overread or desynchronise, so we
        // refuse and surface it in the diagnostics instead.
        Assert.False(VbanPacketHeader.TryGetSampleType(dataType, out _));

        VbanPacketHeader header = new(VbanSubProtocol.Audio, 48_000, 8, 2, dataType, VbanCodec.Pcm, "x", 0);
        Assert.False(VbanPacketHeader.TryGetPayloadSize(header, out _));
    }

    [Theory]
    [InlineData(VbanDataType.Byte8, PcmSampleType.Unsigned8)]
    [InlineData(VbanDataType.Int16, PcmSampleType.Int16)]
    [InlineData(VbanDataType.Int24, PcmSampleType.Int24)]
    [InlineData(VbanDataType.Int32, PcmSampleType.Int32)]
    [InlineData(VbanDataType.Float32, PcmSampleType.Float32)]
    [InlineData(VbanDataType.Float64, PcmSampleType.Float64)]
    public void DocumentedTypesMapToCoreSampleTypes(VbanDataType source, PcmSampleType expected)
    {
        Assert.True(VbanPacketHeader.TryGetSampleType(source, out PcmSampleType actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StreamNamesAreTrimmedAtTheFirstNull()
    {
        byte[] packet = new byte[VbanPacketHeader.Size];
        WriteCommonHeader(packet, srIndex: 3, nbs: 3, nbc: 1, dataType: VbanDataType.Int16, reserved: false, codec: 0x00);
        Encoding.ASCII.GetBytes("Voicemeeter").CopyTo(packet, 8);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out _));
        Assert.Equal("Voicemeeter", header.StreamName);
    }

    [Fact]
    public void FrameCounterIsReadLittleEndian()
    {
        byte[] packet = BuildPacket(3, 2, VbanDataType.Int16, 4, frameCounter: 0x01020304, payloadLength: 16);

        Assert.True(VbanPacketHeader.TryRead(packet, out VbanPacketHeader header, out string? reason), reason);

        uint expected = BitConverter.ToUInt32([0x04, 0x03, 0x02, 0x01], 0);
        Assert.Equal(expected, header.FrameCounter);
    }

    private static byte[] BuildPacket(
        int srIndex,
        int channels,
        VbanDataType dataType,
        int sampleCount,
        string streamName = "AudioHub",
        uint frameCounter = 0,
        int payloadLength = 0)
    {
        byte[] packet = new byte[VbanPacketHeader.Size + payloadLength];
        WriteCommonHeader(packet, srIndex, sampleCount - 1, channels - 1, dataType, reserved: false, codec: 0x00);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(24, 4), frameCounter);

        if (!string.IsNullOrEmpty(streamName))
        {
            Encoding.ASCII.GetBytes(streamName).CopyTo(packet, 8);
        }

        return packet;
    }

    private static void WriteCommonHeader(
        byte[] packet,
        int srIndex,
        int nbs,
        int nbc,
        VbanDataType dataType,
        bool reserved,
        byte codec)
    {
        packet[0] = (byte)'V';
        packet[1] = (byte)'B';
        packet[2] = (byte)'A';
        packet[3] = (byte)'N';
        packet[4] = (byte)srIndex;
        packet[5] = (byte)nbs;
        packet[6] = (byte)nbc;
        packet[7] = (byte)((int)dataType | (reserved ? 0x08 : 0x00) | codec);
    }
}
