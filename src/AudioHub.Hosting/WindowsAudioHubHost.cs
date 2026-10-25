using AudioHub.Core;
using AudioHub.Core.Discovery;
using AudioHub.Core.Security;
using AudioHub.Engine;
using AudioHub.Platform.Windows;
using AudioHub.Sinks.Wasapi;
using AudioHub.Sources.Vban;
using AudioHub.Sources.WindowsLoopback;

namespace AudioHub.Hosting;

/// <summary>
/// Windows composition root: loopback capture into the mixer, mixer into a WASAPI endpoint.
/// </summary>
/// <remarks>
/// <para>
/// This class is the only place that knows which source and sink implementations are in
/// play. Adding a source later — the Android client, or Bluetooth/AirPlay for iPhone —
/// means adding one more registration here and nothing anywhere else.
/// </para>
/// <para>
/// A Linux host would implement <see cref="IAudioHubHost"/> the same way, pairing
/// bluez-alsa capture with a BlueZ sink. The front ends would not change at all.
/// </para>
/// </remarks>
public sealed class WindowsAudioHubHost : IAudioHubHost
{
    private readonly AudioHubOptions _options;
    private readonly MixerEngine _mixer;
    private readonly LoopbackSource? _localSource;
    private readonly TransportAudioSource? _networkSource;
    private readonly VbanTransport? _transport;
    private readonly DiscoveryListener? _discovery;
    private readonly ISecureEnvelope? _envelope;
    private readonly DefaultDeviceWatcher _defaultDeviceWatcher;
    private readonly object _gate = new();

    /// <summary>
    /// Serialises every transition of the output sink — start, stop, explicit device switch, and
    /// the reopen triggered by a default-device change — so a COM callback racing a UI click
    /// cannot stop/dispose the sink out from under another transition.
    /// </summary>
    private readonly SemaphoreSlim _sinkSwapGate = new(1, 1);

    private WasapiSink? _sink;
    private bool _running;
    private bool _disposed;
    private IReadOnlyList<AudioEndpointInfo> _outputDevices;
    private string? _outputDeviceId;
    private string? _localCaptureDeviceId;
    private string? _localCaptureSkippedReason;

    /// <summary>Creates the graph.</summary>
    public WindowsAudioHubHost(AudioHubOptions? options = null)
    {
        _options = options ?? AudioHubOptions.Default;
        _options.Validate();

        AudioFormat outputFormat = new(_options.SampleRate, _options.Channels);
        _mixer = new MixerEngine(outputFormat, _options.OutputCeilingDb);
        _outputDeviceId = _options.OutputDeviceId;
        _outputDevices = AudioEndpointEnumerator.RenderDevices();

        // Created before any channel exists and not dependent on them: a phone looking for this
        // hub should get an answer even when audio is misconfigured, because "found it, so the
        // network is fine, the problem must be further in" is a genuinely useful thing to be able
        // to establish from the phone side.
        Identity = HubIdentity.LoadOrCreate();

        // Optional by default rather than required: a phone that has not been given the key yet
        // must still be able to send, or turning this on would break the only setup that exists.
        // "Optional" is therefore a transition state, not a resting place — once phones are paired
        // the setting worth running is required.
        _envelope = _options.AdvertisedSecurity == SecurityMode.None
            ? null
            : new AesGcmEnvelope(HubKeyStore.DeriveKey(HubKeyStore.LoadOrCreatePsk(), Identity.HubId));

        if (_options.EnableDiscovery)
        {
            _discovery = new DiscoveryListener(
                Identity,
                _options.VbanPort,
                _options.AdvertisedSecurity);
        }

        if (_options.CaptureLocalSound)
        {
            (_localCaptureDeviceId, _localCaptureSkippedReason) = ResolveLoopbackDevice();

            if (_localCaptureDeviceId is not null)
            {
                _localSource = new LoopbackSource(_localCaptureDeviceId, id: "local", displayName: "This PC");
                _localSource.FrameReceived += OnLocalFrameReceived;
                _mixer.AddChannel(
                    _localSource.Id,
                    _localSource.Format,
                    new MixerChannelOptions(GainDb: _options.LocalGainDb, Muted: false, JitterBufferMs: _options.LocalJitterBufferMs));
            }
        }

        if (_options.Transport == TransportKind.Vban)
        {
            _transport = CreateTransport();
            _networkSource = new TransportAudioSource(
                _transport,
                format: NetworkFormat,
                id: "android",
                displayName: "Android (VBAN)",
                kind: SourceKind.Network);
            _networkSource.FrameReceived += OnNetworkFrameReceived;
            _mixer.AddChannel(
                _networkSource.Id,
                _networkSource.Format,
                MixerChannelOptions.Network with { JitterBufferMs = _options.NetworkJitterBufferMs });
        }

        // Follows the system default device for as long as the host is alive. It is cheap, so it
        // is created even when a specific device is pinned: the reopen handler checks for a pin
        // and ignores the notification in that case.
        _defaultDeviceWatcher = new DefaultDeviceWatcher();
        _defaultDeviceWatcher.DefaultRenderDeviceChanged += OnDefaultRenderDeviceChanged;
    }

