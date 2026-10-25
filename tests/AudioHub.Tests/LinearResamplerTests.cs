using AudioHub.Engine;

namespace AudioHub.Tests;

public sealed class LinearResamplerTests
{
    [Fact]
    public void UnityRatioPassesSamplesThrough()
    {
        LinearResampler resampler = new(48_000, 48_000, channels: 1);
        float[] source = [0.1f, 0.2f, 0.3f, 0.4f];
        float[] destination = new float[4];

        resampler.Process(source, destination);

        Assert.Equal(source, destination);
    }

    [Fact]
    public void ProducesExactlyTheRequestedFrameCount()
    {
        LinearResampler resampler = new(44_100, 48_000, channels: 2);
        float[] source = new float[4410 * 2];
        float[] destination = new float[4800 * 2];

        resampler.Process(source, destination);

        Assert.Equal(4800 * 2, destination.Length);
    }

    [Fact]
    public void ConstantSignalSurvivesConversion()
    {
        LinearResampler resampler = new(44_100, 48_000, channels: 1);
        float[] source = Enumerable.Repeat(0.5f, 4410).ToArray();
        float[] destination = new float[4800];

        resampler.Process(source, destination);

        // Interpolating a constant must give back that constant, whatever the ratio.
        Assert.All(destination, sample => Assert.Equal(0.5f, sample));
    }

    [Fact]
    public void DownsamplingStillFillsTheRequest()
    {
        LinearResampler resampler = new(96_000, 48_000, channels: 2);
        float[] source = new float[9600 * 2];
        float[] destination = new float[4800 * 2];

        resampler.Process(source, destination);

        Assert.Equal(4800 * 2, destination.Length);
    }
}
