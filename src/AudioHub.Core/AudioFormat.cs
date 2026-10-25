namespace AudioHub.Core;

/// <summary>
/// Describes the layout of PCM audio exchanged inside AudioHub.
/// The in-process representation is always interleaved 32-bit IEEE float,
/// so a format only needs to carry the sample rate and the channel count.
/// </summary>
/// <param name="SampleRate">Samples per second per channel, e.g. 48000.</param>
/// <param name="Channels">1 for mono, 2 for stereo.</param>
public readonly record struct AudioFormat(int SampleRate, int Channels)
{
    /// <summary>Format used by the mixer unless a sink requests something else.</summary>
    public static AudioFormat Default { get; } = new(48_000, 2);

    /// <summary>Interleaved sample count needed to represent <paramref name="duration"/>.</summary>
    public int SampleCountFor(TimeSpan duration) =>
        (int)Math.Ceiling(duration.TotalSeconds * SampleRate) * Channels;

    /// <summary>Interleaved sample count needed to represent <paramref name="frameCount"/> PCM frames.</summary>
    public int SampleCountForFrames(int frameCount) => frameCount * Channels;

    /// <summary>Throws when the format carries values that downstream code cannot honour.</summary>
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
    }
}
