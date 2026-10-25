namespace AudioHub.Core;

/// <summary>Per-channel mixer settings applied when a channel is added.</summary>
/// <param name="GainDb">Initial gain in decibels. 0 dB means unity.</param>
/// <param name="Muted">Whether the channel starts muted.</param>
/// <param name="JitterBufferMs">
/// Depth of the channel's dejitter buffer. Larger values survive more network or
/// radio jitter at the cost of added latency. Local sources can use a small value.
/// </param>
public sealed record MixerChannelOptions(float GainDb = 0f, bool Muted = false, int JitterBufferMs = 60)
{
    /// <summary>Defaults: unity gain, unmuted, 60 ms of dejitter.</summary>
    public static MixerChannelOptions Default { get; } = new();

    /// <summary>
    /// Settings suited to a source running on the same machine.
    /// </summary>
    /// <remarks>
    /// Deeper than it looks like it should be, because a WASAPI loopback tap does not deliver a
    /// steady trickle: it hands over everything the device produced since the last callback, which
    /// measured about 65 ms at a time on this machine. A buffer shallower than one delivery
    /// overflows on every callback and then runs dry waiting for the next one, so 20 ms here cost
    /// two thirds of the local audio. The buffer has to be at least as deep as one delivery.
    /// </remarks>
    public static MixerChannelOptions Local { get; } = new(JitterBufferMs: 100);

    /// <summary>Settings suited to a source arriving over Wi-Fi.</summary>
    public static MixerChannelOptions Network { get; } = new(JitterBufferMs: 80);

    /// <summary>Settings suited to a source arriving over Bluetooth.</summary>
    public static MixerChannelOptions Bluetooth { get; } = new(JitterBufferMs: 120);

    /// <summary>Validates the settings, throwing when a value cannot be honoured.</summary>
    public void Validate()
    {
        if (GainDb is < -60f or > 20f)
        {
            throw new ArgumentOutOfRangeException(nameof(GainDb), GainDb, "Gain must be between -60 dB and +20 dB.");
        }

        if (JitterBufferMs is < 5 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(JitterBufferMs), JitterBufferMs, "Jitter buffer must be between 5 ms and 2000 ms.");
        }
    }
}
