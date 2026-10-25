using System.Diagnostics;
using System.Runtime.InteropServices;
using AudioHub.Core;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioHub.Platform.Windows;

/// <summary>
/// Measures how long audio sits inside a WASAPI render endpoint before it is played.
/// </summary>
/// <remarks>
/// <para>
/// The probe opens its <em>own</em> rendering client on the endpoint using the same buffer
/// size the app would use, writes silence, and tracks how far ahead of the play head it is.
/// It therefore characterises the endpoint rather than the mix currently running, and every
/// report carries a note saying exactly that.
/// </para>
/// <para>
/// Two independent estimates are produced. The main one subtracts the device's own play
/// position from the number of frames we have written; <c>AudioClockClient.GetPosition</c>
/// hands back both a position and the performance-counter reading it was taken at, so the
/// comparison stays accurate even though the two numbers were sampled at different moments.
/// The second simply asks the endpoint how many frames are still queued. They should agree,
/// and being able to see them disagree is worth the extra field.
/// </para>
/// <para>
/// <c>StreamLatency</c> is also reported, but only for reference: published measurements have
/// caught driver-reported values hundreds of milliseconds away from reality, so it is not the
/// number to quote. The report notes say so.
/// </para>
/// <para>
/// Shared mode hands every client the same mix format, usually 48 kHz stereo float with an
/// eight-byte block align. Code that assumes the sixteen-bit <c>blockAlign = 4</c> default
/// computes wrong buffer sizes without any error being raised, which is why the block align
/// is reported rather than assumed.
/// </para>
/// </remarks>
public sealed class WasapiLatencyProbe : ILatencyProbe
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(10);

    private readonly string? _deviceId;

    /// <summary>Creates a probe for one endpoint.</summary>
    /// <param name="deviceId">WASAPI endpoint id. Null means the default render endpoint.</param>
    public WasapiLatencyProbe(string? deviceId = null) => _deviceId = deviceId;

    /// <inheritdoc />
    public Task<LatencyReport> MeasureAsync(
        TimeSpan duration,
        int bufferMilliseconds,
        CancellationToken cancellationToken = default)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "Duration must be positive.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferMilliseconds);

        // Everything below calls into COM and blocks, so keep it off the caller's thread.
        return Task.Run(() => Measure(duration, bufferMilliseconds, cancellationToken), cancellationToken);
    }

    private LatencyReport Measure(TimeSpan duration, int bufferMilliseconds, CancellationToken cancellationToken)
    {
        using MMDevice target = ResolveEndpoint();

        AudioClient client = target.AudioClient;
        WaveFormat mixFormat = client.MixFormat;

        // Buffer sizes in WASAPI are counted in 100-nanosecond units.
        client.Initialize(
            AudioClientShareMode.Shared,
            AudioClientStreamFlags.None,
            bufferMilliseconds * 10_000L,
            0,
            mixFormat,
            Guid.Empty);

        try
        {
            int bufferFrames = client.BufferSize;
            double actualBufferMs = FramesToMilliseconds(bufferFrames, mixFormat.SampleRate);

            AudioRenderClient render = client.AudioRenderClient;
            AudioClockClient clock = client.AudioClockClient;
            byte[] silence = new byte[bufferFrames * mixFormat.BlockAlign];

            Fill(render, mixFormat, silence, client);
            long framesWritten = client.BufferSize - client.CurrentPadding;
            client.Start();

            var watch = Stopwatch.StartNew();
            double sumPaddingMs = 0;
            double maxPaddingMs = 0;
            double sumClockMs = 0;
            int samples = 0;
            int queueSamples = 0;
            long implausible = 0;
            long dry = 0;
            double frequency = clock.Frequency;

            // Only trust clock samples that land in a plausible window. If the endpoint is
            // starved its play position stops advancing, and subtracting a frozen position
            // from our written count would report ever-growing phantom latency.
            double plausibleCeilingMs = actualBufferMs * 2 + 20;

            while (watch.Elapsed < duration && !cancellationToken.IsCancellationRequested)
            {
                int available = client.BufferSize - client.CurrentPadding;
                if (available > 0)
                {
                    Fill(render, mixFormat, silence, client, available);
                    framesWritten += available;
                }

                double paddingMs = FramesToMilliseconds(client.CurrentPadding, mixFormat.SampleRate);
                if (client.CurrentPadding == 0)
                {
                    dry++;
                }

                {
                    sumPaddingMs += paddingMs;
                    queueSamples++;
                    if (paddingMs > maxPaddingMs)
                    {
                        maxPaddingMs = paddingMs;
                    }
                }

                if (frequency > 0 &&
                    clock.GetPosition(out ulong position, out ulong qpcPosition) &&
                    TryLatencyFromClock(framesWritten, position, qpcPosition, frequency, mixFormat.SampleRate, out double latencyMs) &&
                    latencyMs >= 0 &&
                    latencyMs <= plausibleCeilingMs)
                {
                    sumClockMs += latencyMs;
                    samples++;
                }
                else
                {
                    implausible++;
                }

                Thread.Sleep(SampleInterval);
            }

            client.Stop();

            return new LatencyReport(
                EndpointName: target.FriendlyName,
                RequestedBufferMilliseconds: bufferMilliseconds,
                MixFormatSampleRate: mixFormat.SampleRate,
                MixFormatChannels: mixFormat.Channels,
                MixFormatBitsPerSample: mixFormat.BitsPerSample,
                MixFormatBlockAlign: mixFormat.BlockAlign,
                BufferSizeFrames: bufferFrames,
                ActualBufferMilliseconds: actualBufferMs,
                DeviceDefaultPeriodMilliseconds: HundredNanosecondsToMilliseconds(client.DefaultDevicePeriod),
                DeviceMinimumPeriodMilliseconds: HundredNanosecondsToMilliseconds(client.MinimumDevicePeriod),
                MeasuredQueuedMilliseconds: queueSamples == 0 ? 0 : sumPaddingMs / queueSamples,
                MaxMeasuredQueuedMilliseconds: maxPaddingMs,
                ClockBasedQueuedMilliseconds: samples == 0 ? 0 : sumClockMs / samples,
                ReportedStreamLatencyMilliseconds: HundredNanosecondsToMilliseconds(client.StreamLatency),
                SampleCount: queueSamples,
                ImplausibleSampleCount: implausible,
                DrySampleCount: dry,
                Notes:
                [
                    LatencyReport.StandardNotes.TwoEstimatesDisagree,
                    LatencyReport.StandardNotes.ReportedLatencyUnreliable,
                    LatencyReport.StandardNotes.ExcludesWirelessHop,
                    LatencyReport.StandardNotes.IndependentOfRunningGraph,
                ]);
        }
        finally
        {
            client.Dispose();
        }
    }

    private MMDevice ResolveEndpoint()
    {
        if (string.IsNullOrWhiteSpace(_deviceId))
        {
            using var enumerator = new MMDeviceEnumerator();
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        }

        return AudioEndpointEnumerator.FindRenderDevice(_deviceId)
               ?? throw new InvalidOperationException($"Render endpoint '{_deviceId}' is not present or not active.");
    }

    private static void Fill(AudioRenderClient render, WaveFormat format, byte[] silence, AudioClient client, int? frames = null)
    {
        int count = frames ?? client.BufferSize - client.CurrentPadding;
        if (count <= 0)
        {
            return;
        }

        IntPtr pointer = render.GetBuffer(count);
        Marshal.Copy(silence, 0, pointer, count * format.BlockAlign);
        render.ReleaseBuffer(count, AudioClientBufferFlags.None);
    }

    /// <summary>
    /// Turns written-frame and play-position numbers into a latency in milliseconds.
    /// </summary>
    /// <remarks>
    /// The device reports its position in whatever units its own frequency counts in, and the
    /// frequency per second is exactly what converts it to seconds. Dividing by that frequency
    /// is therefore correct whether the position counts frames, 100-nanosecond ticks or
    /// anything else, which avoids having to guess the unit.
    /// </remarks>
    private static bool TryLatencyFromClock(
        long framesWritten,
        ulong position,
        ulong qpcPosition,
        double frequency,
        int sampleRate,
        out double latencyMilliseconds)
    {
        latencyMilliseconds = 0;

        if (sampleRate <= 0)
        {
            return false;
        }

        ulong now = (ulong)Stopwatch.GetTimestamp();
        double elapsedSeconds = now > qpcPosition
            ? (now - qpcPosition) / (double)Stopwatch.Frequency
            : 0;

        double playedSeconds = position / frequency + elapsedSeconds;
        double writtenSeconds = framesWritten / (double)sampleRate;

        latencyMilliseconds = (writtenSeconds - playedSeconds) * 1_000.0;
        return true;
    }

    private static double FramesToMilliseconds(int frames, int sampleRate) =>
        sampleRate <= 0 ? 0 : frames * 1_000.0 / sampleRate;

    private static double HundredNanosecondsToMilliseconds(long hundredNanoseconds) =>
        hundredNanoseconds / 10_000.0;
}
