using AudioHub.Core;
using Xunit;

namespace AudioHub.Tests;

/// <summary>
/// Tests the byte-to-float boundary every transport crosses.
/// </summary>
public sealed class PcmConverterTests
{
    [Fact]
    public void SixteenBitSilenceBecomesZero()
    {
        byte[] payload = [0, 0, 0, 0];

        float[] result = new float[2];
        PcmConverter.ToFloat(payload, PcmSampleType.Int16, result);

        Assert.All(result, sample => Assert.Equal(0f, sample, 5));
    }

    [Fact]
    public void SixteenBitFullScaleMapsToUnity()
    {
        // 32767 is the largest positive value; it lands just under 1.0 because we scale
        // by 32768 so that positive and negative peaks stay symmetric.
        byte[] payload = [0xFF, 0x7F, 0x00, 0x80];

        float[] result = new float[2];
        PcmConverter.ToFloat(payload, PcmSampleType.Int16, result);

        Assert.Equal(32767f / 32768f, result[0], 5);
        Assert.Equal(-1f, result[1], 5);
    }

    [Fact]
    public void SixteenBitHalfScaleMapsToHalf()
    {
        byte[] payload = [0x00, 0x40];

        float[] result = new float[1];
        PcmConverter.ToFloat(payload, PcmSampleType.Int16, result);

        Assert.Equal(0.5f, result[0], 5);
    }

    [Fact]
    public void TwentyFourBitNegativeValuesSignExtend()
    {
        // -1 in 24-bit two's complement is FF FF FF. If the sign bit is not extended the
        // value reads as a large positive number, which is exactly the audible bug.
        byte[] payload = [0xFF, 0xFF, 0xFF];

        float[] result = new float[1];
        PcmConverter.ToFloat(payload, PcmSampleType.Int24, result);

        Assert.Equal(-1f / 8388608f, result[0], 8);
    }

    [Fact]
    public void TwentyFourBitPositiveValuesAreUnaffected()
    {
        byte[] payload = [0x00, 0x00, 0x40]; // 4194304

        float[] result = new float[1];
        PcmConverter.ToFloat(payload, PcmSampleType.Int24, result);

        Assert.Equal(0.5f, result[0], 5);
    }

    [Fact]
    public void EightBitUnsignedCentresOnSilence()
    {
        byte[] payload = [128, 255, 0];

        float[] result = new float[3];
        PcmConverter.ToFloat(payload, PcmSampleType.Unsigned8, result);

        Assert.Equal(0f, result[0], 5);
        Assert.Equal(127f / 128f, result[1], 5);
        Assert.Equal(-1f, result[2], 5);
    }

    [Fact]
    public void Float32PassesThroughUnchanged()
    {
        byte[] payload = new byte[8];
        BitConverter.GetBytes(0.25f).CopyTo(payload, 0);
        BitConverter.GetBytes(-0.75f).CopyTo(payload, 4);

        float[] result = new float[2];
        PcmConverter.ToFloat(payload, PcmSampleType.Float32, result);

        Assert.Equal(0.25f, result[0], 6);
        Assert.Equal(-0.75f, result[1], 6);
    }

    [Fact]
    public void ByteCountsFollowTheWireTypes()
    {
        Assert.Equal(1, PcmConverter.BytesPerSample(PcmSampleType.Unsigned8));
        Assert.Equal(2, PcmConverter.BytesPerSample(PcmSampleType.Int16));
        Assert.Equal(3, PcmConverter.BytesPerSample(PcmSampleType.Int24));
        Assert.Equal(4, PcmConverter.BytesPerSample(PcmSampleType.Int32));
        Assert.Equal(4, PcmConverter.BytesPerSample(PcmSampleType.Float32));
        Assert.Equal(8, PcmConverter.BytesPerSample(PcmSampleType.Float64));
    }

    [Fact]
    public void PayloadThatDoesNotFillAnExactNumberOfSamplesIsRejected()
    {
        // Three bytes cannot hold two 16-bit samples, and quietly truncating would shift
        // every following packet by one channel, corrupting the stream rather than glitching it.
        byte[] payload = [0, 0, 0];

        Assert.Throws<ArgumentException>(() => PcmConverter.ToFloat(payload, PcmSampleType.Int16, new float[2]));
    }

    [Fact]
    public void Float64NarrowsToFloat32()
    {
        byte[] payload = BitConverter.GetBytes(0.5d);

        float[] result = new float[1];
        PcmConverter.ToFloat(payload, PcmSampleType.Float64, result);

        Assert.Equal(0.5f, result[0], 6);
    }
}
