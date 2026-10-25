namespace AudioHub.Core;

/// <summary>
/// Presents an <see cref="ITransport"/> as an <see cref="IAudioSource"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the seam the plugin guide promises: transport implementations move bytes, this
/// adapter turns whatever arrives into a source the mixer can be fed from. Nothing above
/// this class learns whether the audio came over VBAN, RTP or anything else, which is what
/// lets the Android transport be replaced later without touching the mixer or the UIs.
/// </para>
/// <para>
/// Frames are owned by the transport, exactly as <see cref="ITransport.PacketReceived"/>
/// documents. This adapter forwards the very same frame and must never dispose it.
/// </para>
/// <para>
/// Likewise the transport itself is owned by whoever constructed it, normally the
/// composition root. Disposing this source stops it from listening but does <em>not</em>
/// dispose the transport.
/// </para>
/// </remarks>
public sealed class TransportAudioSource : IAudioSource
{
    private readonly ITransport _transport;
    private readonly SourceKind _kind;
    private readonly AudioFormat _format;

    private SourceState _state = SourceState.Idle;

    /// <summary>Wraps a transport as an audio source.</summary>
    /// <param name="transport">The transport to read from. Ownership stays with the caller.</param>
    /// <param name="format">
    /// The format this source reports. Transports must be configured to produce it; the
    /// adapter does not resample, the mixer does.
    /// </param>
    /// <param name="id">Stable source id. Defaults to the transport's own id.</param>
    /// <param name="displayName">Name shown in the UI. Defaults to the transport id.</param>
    /// <param name="kind">Where the sound comes from. Defaults to <see cref="SourceKind.Network"/>.</param>
    public TransportAudioSource(
        ITransport transport,
        AudioFormat? format = null,
        string? id = null,
        string? displayName = null,
        SourceKind kind = SourceKind.Network)
    {
        ArgumentNullException.ThrowIfNull(transport);

        _transport = transport;
        _kind = kind;
        _format = format ?? AudioFormat.Default;
        _format.Validate();

        Id = string.IsNullOrWhiteSpace(id) ? transport.Id : id;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? transport.Id : displayName;
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public SourceKind Kind => _kind;

    /// <inheritdoc />
    public AudioFormat Format => _format;

    /// <inheritdoc />
    public SourceState State => _state;

    /// <inheritdoc />
    public event EventHandler<AudioFrame>? FrameReceived;

    /// <inheritdoc />
    public event EventHandler<SourceStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_state is SourceState.Streaming or SourceState.Connecting or SourceState.Degraded)
        {
            return Task.CompletedTask;
        }

        SetState(SourceState.Connecting);

        try
        {
            _transport.PacketReceived += OnPacketReceived;
            _transport.StateChanged += OnTransportStateChanged;
        }
        catch (Exception ex)
        {
            SetState(SourceState.Faulted, ex.Message);
            throw;
        }

        return _transport.StartAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _transport.PacketReceived -= OnPacketReceived;
        _transport.StateChanged -= OnTransportStateChanged;

        try
        {
            await _transport.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (_state != SourceState.Idle)
            {
                SetState(SourceState.Stopped);
            }
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);

    private void OnPacketReceived(object? sender, AudioFrame frame) => FrameReceived?.Invoke(this, frame);

    private void OnTransportStateChanged(object? sender, TransportStateChangedEventArgs e)
    {
        // The transport connects and reconnects on its own; we only mirror that onto the
        // source lifecycle so the UI stays truthful.
        SetState(e.IsConnected ? SourceState.Streaming : SourceState.Connecting, e.Message);
    }

    private void SetState(SourceState next, string? message = null)
    {
        SourceState previous;
        lock (this)
        {
            previous = _state;
            if (previous == next)
            {
                return;
            }

            _state = next;
        }

        StateChanged?.Invoke(this, new SourceStateChangedEventArgs(Id, previous, next, message));
    }
}
