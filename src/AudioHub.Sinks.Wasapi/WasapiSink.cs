using AudioHub.Core;
using AudioHub.Platform.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioHub.Sinks.Wasapi;

/// <summary>
/// Renders to a WASAPI endpoint. A Bluetooth headset is just another endpoint here,
/// which is why this sink needs to know nothing about Bluetooth.
/// </summary>
/// <remarks>
/// <para>
/// Bluetooth latency is largely set by the buffer WASAPI is opened with, so the buffer
/// size is a constructor argument rather than a constant. Start small and raise it only
/// if the link glitches — on a busy 2.4 GHz band, too small a buffer clicks.
/// </para>
/// <para>
/// The endpoint is opened in shared mode with float samples where the device accepts it,
/// falling back to 16-bit PCM otherwise. Shared mode matters: exclusive mode would block
/// every other application from making sound.
/// </para>
/// </remarks>
public sealed class WasapiSink : IAudioSink
{
    private readonly IPcmSource _source;
    private readonly AudioFormat _format;
    private readonly string? _deviceId;
    private readonly int _bufferMilliseconds;

    private IWavePlayer? _player;
    private SourceWaveProvider? _provider;

    /// <summary>Creates a sink rendering to the default endpoint, or a specific one.</summary>
    /// <param name="source">What to pull audio from, normally the mixer.</param>
    /// <param name="format">Format to render in. Defaults to 48 kHz stereo.</param>
    /// <param name="deviceId">WASAPI endpoint id. Null means the system default.</param>
    /// <param name="bufferMilliseconds">WASAPI buffer size. Smaller means lower latency.</param>
    /// <param name="id">Stable sink id.</param>
    /// <param name="displayName">Name shown in the UI.</param>
    public WasapiSink(
        IPcmSource source,
        AudioFormat? format = null,
        string? deviceId = null,
        int bufferMilliseconds = 50,
        string? id = null,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        _source = source;
        _format = format ?? AudioFormat.Default;
        _format.Validate();
        _deviceId = deviceId;
        _bufferMilliseconds = Math.Clamp(bufferMilliseconds, 10, 500);
        Id = id ?? "wasapi";
        DisplayName = displayName ?? "Windows audio device";
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public AudioFormat Format => _format;

    /// <inheritdoc />
    public bool IsRunning => _player?.PlaybackState == PlaybackState.Playing;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_player is not null)
        {
            return Task.CompletedTask;
        }

        MMDevice? device = AudioEndpointEnumerator.FindRenderDevice(_deviceId);

        // Prefer IEEE float: it avoids a quantisation round trip. Some endpoints only
        // take PCM, so fall back rather than failing to open at all.
        foreach (WaveFormat candidate in new[]
                 {
                     WaveFormat.CreateIeeeFloatWaveFormat(_format.SampleRate, _format.Channels),
                     new WaveFormat(_format.SampleRate, 16, _format.Channels),
                 })
        {
            try
            {
                SourceWaveProvider provider = new(_source, candidate);
                IWavePlayer player = device is null
                    ? new WasapiOut(AudioClientShareMode.Shared, true, _bufferMilliseconds)
                    : new WasapiOut(device, AudioClientShareMode.Shared, true, _bufferMilliseconds);

                player.Init(provider);
                player.Play();

                _provider = provider;
                _player = player;
                return Task.CompletedTask;
            }
            catch (Exception)
            {
                // Try the next candidate format.
            }
        }

        throw new InvalidOperationException("No supported WASAPI format could be opened on the selected endpoint.");
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        IWavePlayer? player = _player;
        if (player is null)
        {
            return Task.CompletedTask;
        }

        _player = null;
        _provider = null;

        try
        {
            player.Stop();
        }
        finally
        {
            player.Dispose();
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }
}
