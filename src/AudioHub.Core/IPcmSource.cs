namespace AudioHub.Core;

/// <summary>
/// Something a render device can pull PCM from.
/// </summary>
/// <remarks>
/// AudioHub is pull-based end to end: the output device asks for a block, the sink
/// asks the mixer, the mixer asks its channels. The device clock therefore drives
/// everything, and no extra buffering stage is inserted anywhere. A push-based design
/// would need a queue with its own timing, which adds latency and a second clock.
/// </remarks>
public interface IPcmSource
{
    /// <summary>Format of the audio returned by <see cref="Read"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>
    /// Fills <paramref name="destination"/> with interleaved float PCM and returns the
    /// number of floats written. Called on the render thread; must not block or allocate.
    /// </summary>
    int Read(Span<float> destination);
}
