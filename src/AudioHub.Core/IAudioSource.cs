namespace AudioHub.Core;

/// <summary>
/// Produces PCM audio from somewhere: the local machine, the network, a Bluetooth
/// sink, AirPlay, a file. Implementations are the extension points of AudioHub —
/// adding a new way to get sound in means adding one <see cref="IAudioSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FrameReceived"/> is synchronous on purpose. Audio callbacks run on
/// threads with hard deadlines; awaiting a Task there would add scheduling jitter.
/// Handlers must therefore copy or forward the samples and return immediately,
/// and must not dispose the frame — the source owns it and disposes it after the
/// event returns.
/// </para>
/// <para>
/// Implementations must be safe to start and stop repeatedly, and must raise
/// <see cref="StateChanged"/> on every transition so the UI can stay in sync.
/// </para>
/// </remarks>
public interface IAudioSource : IAsyncDisposable
{
    /// <summary>Stable identifier, unique within a running AudioHub instance.</summary>
    string Id { get; }

    /// <summary>Human readable name shown in the UI.</summary>
    string DisplayName { get; }

    /// <summary>Where this source gets its audio from.</summary>
    SourceKind Kind { get; }

    /// <summary>Format of the frames this source produces.</summary>
    AudioFormat Format { get; }

    /// <summary>Current lifecycle state.</summary>
    SourceState State { get; }

    /// <summary>Raised for every captured buffer. See the remarks on this interface.</summary>
    event EventHandler<AudioFrame>? FrameReceived;

    /// <summary>Raised whenever <see cref="State"/> changes.</summary>
    event EventHandler<SourceStateChangedEventArgs>? StateChanged;

    /// <summary>Starts producing frames. Must be idempotent while already streaming.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops producing frames and releases the underlying device or transport.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}
