namespace AudioHub.Engine;

/// <summary>
/// Interpolating sample rate converter, used to bring a channel onto the mixer's clock.
/// </summary>
/// <remarks>
/// Linear interpolation is cheap, click-free and good enough for mixing sources whose
/// rates differ by a few percent (44.1 kHz content into a 48 kHz device, for instance).
/// It does roll off some high frequency content when downsampling. Swapping in a
/// polyphase or SoXR-quality resampler means replacing this class only; nothing else
/// in the engine knows how conversion happens.
/// </remarks>
public sealed class LinearResampler
{
    private readonly int _channels;
    private readonly double _ratio;
    private readonly float[] _held;
    private double _carry;
    private bool _hasHeld;

    /// <summary>Creates a converter from <paramref name="sourceRate"/> to <paramref name="destinationRate"/>.</summary>
    public LinearResampler(int sourceRate, int destinationRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(destinationRate);
        if (channels is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(channels), channels, "Only mono and stereo are supported.");
        }

        _channels = channels;
        _ratio = (double)sourceRate / destinationRate;
        _held = new float[channels];
    }

    /// <summary>Source sample rate divided by destination sample rate.</summary>
    public double Ratio => _ratio;

    /// <summary>
    /// Produces <c>destination.Length / channels</c> frames from <paramref name="source"/>.
    /// Any source samples not needed for this block are assumed consumed; callers size
    /// the input so that this holds, and the dejitter buffer absorbs the rounding.
    /// </summary>
    public void Process(ReadOnlySpan<float> source, Span<float> destination)
    {
        if (destination.IsEmpty)
        {
            return;
        }

        int sourceFrames = source.Length / _channels;
        int outFrames = destination.Length / _channels;
        double position = _carry;

        for (int outFrame = 0; outFrame < outFrames; outFrame++)
        {
            int index = (int)position;
            float fraction = (float)(position - index);

            for (int channel = 0; channel < _channels; channel++)
            {
                float a = index >= 0 && index < sourceFrames ? source[index * _channels + channel] : (_hasHeld ? _held[channel] : 0f);
                float b = index + 1 >= 0 && index + 1 < sourceFrames
                    ? source[(index + 1) * _channels + channel]
                    : a;

                destination[outFrame * _channels + channel] = a + (b - a) * fraction;
            }

            position += _ratio;
        }

        if (sourceFrames > 0)
        {
            for (int channel = 0; channel < _channels; channel++)
            {
                _held[channel] = source[(sourceFrames - 1) * _channels + channel];
            }

            _hasHeld = true;
        }

        _carry = position - sourceFrames;
        if (_carry < 0d)
        {
            _carry = 0d;
        }
    }

    /// <summary>Forgets interpolation state. Call when a stream restarts.</summary>
    public void Reset()
    {
        _carry = 0d;
        _hasHeld = false;
        Array.Clear(_held);
    }
}
