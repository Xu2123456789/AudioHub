namespace AudioHub.Core;

/// <summary>
/// Caps the loudest sample that can ever leave the mixer, so a hostile or broken sender
/// cannot drive headphones to full scale.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Without it, anything above 1.0 is simply cut off at 1.0, and 1.0 is
/// full scale — the loudest the endpoint can go, straight into the listener's ears. Anything
/// that can send to UDP 6980 could put a full-amplitude tone there, and no amount of network
/// security stops that: the sender is behaving legally, it is just sending very loud audio.
/// This puts a ceiling on the outcome rather than trusting the input.
/// </para>
/// <para>
/// <b>How it works.</b> Two stages, deliberately kept separate because they answer two
/// different questions:
/// </para>
/// <list type="number">
/// <item>
/// A soft knee bends anything above <see cref="KneeStart"/> towards unit magnitude. This is
/// about <i>how it sounds</i> when it happens: a sudden flat cutoff generates high harmonics
/// that are harsh and can be genuinely painful, while a smooth curve rolls over instead.
/// </item>
/// <item>
/// Everything is then scaled by <see cref="CeilingLinear"/>. This is the number that actually
/// protects ears — it decides <i>how loud the loudest possible output is</i>.
/// </item>
/// </list>
/// <para>
/// The order matters. Bending first keeps ordinary programme material perfectly linear, because
/// it is decided against absolute amplitude, not against the ceiling. Scaling afterwards acts
/// as one clean output trim and leaves the mix's internal balance untouched.
/// </para>
/// <para>
/// <b>What it deliberately is not.</b> This is memoryless: no attack, no release, no look-ahead,
/// no state between calls. A real broadcast limiter would sound better under heavy reduction,
/// but every one of those features adds latency, needs tuning, and makes behaviour depend on
/// what came before. Here the guarantee matters more than the grace — the useful property is
/// that the output bound can be stated outright, and the cost only ever falls on material that
/// is already far past anything real traffic produces.
/// </para>
/// </remarks>
public sealed class OutputLimiter
{
    /// <summary>
    /// Default ceiling in dBFS. Full scale is never reachable: the loudest possible output is
    /// this. Six decibels leaves a little over half of full scale, which stays clearly audible
    /// while making "someone made it as loud as possible" a bounded event rather than a full
    /// drive into the ear.
    /// </summary>
    public const float DefaultCeilingDb = -6f;

    /// <summary>Most negative ceiling accepted. Below this the output would be near silence.</summary>
    public const float MinCeilingDb = -60f;

    /// <summary>
    /// Amplitude below which the curve is exactly linear, so ordinary material is passed through
    /// untouched instead of being coloured by a transfer function it never needed. Real VBAN
    /// traffic on this project measured a peak of about 0.28, comfortably below this.
    /// </summary>
    private const float KneeStart = 0.7f;

    private readonly float _ceiling;
    private readonly bool _bypass;

    /// <summary>Creates a limiter holding output at or below <paramref name="ceilingDb"/> dBFS.</summary>
    /// <param name="ceilingDb">
    /// Ceiling in dBFS, between <see cref="MinCeilingDb"/> and 0. Zero means the limiter does
    /// nothing at all, and that has to be literal: there is no point offering a switch for
    /// comparing against untreated audio if it still quietly reshapes the loud parts.
    /// </param>
    public OutputLimiter(float ceilingDb = DefaultCeilingDb)
    {
        if (ceilingDb is > 0f or < MinCeilingDb)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ceilingDb),
                ceilingDb,
                $"Ceiling must be between {MinCeilingDb} dB and 0 dB.");
        }

        CeilingDb = ceilingDb;
        _ceiling = Decibels.ToLinear(ceilingDb);
        _bypass = ceilingDb >= 0f;
    }

    /// <summary>The configured ceiling, in dBFS. Negative; 0 means no limiting.</summary>
    public float CeilingDb { get; }

    /// <summary>The ceiling as a linear factor. No output sample will exceed this magnitude.</summary>
    public float CeilingLinear => _ceiling;

    /// <summary>
    /// Applies the knee and the ceiling in place. Every resulting sample is within
    /// ±<see cref="CeilingLinear"/>, unless the ceiling is 0 dB, in which case nothing
    /// is touched.
    /// </summary>
    public void Process(Span<float> samples)
    {
        if (_bypass)
        {
            return;
        }

        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = Bend(samples[i]) * _ceiling;
        }
    }

    /// <summary>
    /// Maps any finite amplitude into (-1, 1), staying exactly linear below
    /// <see cref="KneeStart"/> and rolling smoothly over above it.
    /// </summary>
    /// <remarks>
    /// With t = <see cref="KneeStart"/> and u = |x|:
    /// <c>y = t + (1 - t) * tanh((u - t) / (1 - t))</c>
    /// This is chosen because it is continuous in value <i>and</i> in slope where the knee
    /// starts — matching slope is what stops the transition being heard as a kink — and it
    /// approaches 1 asymptotically, so it never overshoots.
    /// </remarks>
    private static float Bend(float x)
    {
        // A NaN would sail through every comparison below and reach the endpoint as NaN, which
        // can surface as full-scale garbage. Silence is the safe answer to a non-number.
        if (!float.IsFinite(x))
        {
            return 0f;
        }

        float magnitude = MathF.Abs(x);
        if (magnitude <= KneeStart)
        {
            return x;
        }

        float excess = (magnitude - KneeStart) / (1f - KneeStart);
        float bent = KneeStart + ((1f - KneeStart) * MathF.Tanh(excess));
        return MathF.Sign(x) * bent;
    }
}
