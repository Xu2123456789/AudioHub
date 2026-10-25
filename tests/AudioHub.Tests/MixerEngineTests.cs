using AudioHub.Core;
using AudioHub.Engine;

namespace AudioHub.Tests;

public sealed class MixerEngineTests
{
    private const int SampleRate = 48_000;
    private const int Channels = 2;
    private const int BlockSamples = 480; // 5 ms

    private static readonly AudioFormat Format = new(SampleRate, Channels);

    /// <summary>
    /// These tests measure the mixer's own arithmetic — summing, gain, up-mixing, resampling,
    /// ducking — and each expected value is written as an absolute float. The output limiter
    /// sits after all of that and scales the result, so leaving it on would multiply every
    /// expectation here by 0.501 and bury what each test is actually about.
    /// Turning it off keeps each assertion pointed at the one behaviour it names. That the
    /// limiter is on by default is pinned separately, further down.
    /// </summary>
    private const float CeilingOff = 0f;

    [Fact]
    public void SingleChannelAtUnityGainPassesThrough()
    {
        using MixerEngine mixer = Create(out string channel);

        FeedConstant(mixer, channel, amplitude: 0.5f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0.5f);
    }

    [Fact]
    public void NegativeGainAttenuates()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("quiet", Format, new MixerChannelOptions(GainDb: -6f, JitterBufferMs: 200));
        mixer.Start();

