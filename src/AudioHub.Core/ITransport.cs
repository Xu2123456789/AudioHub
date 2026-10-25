namespace AudioHub.Core;

/// <summary>
/// Carries audio over the network. This is the seam that lets the Android side be
/// swapped without touching the mixer or the UI.
/// </summary>
/// <remarks>
/// Milestone M2 ships an adapter over AudioRelay so the end-to-end path can be
/// validated early. The self-hosted client (Kotlin, AudioPlaybackCapture, Opus over
/// UDP) drops in behind this same interface later.
/// </remarks>
public interface ITransport : IAsyncDisposable
{
    /// <summary>Stable identifier of the transport.</summary>
    string Id { get; }

    /// <summary>Whether the transport is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>Raised for every decoded buffer. Handlers must not dispose the frame.</summary>
    event EventHandler<AudioFrame>? PacketReceived;

    /// <summary>Raised whenever <see cref="IsConnected"/> changes.</summary>
    event EventHandler<TransportStateChangedEventArgs>? StateChanged;

    /// <summary>Binds the socket or connects the stream.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Closes the connection.</summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>Carries a transport connectivity change.</summary>
public sealed class TransportStateChangedEventArgs : EventArgs
{
    /// <summary>Creates a new instance.</summary>
    public TransportStateChangedEventArgs(string transportId, bool isConnected, string? message = null)
    {
        TransportId = transportId;
        IsConnected = isConnected;
        Message = message;
    }

    /// <summary>Identifier of the transport that changed.</summary>
    public string TransportId { get; }

    /// <summary>Whether the transport is now connected.</summary>
    public bool IsConnected { get; }

    /// <summary>Optional diagnostic detail.</summary>
    public string? Message { get; }
}
