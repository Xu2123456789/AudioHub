namespace AudioHub.Core;

/// <summary>
/// What a single latency probe run observed on one output endpoint.
/// </summary>
/// <remarks>
/// <para>
/// The important split is between <see cref="MeasuredQueuedMilliseconds"/> and
/// <see cref="ReportedStreamLatencyMilliseconds"/>. The first is derived from how much
/// audio the endpoint reports as still unplayed; the second is whatever the driver hands
/// back when asked directly. Public measurements have found driver-reported numbers that
/// are several hundred milliseconds off the truth, so it is carried for completeness and
/// must not be quoted as the latency.
/// </para>
/// <para>
/// Everything here stops at the WASAPI endpoint. It says nothing about what happens after
/// the samples leave the machine, so for a Bluetooth headset the radio hop on the order of
/// 100-250 ms is on top of these numbers and cannot be measured in software at all.
/// </para>
/// </remarks>
public sealed record LatencyReport(
    string EndpointName,
    int RequestedBufferMilliseconds,
    int MixFormatSampleRate,
    int MixFormatChannels,
    int MixFormatBitsPerSample,
    int MixFormatBlockAlign,
    int BufferSizeFrames,
    double ActualBufferMilliseconds,
    double DeviceDefaultPeriodMilliseconds,
    double DeviceMinimumPeriodMilliseconds,
    double MeasuredQueuedMilliseconds,
    double MaxMeasuredQueuedMilliseconds,
    double ClockBasedQueuedMilliseconds,
    double ReportedStreamLatencyMilliseconds,
    int SampleCount,
    long ImplausibleSampleCount,
    long DrySampleCount,
    IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Converted to JSON. Kept next to the record so every consumer reports the same fields
    /// and none of them silently drops the caveats in <see cref="Notes"/>.
    /// </summary>
    /// <param name="report">The report to serialise.</param>
    /// <returns>Indented JSON.</returns>
    public static string ToJson(LatencyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return System.Text.Json.JsonSerializer.Serialize(report, JsonOptions);
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>Standard caveats every report should carry. Included rather than assumed.</summary>
    public static class StandardNotes
    {
        /// <summary>Why there are two independent estimates, and which one to trust.</summary>
        public const string TwoEstimatesDisagree =
            "MeasuredQueuedMilliseconds comes from how many frames the endpoint reports as still queued. ClockBasedQueuedMilliseconds is an independent estimate from the device's own play clock and did not track the requested buffer, so it is diagnostic rather than a latency figure.";

        /// <summary>The driver's own latency figure is not trustworthy.</summary>
        public const string ReportedLatencyUnreliable =
            "ReportedStreamLatencyMilliseconds comes straight from the driver and is known to be wrong by hundreds of milliseconds on some systems. Prefer MeasuredQueuedMilliseconds.";

        /// <summary>Nothing downstream of the endpoint is covered.</summary>
        public const string ExcludesWirelessHop =
            "These figures stop at the WASAPI endpoint. For a Bluetooth headset add roughly 100-250 ms of radio and codec latency, which no software probe can measure.";

        /// <summary>The probe is not attached to the running mix.</summary>
        public const string IndependentOfRunningGraph =
            "The probe opens its own rendering client on the same endpoint with the same buffer size. It characterises the endpoint, not the audio currently playing through AudioHub.";
    }
}