    /// <summary>
    /// The format packets are expected to arrive in. VBAN has no negotiation, so this has to
    /// be stated up front; see <see cref="AudioHubOptions.NetworkSampleRate"/>.
    /// </summary>
    private AudioFormat NetworkFormat =>
        new(_options.NetworkSampleRate, _options.Channels);

    /// <summary>
    /// Builds the transport selected by the options.
    /// </summary>
    /// <remarks>
    /// One switch arm per kind. Adding a transport means adding an arm here and nowhere else,
    /// because everything above works in terms of <see cref="ITransport"/>.
    /// </remarks>
    private VbanTransport CreateTransport() =>
        new(
            port: _options.VbanPort,
            streamName: _options.VbanStreamName,
            expectedFormat: NetworkFormat,
            envelope: _envelope,
            requireEncryption: _options.AdvertisedSecurity == SecurityMode.Required);

    /// <summary>
    /// Diagnostics for the network source, or null when none is configured.
    /// </summary>
    public VbanTransportStats? TransportStats => _transport?.Stats;

    /// <summary>This hub's stable identity, which phones recognise it by across reconnects.</summary>
    public HubIdentity Identity { get; }

    /// <summary>
    /// The key a phone needs to send encrypted audio, in the grouped form someone types.
    /// Null when encryption is off.
    /// </summary>
    public string? PairingKey =>
        _options.AdvertisedSecurity == SecurityMode.None
            ? null
            : HubKeyStore.ToDisplayString(HubKeyStore.LoadOrCreatePsk());

    /// <summary>How many arriving packets have been decrypted, or null when encryption is off.</summary>
    public long? PacketsDecrypted => _transport?.PacketsDecrypted;

    /// <summary>What this hub demands of senders.</summary>
    public SecurityMode SecurityMode => _options.AdvertisedSecurity;

    /// <summary>
    /// Discovery diagnostics, or null when discovery is off. Answers probe replies even though
    /// nothing lights up when it silently does not work.
    /// </summary>
    public IDiscoveryTransport? Discovery => _discovery;

    /// <summary>Endpoint the local loopback tap is reading, or null when it is not running.</summary>
    public string? LocalCaptureDeviceId => _localCaptureDeviceId;

    /// <summary>Why the local tap is not running, or null when it is. Worth printing: it is silent otherwise.</summary>
    public string? LocalCaptureSkippedReason => _localCaptureSkippedReason;

    /// <summary>
    /// Picks which endpoint to tap for this machine's own playback.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A loopback tap on the endpoint we render to captures our own output and hands it straight
    /// back to the mixer. That is a feedback loop: every pass adds another copy at 60-100 ms of
    /// delay, which is a comb filter to the ear and a level that pumps as the copies reinforce and
    /// cancel. It is not a rare misconfiguration either — the headset is normally the Windows
    /// default playback device, so "the default device" and "our output device" are usually the
    /// same thing.
    /// </para>
    /// <para>
    /// So the automatic choice is the first endpoint that is not the one we render to, which on a
    /// laptop is the built-in speakers. An explicit <see cref="AudioHubOptions.LoopbackDeviceId"/>
    /// is honoured as given, because asking for it by id is how the self test gets the loop it
    /// deliberately wants to measure. When every endpoint is the output endpoint there is nothing
    /// safe to tap, and the reason is returned instead of an id.
    /// </para>
    /// </remarks>
    private (string? DeviceId, string? SkipReason) ResolveLoopbackDevice()
    {
        if (!string.IsNullOrWhiteSpace(_options.LoopbackDeviceId))
        {
            return (_options.LoopbackDeviceId, null);
        }

        string? outputId = _options.OutputDeviceId
                           ?? _outputDevices.FirstOrDefault(d => d.IsDefault)?.Id;

        // RenderDevices() puts the default first, so this prefers the default endpoint whenever
        // that is not the one being rendered to, and falls back to any other endpoint when it is.
        AudioEndpointInfo? candidate = _outputDevices.FirstOrDefault(d => d.Id != outputId);

        return candidate is null
            ? (null, "only one playback endpoint, and it is the one being rendered to")
            : (candidate.Id, null);
    }

    /// <inheritdoc />
    public IMixerEngine Mixer => _mixer;

    /// <inheritdoc />
    public bool IsRunning
    {
        get { lock (_gate) { return _running; } }
    }

    /// <inheritdoc />
    public IReadOnlyList<AudioEndpointInfo> OutputDevices
    {
        get { lock (_gate) { return _outputDevices; } }
    }

    /// <inheritdoc />
    public string? OutputDeviceId
    {
        get { lock (_gate) { return _outputDeviceId; } }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                return;
            }

