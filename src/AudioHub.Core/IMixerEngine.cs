namespace AudioHub.Core;

/// <summary>
/// Sums any number of input channels into one interleaved float PCM stream.
/// </summary>
/// <remarks>
/// <para>
/// The engine is pull-based: the render thread of an <see cref="IAudioSink"/> calls
/// <see cref="IPcmSource.Read"/> whenever the device wants more audio. Nothing runs on a timer,
/// so there is no extra buffering stage between the mixer and the device and the
/// output clock of the device drives the whole pipeline.
/// </para>
/// <para>
/// Channels may run at any sample rate and may be mono or stereo. The engine
/// resamples and up-mixes them to <see cref="IPcmSource.Format"/> internally.
/// </para>
/// </remarks>
public interface IMixerEngine : IPcmSource, IDisposable
{
    /// <summary>Whether the engine is accepting and mixing input.</summary>
    bool IsRunning { get; }

    /// <summary>Identifiers of every channel currently in the mix.</summary>
    IReadOnlyCollection<string> ChannelIds { get; }

    /// <summary>Registers a channel. Throws if <paramref name="channelId"/> already exists.</summary>
    void AddChannel(string channelId, AudioFormat format, MixerChannelOptions? options = null);

    /// <summary>Removes a channel and discards its buffered audio.</summary>
    void RemoveChannel(string channelId);

    /// <summary>
    /// Hands captured samples to a channel's dejitter buffer.
    /// Called from capture threads; must not block.
    /// </summary>
    void Submit(string channelId, ReadOnlySpan<float> samples);

    /// <summary>Current gain of a channel, in decibels.</summary>
    float GetGainDb(string channelId);

    /// <summary>Sets a channel's gain. Smoothed internally to avoid zipper noise.</summary>
    void SetGainDb(string channelId, float gainDb);

    /// <summary>Whether a channel is muted.</summary>
    bool GetMuted(string channelId);

    /// <summary>Mutes or unmutes a channel.</summary>
    void SetMuted(string channelId, bool muted);

    /// <summary>Smoothed RMS level of a channel, for meters in the UI.</summary>
    float GetLevel(string channelId);

    /// <summary>
    /// Dejitter-buffer health for a channel. Separates "the network starved us" from
    /// "audio arrived too fast to keep" without anyone having to listen.
    /// </summary>
    ChannelBufferStats GetBufferStats(string channelId);

    /// <summary>Adds or replaces a ducking rule by id.</summary>
    void AddDuckingRule(DuckingRule rule);

    /// <summary>Removes a ducking rule.</summary>
    void RemoveDuckingRule(string ruleId);

    /// <summary>Every registered ducking rule.</summary>
    IReadOnlyCollection<DuckingRule> DuckingRules { get; }

    /// <summary>
    /// Loudest output this mixer can produce, in dBFS. Zero means unprotected.
    /// </summary>
    /// <remarks>
    /// On the interface so that every front end can show people the limit that is actually in
    /// force, rather than leaving them to assume the output is safe because it usually is.
    /// A ceiling nobody can see is a ceiling nobody knows they have.
    /// </remarks>
    float OutputCeilingDb { get; }

    /// <summary>Starts mixing. Channels may be added before or after this call.</summary>
    void Start();

    /// <summary>Stops mixing and clears every channel buffer.</summary>
    void Stop();
}
