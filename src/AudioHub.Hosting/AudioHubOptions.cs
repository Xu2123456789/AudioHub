using AudioHub.Core;
using AudioHub.Core.Discovery;

namespace AudioHub.Hosting;

/// <summary>Settings for the audio graph.</summary>
/// <param name="SampleRate">Mixer output rate. 48000 matches most Bluetooth endpoints natively.</param>
/// <param name="Channels">Mixer output channel count.</param>
/// <param name="OutputDeviceId">Endpoint to render to. Null means the system default.</param>
/// <param name="BufferMilliseconds">
/// WASAPI buffer size. This is the main knob for Bluetooth latency: lower is faster but
/// clicks sooner when the 2.4 GHz band is busy.
/// </param>
/// <param name="CaptureLocalSound">
/// Whether to capture this machine's own playback. **Off by default, and that is a fix, not
/// a preference.** When the output endpoint is also the Windows default playback device — which
/// is the normal case, because the headset is where everything goes — the loopback tap captures
/// the mixer's own output and feeds it straight back in. The result is a comb filter (metallic
/// noise) with the level pumping as the layers reinforce and cancel. Measured on this machine:
/// with nothing playing locally, the local channel still read 0.033-0.073 RMS purely from the
/// network channel leaking around the loop. Turning this off is the cheapest way to break that
/// loop: the PC's own audio already reaches the headset through Windows, which mixes our stream
/// with everything else at the endpoint. The cost is that the local channel has no independent
/// gain or ducking. See <see cref="LoopbackDeviceId"/> for how to turn it back on safely.
/// </param>
/// <param name="LoopbackDeviceId">
/// Endpoint to tap for this machine's own playback. Null means "pick one that is not the output
/// endpoint": a loopback tap on the endpoint we render to is a feedback loop, so the host refuses
/// to choose it. Set this explicitly only when you want that loop anyway — the self test does,
/// because it is measuring whether the tap works at all, not whether the mix sounds good.
/// </param>
/// <param name="LocalJitterBufferMs">
/// Dejitter depth for the local channel. It has to be at least as deep as one delivery from the
/// loopback tap, which on this machine measured about 65 ms at a time into a buffer that was only
/// 20 ms deep — two thirds of the PC's own audio was being thrown away and replaced with silence.
/// 100 ms holds a whole delivery with room to spare.
/// </param>
/// <param name="LocalGainDb">Starting gain for the local channel.</param>
/// <param name="Transport">Which network transport to add. None keeps the app local-only.</param>
/// <param name="VbanPort">UDP port for VBAN, when that transport is selected. 6980 is the protocol default.</param>
/// <param name="VbanStreamName">
/// VBAN stream name to accept. Null accepts any name, which is easier before the sender is
/// configured but lets through whatever else is on that port.
/// </param>
/// <param name="NetworkSampleRate">
/// Sample rate the Android sender is expected to use. Must match what the phone actually
/// sends: VBAN carries no resampling agreement, and the mixer channel is created up front
/// with one fixed format.
/// </param>
/// <param name="NetworkJitterBufferMs">
/// Dejitter depth for the network channel. This is the knob that decides whether a bursty
/// arrival pattern survives: audio that arrives a hundred milliseconds late can only be
/// played smoothly by holding a hundred milliseconds of it. Raising it buys tolerance and
/// costs latency, one for one. The default is deliberately the same 80 ms the network
/// channel has always used — the right value depends on a measurement, not on a guess.
/// </param>
/// <param name="OutputCeilingDb">
/// Loudest output the mixer will produce, in dBFS. Zero means unprotected.
/// </param>
/// <param name="EnableDiscovery">
/// Whether to answer discovery probes on UDP 6979 so phones can find this hub without anybody
/// typing its address. See <see cref="DiscoveryProtocol"/> for why probes flow phone to hub.
/// </param>
/// <param name="AdvertisedSecurity">
/// What this hub tells senders it expects. Advertised rather than discovered by trial so a phone
/// can ask for a key before sending, instead of transmitting once in the clear to find out.
/// </param>
/// <remarks>
/// This exists because of what reaches the ear, not what reaches the network. Port 6980
/// accepts audio from anything that can send to it; nothing about encryption or discovery
/// changes that, because a sender playing very loud audio is not misbehaving. Left
/// unclamped, the loudest possible output is full scale — whatever the headset can do.
/// The default of <see cref="OutputLimiter.DefaultCeilingDb"/> puts a bound on the outcome
/// instead of trusting the input, and ordinary material never comes near it. See
/// <see cref="OutputLimiter"/> for why the limit is a curve rather than a cutoff.
/// </remarks>
public sealed record AudioHubOptions(
    int SampleRate = 48_000,
    int Channels = 2,
    string? OutputDeviceId = null,
    int BufferMilliseconds = 50,
    bool CaptureLocalSound = false,
    string? LoopbackDeviceId = null,
    int LocalJitterBufferMs = 100,
    float LocalGainDb = 0f,
    TransportKind Transport = TransportKind.None,
    int VbanPort = 6_980,
    string? VbanStreamName = null,
    int NetworkSampleRate = 48_000,
    int NetworkJitterBufferMs = 80,
    float OutputCeilingDb = OutputLimiter.DefaultCeilingDb,
    bool EnableDiscovery = true,
    SecurityMode AdvertisedSecurity = SecurityMode.None)
{
    /// <summary>Defaults.</summary>
    public static AudioHubOptions Default { get; } = new();

    /// <summary>Validates the settings.</summary>
    public void Validate()
    {
        if (SampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "Sample rate must be between 8 kHz and 384 kHz.");
        }

        if (Channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(Channels), Channels, "Only mono and stereo are supported.");
        }

        if (BufferMilliseconds is < 10 or > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(BufferMilliseconds), BufferMilliseconds, "Buffer must be between 10 ms and 500 ms.");
        }

        if (LocalGainDb is < -60f or > 20f)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalGainDb), LocalGainDb, "Gain must be between -60 dB and +20 dB.");
        }

        if (VbanPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(nameof(VbanPort), VbanPort, "VBAN port must be between 1 and 65535.");
        }

        if (NetworkSampleRate is < 8_000 or > 384_000)
        {
            throw new ArgumentOutOfRangeException(nameof(NetworkSampleRate), NetworkSampleRate, "Network sample rate must be between 8 kHz and 384 kHz.");
        }

        if (LocalJitterBufferMs is < 5 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(LocalJitterBufferMs), LocalJitterBufferMs, "Local jitter buffer must be between 5 ms and 2000 ms.");
        }

        if (NetworkJitterBufferMs is < 5 or > 2_000)
        {
            throw new ArgumentOutOfRangeException(nameof(NetworkJitterBufferMs), NetworkJitterBufferMs, "Network jitter buffer must be between 5 ms and 2000 ms.");
        }

        if (OutputCeilingDb is < OutputLimiter.MinCeilingDb or > 0f)
        {
            throw new ArgumentOutOfRangeException(
                nameof(OutputCeilingDb),
                OutputCeilingDb,
                $"Output ceiling must be between {OutputLimiter.MinCeilingDb} dB and 0 dB.");
        }
    }
}
