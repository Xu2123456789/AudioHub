namespace AudioHub.Core;

/// <summary>
/// Health of one channel's dejitter buffer, as of now.
/// </summary>
/// <remarks>
/// <para>
/// This exists because "the phone's audio crackles" was otherwise unanswerable from the
/// outside. A channel can sound broken for three completely different reasons, and these
/// counters tell them apart without anyone listening:
/// </para>
/// <list type="bullet">
/// <item><see cref="Underruns"/> and <see cref="SilentSamplesInserted"/> — the render clock
/// asked for audio and the buffer had none, so silence was invented. Heard as dropouts and
/// a level that keeps falling away, which is exactly what "音量一下大一下小" describes.</item>
/// <item><see cref="SamplesDroppedOnOverflow"/> — audio arrived faster than the buffer could
/// hold, so the oldest part was thrown away. Heard as a click at each splice.</item>
/// <item><see cref="BufferedMilliseconds"/> against <see cref="CapacityMilliseconds"/> — how
/// much headroom is left. A buffer that sits pinned at capacity is being fed in bursts.</item>
/// </list>
/// </remarks>
/// <param name="BufferedSamples">Interleaved floats held right now.</param>
/// <param name="CapacitySamples">Interleaved floats the buffer can hold.</param>
/// <param name="BufferedMilliseconds">Audio currently held, in milliseconds.</param>
/// <param name="CapacityMilliseconds">Audio the buffer can hold, in milliseconds.</param>
/// <param name="IsPrimed">Whether the buffer currently has enough audio to serve reads.</param>
/// <param name="Underruns">Reads that ran dry after the stream had started.</param>
/// <param name="SilentSamplesInserted">Samples invented as silence because of those underruns.</param>
/// <param name="SamplesDroppedOnOverflow">Samples discarded because incoming audio did not fit.</param>
public sealed record ChannelBufferStats(
    int BufferedSamples,
    int CapacitySamples,
    double BufferedMilliseconds,
    double CapacityMilliseconds,
    bool IsPrimed,
    long Underruns,
    long SilentSamplesInserted,
    long SamplesDroppedOnOverflow);
