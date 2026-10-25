using System.Buffers;

namespace AudioHub.Core;

/// <summary>
/// A buffer of interleaved 32-bit float PCM, rented from a shared pool.
/// </summary>
/// <remarks>
/// Audio callbacks run on real-time threads where the garbage collector must not
/// be provoked, so frames are pooled instead of allocated. The producer that rents
/// a frame owns it and must dispose it once every consumer is done with it.
/// </remarks>
public sealed class AudioFrame : IDisposable
{
    private readonly float[] _buffer;
    private bool _disposed;

    private AudioFrame(AudioFormat format, float[] buffer, int sampleCount, long timestampTicks)
    {
        Format = format;
        _buffer = buffer;
        SampleCount = sampleCount;
        TimestampTicks = timestampTicks;
    }

    /// <summary>Layout of the samples in this frame.</summary>
    public AudioFormat Format { get; }

    /// <summary>Number of interleaved float values actually in use.</summary>
    public int SampleCount { get; }

    /// <summary>Capture instant, in <see cref="DateTime.UtcNow"/> ticks.</summary>
    public long TimestampTicks { get; }

    /// <summary>PCM frames (one sample per channel) contained in this frame.</summary>
    public int FrameCount => SampleCount / Format.Channels;

    /// <summary>Playback duration of this frame.</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds((double)FrameCount / Format.SampleRate);

    /// <summary>Read-only view over the interleaved samples.</summary>
    public ReadOnlySpan<float> Samples => _buffer.AsSpan(0, SampleCount);

    /// <summary>Writable view over the interleaved samples, for producers.</summary>
    public Span<float> WritableSamples => _buffer.AsSpan(0, SampleCount);

    /// <summary>Rents a frame of exactly <paramref name="sampleCount"/> interleaved floats.</summary>
    public static AudioFrame Rent(AudioFormat format, int sampleCount, long timestampTicks = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        format.Validate();

        float[] buffer = ArrayPool<float>.Shared.Rent(sampleCount);
        return new AudioFrame(format, buffer, sampleCount, timestampTicks);
    }

    /// <summary>Rents a frame able to hold <paramref name="frameCount"/> PCM frames.</summary>
    public static AudioFrame RentFrames(AudioFormat format, int frameCount, long timestampTicks = 0) =>
        Rent(format, format.SampleCountForFrames(frameCount), timestampTicks);

    /// <summary>Returns the backing array to the pool.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ArrayPool<float>.Shared.Return(_buffer, clearArray: false);
    }
}