        FeedConstant(mixer, "quiet", amplitude: 0.5f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0.5f * Decibels.ToLinear(-6f));
    }

    [Fact]
    public void MutedChannelSettlesToSilence()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("muted", Format, new MixerChannelOptions(Muted: true, JitterBufferMs: 2_000));
        mixer.Start();

        FeedConstant(mixer, "muted", amplitude: 0.5f, seconds: 2.0);
        float[] output = Settle(mixer, blocks: 200);

        AssertBlock(output, expected: 0f, tolerance: 0.001f);
    }

    [Fact]
    public void TwoChannelsSum()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("a", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.AddChannel("b", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        FeedConstant(mixer, "a", amplitude: 0.2f, seconds: 0.5);
        FeedConstant(mixer, "b", amplitude: 0.3f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0.5f);
    }

    [Fact]
    public void SummedChannelsAreClippedNotWrapped()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("a", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.AddChannel("b", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        FeedConstant(mixer, "a", amplitude: 0.9f, seconds: 0.5);
        FeedConstant(mixer, "b", amplitude: 0.9f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        Assert.All(output, sample => Assert.Equal(1f, sample));
    }

    [Fact]
    public void MonoChannelIsUpMixedToStereo()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("mono", new AudioFormat(SampleRate, 1), new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        float[] mono = new float[24_000];
        Array.Fill(mono, 0.25f);
        mixer.Submit("mono", mono);

        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0.25f);
    }

    [Fact]
    public void OffRateChannelIsResampledToTheOutputClock()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("cd", new AudioFormat(44_100, 2), new MixerChannelOptions(JitterBufferMs: 500));
        mixer.Start();

        float[] source = new float[44_100];
        Array.Fill(source, 0.4f);
        mixer.Submit("cd", source);

        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0.4f, tolerance: 0.02f);
    }

    [Fact]
    public void DuckingTurnsTargetsDownWhileTriggerIsLoud()
    {
        using MixerEngine mixer = new(Format, CeilingOff);

        // The trigger is loud but mixed almost to silence, so what remains in the output
        // is the target channel alone, making the duck amount directly measurable.
        mixer.AddChannel("call", Format, new MixerChannelOptions(GainDb: -60f, JitterBufferMs: 2_000));
        mixer.AddChannel("music", Format, new MixerChannelOptions(GainDb: 0f, JitterBufferMs: 2_000));
        mixer.AddDuckingRule(new DuckingRule(
            Id: "call-over-music",
            TriggerChannelId: "call",
            TargetChannelIds: ["music"],
            DuckDb: -12f,
            ThresholdRms: 0.02f,
            AttackMs: 20,
            ReleaseMs: 200,
            HoldMs: 1_000));
        mixer.Start();

        FeedConstant(mixer, "call", amplitude: 0.5f, seconds: 2.0);
        FeedConstant(mixer, "music", amplitude: 0.5f, seconds: 2.0);

        float[] output = Settle(mixer, blocks: 200);
        float expected = 0.5f * Decibels.ToLinear(-12f);

        AssertBlock(output, expected: expected, tolerance: 0.01f);
    }

    [Fact]
    public void AddingTheSameChannelTwiceIsRejected()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("a", Format);

        Assert.Throws<InvalidOperationException>(() => mixer.AddChannel("a", Format));
    }

    /// <summary>
    /// The default must be protected. This is the assertion that stops someone "tidying" the
    /// ceiling off later: every other test here builds the engine with the limiter disabled,
    /// so nothing else would notice if the default quietly became 0 dB.
    /// </summary>
    [Fact]
    public void AppliesTheDefaultCeiling()
    {
        using MixerEngine mixer = new(Format);
        mixer.AddChannel("quiet", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        FeedConstant(mixer, "quiet", amplitude: 0.25f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        Assert.Equal(OutputLimiter.DefaultCeilingDb, mixer.OutputCeilingDb);
        AssertBlock(output, expected: 0.25f * Decibels.ToLinear(OutputLimiter.DefaultCeilingDb));
    }

    /// <summary>
    /// The case this whole mechanism exists for: several senders at once, each individually
    /// reasonable, summing to something that would otherwise slam into full scale and straight
    /// into somebody's ears.
    /// </summary>
    [Fact]
    public void SummedLoudChannelsNeverReachFullScale()
    {
        using MixerEngine mixer = new(Format);
        mixer.Start();

        foreach (string id in new[] { "a", "b", "c", "d" })
        {
            mixer.AddChannel(id, Format, new MixerChannelOptions(JitterBufferMs: 200));
            FeedConstant(mixer, id, amplitude: 0.9f, seconds: 0.5);
        }

        float[] output = ReadBlock(mixer);
        float ceiling = Decibels.ToLinear(mixer.OutputCeilingDb);

        foreach (float sample in output)
        {
            Assert.True(
                MathF.Abs(sample) <= ceiling + float.Epsilon,
                $"Sample {sample} escaped the {ceiling} ceiling.");
        }

        // And it must still be loud enough to be worth listening to, not silence by accident.
        Assert.True(output.Any(s => MathF.Abs(s) > 0.4f), "Output came out near silence.");
    }

    /// <summary>
    /// The self test reads channels through <see cref="IMixerEngine.GetLevel"/>, and its PASS or
    /// FAIL turns on whether that level exceeds a threshold. The ceiling has to leave those meters
    /// alone: they report what arrived, not what was rendered. If limiting ever moved upstream it
    /// would quietly scale every self test reading down by half and nobody would notice the
    /// thresholds drifting.
    /// </summary>
    [Fact]
    public void CeilingDoesNotAffectChannelLevelMeters()
    {
        using MixerEngine limited = new(Format);
        using MixerEngine untouched = new(Format, CeilingOff);

        foreach (MixerEngine mixer in new[] { limited, untouched })
        {
            mixer.AddChannel("local", Format, new MixerChannelOptions(JitterBufferMs: 200));
            mixer.Start();
            FeedConstant(mixer, "local", amplitude: 0.2f, seconds: 0.5);
            ReadBlock(mixer);
        }

        Assert.Equal(untouched.GetLevel("local"), limited.GetLevel("local"), 6);
        Assert.True(limited.GetLevel("local") > 0.01f, "Level meter went quiet.");
    }

    [Fact]
    public void CustomCeilingIsHonoured()
    {
        using MixerEngine mixer = new(Format, -20f);
        mixer.AddChannel("quiet", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        FeedConstant(mixer, "quiet", amplitude: 5f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        float ceiling = Decibels.ToLinear(-20f);
        Assert.Equal(-20f, mixer.OutputCeilingDb);

        foreach (float sample in output)
        {
            Assert.True(MathF.Abs(sample) <= ceiling + float.Epsilon, $"Sample {sample} exceeded the ceiling.");
        }
    }

    [Fact]
    public void StoppedEngineOutputsSilence()
    {
        using MixerEngine mixer = Create(out string channel);
        mixer.Stop();

        FeedConstant(mixer, channel, amplitude: 0.5f, seconds: 0.5);
        float[] output = ReadBlock(mixer);

        AssertBlock(output, expected: 0f);
    }

    [Fact]
    public void BufferStatsReportCapacityInMilliseconds()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("net", Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();

        ChannelBufferStats stats = mixer.GetBufferStats("net");

        Assert.Equal(200d, stats.CapacityMilliseconds, 0.5d);
        Assert.Equal(0d, stats.BufferedMilliseconds, 0.5d);
        Assert.False(stats.IsPrimed);
        Assert.Equal(0, stats.Underruns);
        Assert.Equal(0, stats.SamplesDroppedOnOverflow);
    }

    [Fact]
    public void BufferStatsExposeStarvationAfterPlaybackHasStarted()
    {
        using MixerEngine mixer = new(Format, CeilingOff);
        mixer.AddChannel("net", Format, new MixerChannelOptions(JitterBufferMs: 20));
        mixer.Start();

        // A 20 ms buffer is primed by 5 ms of audio, so one submitted block is enough to
        // start it. The next read then has nothing left to hand over — which is exactly the
        // shape of the dropout the phone stream produced, and exactly what these counters
        // are there to make visible without anyone listening.
        float[] block = new float[BlockSamples];
        Array.Fill(block, 0.5f);
        mixer.Submit("net", block);

        Assert.Equal(0.5f, ReadBlock(mixer)[0], 3);
        ReadBlock(mixer);

        ChannelBufferStats stats = mixer.GetBufferStats("net");
        Assert.True(stats.Underruns >= 1, $"underruns was {stats.Underruns}");
        Assert.True(stats.SilentSamplesInserted > 0, "no silence was accounted for");
    }

    private static MixerEngine Create(out string channelId)
    {
        MixerEngine mixer = new(Format, CeilingOff);
        channelId = "local";
        mixer.AddChannel(channelId, Format, new MixerChannelOptions(JitterBufferMs: 200));
        mixer.Start();
        return mixer;
    }

    private static void FeedConstant(MixerEngine mixer, string channelId, float amplitude, double seconds)
    {
        float[] block = new float[BlockSamples];
        Array.Fill(block, amplitude);

        int blocks = (int)(seconds * SampleRate * Channels / BlockSamples);
        for (int i = 0; i < blocks; i++)
        {
            mixer.Submit(channelId, block);
        }
    }

    private static float[] ReadBlock(MixerEngine mixer)
    {
        float[] output = new float[BlockSamples];
        mixer.Read(output);
        return output;
    }

    private static float[] Settle(MixerEngine mixer, int blocks)
    {
        float[] output = new float[BlockSamples];
        for (int i = 0; i < blocks; i++)
        {
            mixer.Read(output);
        }

        return output;
    }

    private static void AssertBlock(float[] actual, float expected, float tolerance = 0.001f)
    {
        for (int i = 0; i < actual.Length; i++)
        {
            Assert.True(
                Math.Abs(actual[i] - expected) <= tolerance,
                $"Sample {i} was {actual[i]} but expected about {expected}.");
        }
    }
}
