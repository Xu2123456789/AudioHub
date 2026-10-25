namespace AudioHub.Core;

/// <summary>
/// "When this channel starts making noise, turn those channels down."
/// This is what lets an incoming call or a navigation prompt be heard over music.
/// </summary>
/// <param name="Id">Stable identifier of the rule.</param>
/// <param name="TriggerChannelId">Channel whose activity starts the duck.</param>
/// <param name="TargetChannelIds">Channels that get turned down.</param>
/// <param name="DuckDb">How far to turn the targets down, in decibels. Negative.</param>
/// <param name="ThresholdRms">RMS level above which the trigger counts as active.</param>
/// <param name="AttackMs">Time to ramp from unity down to <paramref name="DuckDb"/>.</param>
/// <param name="ReleaseMs">Time to ramp back to unity once the trigger goes quiet.</param>
/// <param name="HoldMs">How long to stay ducked after the trigger goes quiet, to avoid pumping on pauses.</param>
public sealed record DuckingRule(
    string Id,
    string TriggerChannelId,
    IReadOnlyList<string> TargetChannelIds,
    float DuckDb = -12f,
    float ThresholdRms = 0.02f,
    int AttackMs = 60,
    int ReleaseMs = 400,
    int HoldMs = 500)
{
    /// <summary>Validates the rule, throwing when a value cannot be honoured.</summary>
    public void Validate()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Id);
        ArgumentException.ThrowIfNullOrWhiteSpace(TriggerChannelId);

        if (TargetChannelIds is null || TargetChannelIds.Count == 0)
        {
            throw new ArgumentException("A ducking rule needs at least one target channel.", nameof(TargetChannelIds));
        }

        if (DuckDb is > 0f or < -60f)
        {
            throw new ArgumentOutOfRangeException(nameof(DuckDb), DuckDb, "Duck amount must be between -60 dB and 0 dB.");
        }

        if (ThresholdRms is <= 0f or > 1f)
        {
            throw new ArgumentOutOfRangeException(nameof(ThresholdRms), ThresholdRms, "Threshold must be between 0 and 1.");
        }

        if (AttackMs < 0 || ReleaseMs < 0 || HoldMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(AttackMs), "Attack, release and hold must not be negative.");
        }
    }
}