            _running = true;
        }

        _mixer.Start();
        _discovery?.Start();

        if (_localSource is not null)
        {
            await _localSource.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_networkSource is not null)
        {
            await _networkSource.StartAsync(cancellationToken).ConfigureAwait(false);
        }

        await _sinkSwapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await OpenSinkAsync(_outputDeviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sinkSwapGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _running = false;
        }

        await _sinkSwapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sink is { } sink)
            {
                await sink.StopAsync(cancellationToken).ConfigureAwait(false);
                await sink.DisposeAsync().ConfigureAwait(false);
                _sink = null;
            }
        }
        finally
        {
            _sinkSwapGate.Release();
        }

        if (_localSource is not null)
        {
            await _localSource.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_networkSource is not null)
        {
            await _networkSource.StopAsync(cancellationToken).ConfigureAwait(false);
        }

        _mixer.Stop();
    }

    /// <inheritdoc />
    public async Task SetOutputDeviceAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        bool wasRunning;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _outputDeviceId = deviceId;
            wasRunning = _running;
        }

        if (!wasRunning)
        {
            return;
        }

        await _sinkSwapGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReplaceSinkAsync(deviceId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sinkSwapGate.Release();
        }
    }

    /// <inheritdoc />
    public void RefreshOutputDevices()
    {
        lock (_gate)
        {
            _outputDevices = AudioEndpointEnumerator.RenderDevices();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);

        if (_localSource is not null)
        {
            _localSource.FrameReceived -= OnLocalFrameReceived;
            await _localSource.DisposeAsync().ConfigureAwait(false);
        }

        // The source does not own the transport it wraps, so dispose them separately.
        if (_networkSource is not null)
        {
            _networkSource.FrameReceived -= OnNetworkFrameReceived;
            await _networkSource.DisposeAsync().ConfigureAwait(false);
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync().ConfigureAwait(false);
        }

        // Last rather than first: StopAsync waits briefly for the receive loop, and doing that
        // while nothing else is running makes the wait meaningful instead of racing the teardown.
        _discovery?.Dispose();

        _defaultDeviceWatcher.DefaultRenderDeviceChanged -= OnDefaultRenderDeviceChanged;
        _defaultDeviceWatcher.Dispose();

        _mixer.Dispose();

        lock (_gate)
        {
            _disposed = true;
        }
    }

    private void OnLocalFrameReceived(object? sender, AudioFrame frame)
    {
        // The loopback callback runs on a WASAPI thread with a hard deadline. Submitting
        // only copies into the channel's dejitter buffer, so it is safe to do here.
        if (_localSource is not null)
        {
            _mixer.Submit(_localSource.Id, frame.Samples);
        }
    }

    private void OnNetworkFrameReceived(object? sender, AudioFrame frame)
    {
        if (_networkSource is not null)
        {
            _mixer.Submit(_networkSource.Id, frame.Samples);
        }
    }

    private async Task OpenSinkAsync(string? deviceId, CancellationToken cancellationToken)
    {
        WasapiSink sink = new(
            _mixer,
            new AudioFormat(_options.SampleRate, _options.Channels),
            deviceId,
            _options.BufferMilliseconds,
            id: "output",
            displayName: "Output device");

        _sink = sink;
        await sink.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stops the current sink, if any, and opens a fresh one on <paramref name="deviceId"/>.
    /// Callers must hold <see cref="_sinkSwapGate"/>.
    /// </summary>
    private async Task ReplaceSinkAsync(string? deviceId, CancellationToken cancellationToken)
    {
        if (_sink is { } existing)
        {
            await existing.StopAsync(cancellationToken).ConfigureAwait(false);
            await existing.DisposeAsync().ConfigureAwait(false);
            _sink = null;
        }

        await OpenSinkAsync(deviceId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Handles a default-device change. The callback is on a COM thread, so this just fires an
    /// async reopen and returns immediately; the reopen is serialised with every other sink
    /// transition by <see cref="_sinkSwapGate"/>.
    /// </summary>
    private void OnDefaultRenderDeviceChanged(object? sender, EventArgs e)
        => _ = ReopenSinkAfterDefaultDeviceChangedAsync();

    private async Task ReopenSinkAfterDefaultDeviceChangedAsync()
    {
        await _sinkSwapGate.WaitAsync().ConfigureAwait(false);
        try
        {
            bool running;
            string? deviceId;
            lock (_gate)
            {
                running = _running;
                deviceId = _outputDeviceId;
            }

            // A pinned device (set via CLI) is the user's explicit choice, so the default moving
            // is not our concern. null means "follow the default", which is exactly what changed.
            // A null _sink while running means StartAsync is mid-flight and about to open the
            // current default anyway, so there is nothing to replace yet.
            if (!running || deviceId is not null || _sink is null)
            {
                return;
            }

            // A device transition can land in a brief window where the old endpoint is gone but
            // the new default is not openable yet. Retry a few times so the switch completes
            // without the user having to pause and resume. If it still fails the sink stays
            // stopped and the next default-device change (or a manual start/stop) retries it.
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    await ReplaceSinkAsync(deviceId, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                catch
                {
                    if (attempt == 3)
                    {
                        return;
                    }

                    await Task.Delay(150, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _sinkSwapGate.Release();
        }
    }
}
