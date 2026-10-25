using AudioHub.Engine;

namespace AudioHub.Tests;

public sealed class JitterBufferTests
{
    [Fact]
    public void ReturnsNothingUntilPrefilled()
    {
        JitterBuffer buffer = new(capacitySamples: 100, prefillSamples: 50);

        buffer.Write(new float[40]);

        float[] destination = new float[40];
        Assert.Equal(0, buffer.Read(destination));
        Assert.All(destination, sample => Assert.Equal(0f, sample));
    }

    [Fact]
    public void ReturnsWrittenSamplesInOrder()
    {
        JitterBuffer buffer = new(capacitySamples: 16, prefillSamples: 4);
        float[] source = Enumerable.Range(0, 8).Select(i => (float)i).ToArray();

        buffer.Write(source);

        float[] destination = new float[8];
        Assert.Equal(8, buffer.Read(destination));
        Assert.Equal(source, destination);
    }

    [Fact]
    public void UnderrunZeroPadsButStillAdvances()
    {
        JitterBuffer buffer = new(capacitySamples: 16, prefillSamples: 4);
        buffer.Write(new float[4]);

        float[] destination = new float[8];
        int read = buffer.Read(destination);

        Assert.Equal(4, read);
        Assert.Equal(0f, destination[5]);
        Assert.Equal(0f, destination[7]);
    }

    [Fact]
    public void OverflowDropsOldestAudio()
    {
        JitterBuffer buffer = new(capacitySamples: 8, prefillSamples: 2);
        float[] first = Enumerable.Repeat(1f, 8).ToArray();
        float[] second = Enumerable.Repeat(2f, 8).ToArray();

        buffer.Write(first);
        buffer.Write(second);

        float[] destination = new float[8];
        buffer.Read(destination);

        Assert.All(destination, sample => Assert.Equal(2f, sample));
    }

    [Fact]
    public void ClearRequiresPrimingAgain()
    {
        JitterBuffer buffer = new(capacitySamples: 16, prefillSamples: 4);
        buffer.Write(new float[8]);
        buffer.Clear();

        Assert.False(buffer.IsPrimed);
        Assert.Equal(0, buffer.Count);
        Assert.Equal(0, buffer.Read(new float[8]));
    }

    [Fact]
    public void OverflowCountsTheSamplesItThrewAway()
    {
        JitterBuffer buffer = new(capacitySamples: 8, prefillSamples: 2);

        buffer.Write(new float[8]);
        buffer.Write(new float[8]);

        Assert.Equal(8, buffer.SamplesDroppedOnOverflow);
    }

    [Fact]
    public void FillingUpForTheFirstTimeIsNotAnUnderrun()
    {
        JitterBuffer buffer = new(capacitySamples: 100, prefillSamples: 50);

        buffer.Write(new float[40]);

        Assert.Equal(0, buffer.Read(new float[40]));
        Assert.Equal(0, buffer.Underruns);
        Assert.Equal(0, buffer.SilentSamplesInserted);
    }

    [Fact]
    public void RunningDryMidStreamIsCounted()
    {
        JitterBuffer buffer = new(capacitySamples: 100, prefillSamples: 4);
        buffer.Write(new float[8]);

        Assert.Equal(8, buffer.Read(new float[8]));
        Assert.Equal(0, buffer.Read(new float[10]));

        Assert.Equal(1, buffer.Underruns);
        Assert.Equal(10, buffer.SilentSamplesInserted);
    }

    [Fact]
    public void FadeTurnsTheSeamAtAnUnderrunIntoASlope()
    {
        const float Full = 1f;
        JitterBuffer buffer = new(capacitySamples: 64, prefillSamples: 4, fadeSamples: 4);

        float[] ones = new float[8];
        Array.Fill(ones, Full);
        buffer.Write(ones);

        // First audio out of a fresh buffer passes through untouched.
        float[] opening = new float[4];
        Assert.Equal(4, buffer.Read(opening));
        Assert.All(opening, sample => Assert.Equal(Full, sample));

        // Four real samples left, then the buffer is dry. The delivered tail has to ramp
        // down so the join to the silence is a slope rather than a step.
        float[] draining = new float[8];
        Assert.Equal(4, buffer.Read(draining));

        Assert.Equal(0.75f, draining[0], 3);
        Assert.Equal(0.5f, draining[1], 3);
        Assert.Equal(0.25f, draining[2], 3);
        Assert.Equal(0f, draining[3]);
        Assert.All(draining[4..], sample => Assert.Equal(0f, sample));
        Assert.True(LargestStep(draining) <= 0.25f, $"step was {LargestStep(draining)}");

        // And the audio that resumes after the gap ramps back up.
        buffer.Write(ones);
        float[] resumed = new float[4];
        Assert.Equal(4, buffer.Read(resumed));

        Assert.Equal(0.25f, resumed[0], 3);
        Assert.Equal(Full, resumed[3]);
        Assert.True(LargestStep(resumed) <= 0.25f, $"step was {LargestStep(resumed)}");
    }

    [Fact]
    public void WithoutAFadeTheSeamIsAStep()
    {
        // The contrast that makes the ramp worth having: this is what the seam used to be.
        JitterBuffer buffer = new(capacitySamples: 64, prefillSamples: 4);

        float[] ones = new float[8];
        Array.Fill(ones, 1f);
        buffer.Write(ones);
        buffer.Read(new float[4]);

        float[] draining = new float[8];
        Assert.Equal(4, buffer.Read(draining));

        Assert.Equal(1f, LargestStep(draining));
    }

    private static float LargestStep(ReadOnlySpan<float> samples)
    {
        float largest = 0f;
        for (int i = 1; i < samples.Length; i++)
        {
            float step = Math.Abs(samples[i] - samples[i - 1]);
            if (step > largest)
            {
                largest = step;
            }
        }

        return largest;
    }
}
