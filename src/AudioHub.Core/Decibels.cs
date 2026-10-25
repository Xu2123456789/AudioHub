namespace AudioHub.Core;

/// <summary>Conversions between decibel values and linear gain factors.</summary>
public static class Decibels
{
    /// <summary>Smallest linear gain AudioHub treats as meaningful. Anything below this is silence.</summary>
    public const float Floor = 1e-6f;

    /// <summary>Converts a decibel value to a linear gain factor.</summary>
    public static float ToLinear(float db) => db <= -120f ? 0f : MathF.Pow(10f, db / 20f);

    /// <summary>Converts a linear gain factor to decibels. Zero maps to -120 dB.</summary>
    public static float ToDb(float linear) => linear <= Floor ? -120f : 20f * MathF.Log10(linear);

    /// <summary>
    /// Per-sample smoothing coefficient that reaches roughly 63% of a step change
    /// after <paramref name="timeMs"/> milliseconds at the given sample rate.
    /// Used to glide gains instead of jumping them, which would click.
    /// </summary>
    /// <remarks>
    /// Only valid where the smoother runs once per sample. Code that updates once per
    /// render block must scale by block length instead, or the time constant comes out
    /// larger by a factor of the block size.
    /// </remarks>
    public static float SmoothingCoefficient(int timeMs, int sampleRate)
    {
        if (timeMs <= 0)
        {
            return 1f;
        }

        double tauSeconds = timeMs / 1000.0;
        return 1f - MathF.Exp(-1f / (float)(tauSeconds * sampleRate));
    }
}
