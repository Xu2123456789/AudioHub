using AudioHub.Core;

namespace AudioHub.Engine;

/// <summary>
/// Default <see cref="IMixerEngine"/>: sums channels onto the output clock and
/// applies gain, muting and ducking.
/// </summary>
/// <remarks>
/// <para>
/// Work happens in three passes per <see cref="Read"/>: pull and convert every
/// channel, decide ducking from the levels just measured, then sum. Levels have to be
/// measured before ducking can be decided, and ducking has to be decided before
/// summing, hence the three passes rather than one.
/// </para>
/// <para>
/// Every gain change is smoothed rather than stepped. Jumping a gain inside a block
/// produces a discontinuity, which is heard as a click.
/// </para>
/// </remarks>
public sealed class MixerEngine : IMixerEngine
{
    private const int GainGlideMs = 30;
    private const int LevelMeterMs = 50;

    /// <summary>
    /// Ramp applied where a channel's audio starts or stops, in milliseconds. Short enough
    /// to be inaudible, long enough to turn a step into a slope. See <see cref="JitterBuffer"/>.
    /// </summary>
    private const int JitterFadeMs = 2;

    private readonly AudioFormat _outputFormat;
    private readonly OutputLimiter _outputLimiter;
    private readonly object _gate = new();
    private readonly Dictionary<string, Channel> _channels = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DuckingRule> _rules = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _ruleHoldUntilTicks = new(StringComparer.Ordinal);
    private bool _running;
    private bool _disposed;

    /// <summary>Creates a mixer producing audio in <paramref name="outputFormat"/>.</summary>
    /// <param name="outputFormat">Output format. Defaults to <see cref="AudioFormat.Default"/>.</param>
    /// <param name="outputCeilingDb">
    /// Loudest output this mixer will produce, in dBFS. Defaults to
    /// <see cref="OutputLimiter.DefaultCeilingDb"/> because the alternative is to let any
    /// sender on port 6980 drive headphones to full scale. Pass 0 only when the limit itself
    /// is what is under investigation — see <see cref="OutputLimiter"/> for why.
    /// </param>
    public MixerEngine(AudioFormat? outputFormat = null, float outputCeilingDb = OutputLimiter.DefaultCeilingDb)
    {
        _outputFormat = outputFormat ?? AudioFormat.Default;
        _outputFormat.Validate();
        _outputLimiter = new OutputLimiter(outputCeilingDb);
    }

    /// <inheritdoc />
    public AudioFormat Format => _outputFormat;

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<string> ChannelIds
    {
        get { lock (_gate) { return _channels.Keys.ToArray(); } }
    }

    /// <inheritdoc />
    public IReadOnlyCollection<DuckingRule> DuckingRules
    {
        get { lock (_gate) { return _rules.Values.ToArray(); } }
    }

    /// <inheritdoc />
    public float OutputCeilingDb => _outputLimiter.CeilingDb;

    /// <inheritdoc />
    public void AddChannel(string channelId, AudioFormat format, MixerChannelOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        format.Validate();

        MixerChannelOptions opts = options ?? MixerChannelOptions.Default;
        opts.Validate();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_channels.ContainsKey(channelId))
            {
                throw new InvalidOperationException($"Channel '{channelId}' is already in the mix.");
            }

            int capacity = format.SampleCountFor(TimeSpan.FromMilliseconds(opts.JitterBufferMs));
            int fadeSamples = format.SampleCountFor(TimeSpan.FromMilliseconds(JitterFadeMs));
            LinearResampler? resampler = format.SampleRate == _outputFormat.SampleRate
                ? null
                : new LinearResampler(format.SampleRate, _outputFormat.SampleRate, format.Channels);

