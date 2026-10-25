namespace AudioHub.Core;

/// <summary>
/// Measures how long audio waits inside the output endpoint before it is played.
/// </summary>
/// <remarks>
/// <para>
/// The interface lives in Core and knows nothing about Windows, even though the only
/// implementation today is a WASAPI one. Keeping it here means a future Linux host can
/// add a bluez-alsa probe and every front end keeps working unchanged.
/// </para>
/// <para>
/// Implementations open their own client rather than attaching to the running sink. That
/// is a real limitation, and reports are expected to say so via
/// <see cref="LatencyReport.StandardNotes.IndependentOfRunningGraph"/>.
/// </para>
/// </remarks>
public interface ILatencyProbe
{
    /// <summary>Opens the endpoint, feeds silence, and samples the resulting queue.</summary>
    /// <param name="duration">How long to keep sampling. Longer runtimes catch more jitter.</param>
    /// <param name="bufferMilliseconds">WASAPI buffer to open with, matching the knob the app exposes.</param>
    /// <param name="cancellationToken">Cancels the measurement.</param>
    /// <returns>Everything the run observed plus its caveats.</returns>
    Task<LatencyReport> MeasureAsync(
        TimeSpan duration,
        int bufferMilliseconds,
        CancellationToken cancellationToken = default);
}
