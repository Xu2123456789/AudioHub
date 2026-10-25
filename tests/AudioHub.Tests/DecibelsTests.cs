using AudioHub.Core;

namespace AudioHub.Tests;

public sealed class DecibelsTests
{
    [Theory]
    [InlineData(0f, 1f)]
    [InlineData(-6f, 0.5011872f)]
    [InlineData(-20f, 0.1f)]
    [InlineData(6f, 1.9952623f)]
    public void ToLinear_MatchesKnownValues(float db, float expected)
    {
        Assert.Equal(expected, Decibels.ToLinear(db), 5);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0.1f, -20f)]
    public void ToDb_RoundTrips(float linear, float expected)
    {
        Assert.Equal(expected, Decibels.ToDb(linear), 3);
    }

    [Fact]
    public void ToDb_SilenceIsFloor()
    {
        Assert.Equal(-120f, Decibels.ToDb(0f));
    }

    [Fact]
    public void SmoothingCoefficient_ZeroTimeMeansInstant()
    {
        Assert.Equal(1f, Decibels.SmoothingCoefficient(0, 48_000));
    }

    [Fact]
    public void SmoothingCoefficient_IsBetweenZeroAndOne()
    {
        float coefficient = Decibels.SmoothingCoefficient(50, 48_000);

        Assert.True(coefficient is > 0f and < 1f, $"Expected a fraction, got {coefficient}.");
    }
}
