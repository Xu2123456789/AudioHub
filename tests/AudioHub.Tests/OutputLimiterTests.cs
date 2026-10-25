using AudioHub.Core;

namespace AudioHub.Tests;

/// <summary>
/// Pins the output limiter, which is the only thing standing between port 6980 and the
/// listener's ears.
/// </summary>
/// <remarks>
/// These assertions exist because the safety property here is easy to lose quietly. A refactor
/// that reorders the two stages, or swaps the knee for a plain clamp, produces code that still
/// looks like a limiter and still passes every other test in the project — while letting a
/// sender reach full scale again. Each test below is aimed at one specific way that could happen.
/// </remarks>
public class OutputLimiterTests
{
    private const float DefaultCeiling = 0.5011872f; // 10^(-6/20)

    [Fact]
    public void DefaultCeiling_IsMinusSixDecibels()
    {
        OutputLimiter limiter = new();

        Assert.Equal(OutputLimiter.DefaultCeilingDb, limiter.CeilingDb);
        Assert.Equal(-6f, limiter.CeilingDb);
        Assert.Equal(DefaultCeiling, limiter.CeilingLinear, 6);
        Assert.Equal(-6f, Decibels.ToDb(limiter.CeilingLinear), 3);
    }

    /// <summary>
    /// The whole point. A sender putting ten times full scale down the wire is legal VBAN, and
    /// it is the case that hurts people. Whatever it sends, the output cannot exceed the ceiling
    /// by even one float step.
    /// </summary>
    [Theory]
    [InlineData(1f)]
    [InlineData(2f)]
    [InlineData(10f)]
    [InlineData(1_000f)]
    [InlineData(float.MaxValue)]
    public void AnythingLoud_StaysAtOrBelowTheCeiling(float amplitude)
    {
        OutputLimiter limiter = new();

        Span<float> samples = [amplitude, -amplitude];
        limiter.Process(samples);

        Assert.InRange(samples[0], 0f, limiter.CeilingLinear);
        Assert.InRange(samples[1], -limiter.CeilingLinear, 0f);
        Assert.True(
            MathF.Abs(samples[0]) <= limiter.CeilingLinear + float.Epsilon,
            $"{amplitude} produced {samples[0]}, above the {limiter.CeilingLinear} ceiling.");
    }

    /// <summary>
    /// Ordinary material must come out bit for bit, not merely "close". The knee starting at 0.7
    /// is what buys this: real VBAN traffic here peaked at 0.28, well under it, so the limiter
    /// should be a plain multiply for everything that has ever actually been heard.
    /// </summary>
    [Theory]
    [InlineData(0.28f)]     // peak measured from a real phone over Wi-Fi
    [InlineData(0.0f)]
    [InlineData(0.5f)]
    [InlineData(-0.28f)]
    [InlineData(0.7f)]      // exactly at the knee, still linear
    public void OrdinaryLevels_PassThroughLinearly(float input)
    {
        OutputLimiter limiter = new();

        Span<float> samples = [input];
        limiter.Process(samples);

        Assert.Equal(input * DefaultCeiling, samples[0], 6);
    }

    /// <summary>
    /// Louder in, louder out. Guards against a curve that folds back on itself, which would
    /// turn a loud passage into a quiet one and could make someone turn the volume up into
    /// something worse.
    /// </summary>
    [Fact]
    public void Output_IsMonotonicInInputMagnitude()
    {
        OutputLimiter limiter = new();

        float previous = 0f;
        for (float input = 0f; input <= 5f; input += 0.05f)
        {
            Span<float> samples = [input];
            limiter.Process(samples);

            Assert.True(
                samples[0] >= previous - float.Epsilon,
                $"Output fell from {previous} to {samples[0]} as input rose to {input}.");
            previous = samples[0];
        }
    }

    /// <summary>
    /// No step anywhere in the curve. A discontinuity is heard as a click, and a sharp one
    /// generates the same harsh harmonics this limiter exists to avoid. Swept finely so that a
    /// kink hidden between coarse sample points cannot survive.
    /// </summary>
    [Fact]
    public void Curve_HasNoDiscontinuityAcrossTheKnee()
    {
        OutputLimiter limiter = new();

        float previousInput = 0f;
        float previousOutput = 0f;
        for (float input = 0f; input <= 4f; input += 0.0005f)
        {
            Span<float> samples = [input];
            limiter.Process(samples);

            float inputStep = input - previousInput;
            float outputStep = samples[0] - previousOutput;

            // The slope below the knee is exactly the ceiling factor, and nowhere above it may
            // the slope exceed that: the knee can only ever bend downwards.
            Assert.True(
                outputStep <= (inputStep * DefaultCeiling) + 1e-6f,
                $"Slope jumped to {outputStep / inputStep} at input {input}.");
            Assert.True(outputStep >= -1e-6f, $"Output stepped backwards at input {input}.");

            previousInput = input;
            previousOutput = samples[0];
        }
    }

    /// <summary>
    /// A NaN reaching an audio endpoint is not silence — depending on the driver it can surface
    /// as full-scale garbage, which would defeat the entire purpose of having a ceiling.
    /// </summary>
    [Fact]
    public void NonFiniteInput_BecomesSilence()
    {
        OutputLimiter limiter = new();

        Span<float> samples = [float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.3f];
        limiter.Process(samples);

        Assert.Equal(0f, samples[0]);
        Assert.Equal(0f, samples[1]);
        Assert.Equal(0f, samples[2]);
        Assert.InRange(samples[3], 0f, limiter.CeilingLinear);
    }

    [Fact]
    public void ZeroCeiling_BehavesLikeNoCeiling()
    {
        OutputLimiter limiter = new(0f);

        Assert.Equal(1f, limiter.CeilingLinear, 6);

        Span<float> samples = [0.25f];
        limiter.Process(samples);

        Assert.Equal(0.25f, samples[0], 6);
    }

    /// <summary>
    /// Pins the meaning of "off". An earlier version scaled by 1 but still bent everything past
    /// the knee, so asking for no ceiling quietly still reshaped the loud parts. That is not a
    /// baseline anyone can compare against, and it showed up as a mixer test that expected an
    /// exact 1.0 after clipping and got 0.997 instead.
    /// </summary>
    [Fact]
    public void ZeroCeiling_LeavesLoudInputCompletelyUntouched()
    {
        OutputLimiter limiter = new(0f);

        Span<float> samples = [5f, -5f, 1.8f];
        limiter.Process(samples);

        Assert.Equal(5f, samples[0], 6);
        Assert.Equal(-5f, samples[1], 6);
        Assert.Equal(1.8f, samples[2], 6);
    }

    [Theory]
    [InlineData(0.1f)]
    [InlineData(-61f)]
    [InlineData(-200f)]
    public void OutOfRangeCeiling_IsRejected(float ceilingDb)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OutputLimiter(ceilingDb));
    }
}