            _channels[channelId] = new Channel(
                channelId,
                format,
                new JitterBuffer(capacity, capacity / 4, fadeSamples),
                resampler)
            {
                GainDb = opts.GainDb,
                Muted = opts.Muted,
                CurrentGainLinear = Decibels.ToLinear(opts.GainDb),
            };
        }
    }

    /// <inheritdoc />
    public void RemoveChannel(string channelId)
    {
        lock (_gate)
        {
            _channels.Remove(channelId);
        }
    }

    /// <inheritdoc />
    public void Submit(string channelId, ReadOnlySpan<float> samples)
    {
        Channel? channel;
        lock (_gate)
        {
            _channels.TryGetValue(channelId, out channel);
        }

        channel?.Buffer.Write(samples);
    }

    /// <inheritdoc />
    public float GetGainDb(string channelId) => WithChannel(channelId, static c => c.GainDb);

    /// <inheritdoc />
    public void SetGainDb(string channelId, float gainDb)
    {
        if (gainDb is < -60f or > 20f)
        {
            throw new ArgumentOutOfRangeException(nameof(gainDb), gainDb, "Gain must be between -60 dB and +20 dB.");
        }

        lock (_gate)
        {
            GetRequired(channelId).GainDb = gainDb;
        }
    }

    /// <inheritdoc />
    public bool GetMuted(string channelId) => WithChannel(channelId, static c => c.Muted);

    /// <inheritdoc />
    public void SetMuted(string channelId, bool muted)
    {
        lock (_gate)
        {
            GetRequired(channelId).Muted = muted;
        }
    }

    /// <inheritdoc />
    public float GetLevel(string channelId) => WithChannel(channelId, static c => c.Level);

    /// <inheritdoc />
    public ChannelBufferStats GetBufferStats(string channelId)
    {
        lock (_gate)
        {
            Channel channel = GetRequired(channelId);
            JitterBuffer buffer = channel.Buffer;

            return new ChannelBufferStats(
                BufferedSamples: buffer.Count,
                CapacitySamples: buffer.Capacity,
                BufferedMilliseconds: Milliseconds(buffer.Count, channel.Format),
                CapacityMilliseconds: Milliseconds(buffer.Capacity, channel.Format),
                IsPrimed: buffer.IsPrimed,
                Underruns: buffer.Underruns,
                SilentSamplesInserted: buffer.SilentSamplesInserted,
                SamplesDroppedOnOverflow: buffer.SamplesDroppedOnOverflow);
        }
    }

    private static double Milliseconds(int samples, AudioFormat format) =>
        samples * 1000.0 / ((long)format.SampleRate * format.Channels);

    /// <inheritdoc />
    public void AddDuckingRule(DuckingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        rule.Validate();

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _rules[rule.Id] = rule;
        }
    }

    /// <inheritdoc />
    public void RemoveDuckingRule(string ruleId)
    {
        lock (_gate)
        {
            _rules.Remove(ruleId);
            _ruleHoldUntilTicks.Remove(ruleId);
        }
    }

    /// <inheritdoc />
    public int Read(Span<float> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int outChannels = _outputFormat.Channels;
        int frames = destination.Length / outChannels;
        destination.Clear();

        if (frames == 0)
        {
            return 0;
        }

        lock (_gate)
        {
            if (!_running || _channels.Count == 0)
            {
                return frames * outChannels;
            }

            foreach (Channel channel in _channels.Values)
            {
                Prepare(channel, frames);
            }

            UpdateDucking(frames);

            foreach (Channel channel in _channels.Values)
            {
                Mix(channel, destination, frames);
            }
        }

        // Two guarantees on the way out, and they are different guarantees.
        //
        // The limiter bends anything near the top and then scales by the ceiling; that is what
        // stops a loud sender reaching full scale, and doing it with a curve rather than a cutoff
        // keeps it from turning into harsh harmonics on the way down.
        //
        // The clamp afterwards should never fire: the limiter's output is already inside
        // +/-ceiling, which is inside +/-1. It stays as a backstop so that any future change to
        // the curve degrades into "slightly wrong limiting" rather than "numbers the endpoint was
        // never meant to see". Cheap insurance for an outcome nobody can hear coming.
        _outputLimiter.Process(destination);
        Clamp(destination);
        return frames * outChannels;
    }

    /// <inheritdoc />
    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _running = true;
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_gate)
        {
            _running = false;
            foreach (Channel channel in _channels.Values)
            {
                channel.Buffer.Clear();
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _running = false;
            _channels.Clear();
            _rules.Clear();
            _ruleHoldUntilTicks.Clear();
        }
    }

    private void Prepare(Channel channel, int frames)
    {
        int channelSamples = frames * channel.Format.Channels;
        int sourceSamples = channel.Resampler is null
            ? channelSamples
            : ((int)Math.Ceiling(frames * channel.Resampler.Ratio) + 2) * channel.Format.Channels;

        EnsureCapacity(ref channel.Input, sourceSamples);
        EnsureCapacity(ref channel.Output, channelSamples);

        channel.Buffer.Read(channel.Input.AsSpan(0, sourceSamples));

        if (channel.Resampler is null)
        {
            channel.Input.AsSpan(0, channelSamples).CopyTo(channel.Output.AsSpan(0, channelSamples));
        }
        else
        {
            channel.Resampler.Process(channel.Input.AsSpan(0, sourceSamples), channel.Output.AsSpan(0, channelSamples));
        }

        double sum = 0d;
        for (int i = 0; i < channelSamples; i++)
        {
            sum += channel.Output[i] * channel.Output[i];
        }

        float rms = MathF.Sqrt((float)(sum / channelSamples));
        channel.Level += (rms - channel.Level) * BlockCoefficient(LevelMeterMs, frames);
    }

    private void UpdateDucking(int frames)
    {
        long now = DateTime.UtcNow.Ticks;

        foreach (Channel channel in _channels.Values)
        {
            channel.DuckTargetLinear = 1f;
        }

        foreach (DuckingRule rule in _rules.Values)
        {
            if (!_channels.TryGetValue(rule.TriggerChannelId, out Channel? trigger))
            {
                continue;
            }

            bool active = trigger.Level >= rule.ThresholdRms;
            if (active)
            {
                _ruleHoldUntilTicks[rule.Id] = now + TimeSpan.FromMilliseconds(rule.HoldMs).Ticks;
            }

            bool ducking = active || (_ruleHoldUntilTicks.TryGetValue(rule.Id, out long until) && now < until);
            if (!ducking)
            {
                continue;
            }

            float ducked = Decibels.ToLinear(rule.DuckDb);
            foreach (string targetId in rule.TargetChannelIds)
            {
                if (!_channels.TryGetValue(targetId, out Channel? target))
                {
                    continue;
                }

                target.DuckTargetLinear = MathF.Min(target.DuckTargetLinear, ducked);
                target.DuckAttackMs = Math.Max(rule.AttackMs, 1);
                target.DuckReleaseMs = Math.Max(rule.ReleaseMs, 1);
            }
        }

        foreach (Channel channel in _channels.Values)
        {
            int timeMs = channel.DuckTargetLinear < channel.DuckLinear
                ? channel.DuckAttackMs
                : channel.DuckReleaseMs;

            channel.DuckLinear += (channel.DuckTargetLinear - channel.DuckLinear) * BlockCoefficient(timeMs, frames);
        }
    }

    private void Mix(Channel channel, Span<float> destination, int frames)
    {
        int outChannels = _outputFormat.Channels;
        float target = channel.Muted ? 0f : Decibels.ToLinear(channel.GainDb) * channel.DuckLinear;
        float glide = Decibels.SmoothingCoefficient(GainGlideMs, _outputFormat.SampleRate);

        for (int frame = 0; frame < frames; frame++)
        {
            channel.CurrentGainLinear += (target - channel.CurrentGainLinear) * glide;
            float gain = channel.CurrentGainLinear;

            float left = channel.Output[frame * channel.Format.Channels];
            float right = channel.Format.Channels == 2 ? channel.Output[(frame * 2) + 1] : left;

            if (outChannels == 1)
            {
                destination[frame] += (left + right) * 0.5f * gain;
            }
            else
            {
                destination[frame * 2] += left * gain;
                destination[(frame * 2) + 1] += right * gain;
            }
        }
    }

    /// <summary>
    /// One-pole smoothing coefficient for a value updated once per block of
    /// <paramref name="frames"/> samples, reaching about 63% of a step after
    /// <paramref name="timeMs"/> milliseconds.
    /// </summary>
    /// <remarks>
    /// Level and duck state are updated once per render block, not per sample, so they
    /// need a coefficient scaled by block length. Using a per-sample coefficient here
    /// stretches the time constant by the block size, which is wrong by orders of magnitude.
    /// Gain in <see cref="Mix"/> is applied per sample and uses the per-sample form.
    /// </remarks>
    private float BlockCoefficient(int timeMs, int frames)
    {
        if (timeMs <= 0 || frames <= 0)
        {
            return 1f;
        }

        double denominator = (timeMs / 1000.0) * _outputFormat.SampleRate;
        return 1f - MathF.Exp(-(float)(frames / denominator));
    }

    private static void Clamp(Span<float> samples)
    {
        for (int i = 0; i < samples.Length; i++)
        {
            if (samples[i] > 1f)
            {
                samples[i] = 1f;
            }
            else if (samples[i] < -1f)
            {
                samples[i] = -1f;
            }
        }
    }

    private static void EnsureCapacity(ref float[] array, int needed)
    {
        if (array.Length < needed)
        {
            array = new float[needed];
        }
    }

    private T WithChannel<T>(string channelId, Func<Channel, T> selector)
    {
        lock (_gate)
        {
            return selector(GetRequired(channelId));
        }
    }

    private Channel GetRequired(string channelId)
    {
        if (!_channels.TryGetValue(channelId, out Channel? channel))
        {
            throw new KeyNotFoundException($"No channel '{channelId}' in the mix.");
        }

        return channel;
    }

    private sealed class Channel
    {
        public Channel(string id, AudioFormat format, JitterBuffer buffer, LinearResampler? resampler)
        {
            Id = id;
            Format = format;
            Buffer = buffer;
            Resampler = resampler;
        }

        public string Id { get; }

        public AudioFormat Format { get; }

        public JitterBuffer Buffer { get; }

        public LinearResampler? Resampler { get; }

        public float[] Input = Array.Empty<float>();

        public float[] Output = Array.Empty<float>();

        public float GainDb { get; set; }

        public bool Muted { get; set; }

        public float CurrentGainLinear { get; set; } = 1f;

        public float Level { get; set; }

        public float DuckLinear { get; set; } = 1f;

        public float DuckTargetLinear { get; set; } = 1f;

        public int DuckAttackMs { get; set; } = 60;

        public int DuckReleaseMs { get; set; } = 400;
    }
}
