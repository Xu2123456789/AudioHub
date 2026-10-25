namespace AudioHub.Core;

/// <summary>
/// Consumes mixed PCM and renders it somewhere: a WASAPI endpoint, a Bluetooth
/// headset, a BlueZ A2DP source on Linux. Like <see cref="IAudioSource"/>, this is
/// an extension point — a new way to get sound out means one new implementation.
/// </summary>
/// <remarks>
/// A sink owns a render device and pulls from an <see cref="Core.IPcmSource"/> on that
/// device's own thread, so the device clock drives the whole pipeline. Implementations
/// must therefore do their work without blocking, allocating, or taking contended locks
/// inside the render callback.
/// </remarks>
public interface IAudioSink : IAsyncDisposable
{
    /// <summary>Stable identifier, unique within a running AudioHub instance.</summary>
    string Id { get; }

    /// <summary>Human readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Format the sink renders in. The mixer produces this format.</summary>
    AudioFormat Format { get; }

    /// <summary>Whether the sink is currently rendering.</summary>
    bool IsRunning { get; }

    /// <summary>Opens the device and begins rendering.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops rendering and closes the device.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
