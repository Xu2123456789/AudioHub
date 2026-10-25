using AudioHub.Core;
using AudioHub.Platform.Windows;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace AudioHub.Sources.WindowsLoopback;

/// <summary>
/// Captures everything the machine is playing, using the WASAPI loopback endpoint.
/// </summary>
/// <remarks>
/// <para>
/// This is the "local" source: game audio, music, meeting audio, browser tabs. It costs
/// nothing in added latency because it taps the mix that is already going to the speakers
/// rather than re-entering the audio stack.
/// </para>
/// <para>
/// The capture runs at the device's own mix format, which is normally 48 kHz stereo float
/// but is not guaranteed to be. The source reports whatever it got and the mixer resamples,
/// so no assumption about the rate leaks out of this class.
/// </para>
/// </remarks>
public sealed class LoopbackSource : IAudioSource
{
    private readonly string? _deviceId;
    private WasapiLoopbackCapture? _capture;
    private AudioFormat _format = AudioFormat.Default;
    private SourceState _state = SourceState.Idle;

    /// <summary>Creates a source capturing the default render endpoint, or a specific one.</summary>
    /// <param name="deviceId">WASAPI endpoint id to capture. Null means the system default.</param>
    /// <param name="id">Stable source id. Defaults to a fixed value.</param>
    /// <param name="displayName">Name shown in the UI.</param>
    public LoopbackSource(string? deviceId = null, string? id = null, string? displayName = null)
    {
        _deviceId = deviceId;
        Id = id ?? "local";
        DisplayName = displayName ?? "This PC";
    }

    /// <inheritdoc />
    public string Id { get; }

    /// <inheritdoc />
    public string DisplayName { get; }

    /// <inheritdoc />
    public SourceKind Kind => SourceKind.Local;

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
        if (_state is SourceState.Streaming or SourceState.Connecting)
        {
            return Task.CompletedTask;
        }

        SetState(SourceState.Connecting);

        try
        {
            MMDevice? device = AudioEndpointEnumerator.FindRenderDevice(_deviceId);
            WasapiLoopbackCapture capture = device is null
                ? new WasapiLoopbackCapture()
                : new WasapiLoopbackCapture(device);

            capture.DataAvailable += OnDataAvailable;
            capture.RecordingStopped += OnRecordingStopped;

            _capture = capture;
            _format = new AudioFormat(capture.WaveFormat.SampleRate, capture.WaveFormat.Channels);
            capture.StartRecording();

            SetState(SourceState.Streaming);
            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            SetState(SourceState.Faulted, ex.Message);
            throw;
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        WasapiLoopbackCapture? capture = _capture;
        if (capture is null)
        {
            SetState(SourceState.Stopped);
            return Task.CompletedTask;
        }

        try
        {
            capture.StopRecording();
        }
        catch (Exception)
        {
            // Stopping an already-stopped capture is not interesting.
        }

        Teardown(capture);
        SetState(SourceState.Stopped);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        WasapiLoopbackCapture? capture = _capture;
        if (capture is null || e.BytesRecorded <= 0)
        {
            return;
        }

        WaveFormat waveFormat = capture.WaveFormat;
        int bytesPerSample = waveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return;
        }

        int frameCount = e.BytesRecorded / (bytesPerSample * waveFormat.Channels);
        if (frameCount <= 0)
        {
            return;
        }

        using AudioFrame frame = AudioFrame.RentFrames(_format, frameCount, DateTime.UtcNow.Ticks);
        WaveSampleConverter.ToFloat(e.Buffer.AsSpan(0, e.BytesRecorded), waveFormat, frame.WritableSamples);
        FrameReceived?.Invoke(this, frame);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (_capture is { } capture)
        {
            Teardown(capture);
        }

        if (e.Exception is null)
        {
            if (_state != SourceState.Stopped)
            {
                SetState(SourceState.Stopped);
            }
        }
        else
        {
            SetState(SourceState.Faulted, e.Exception.Message);
        }
    }

    private void Teardown(WasapiLoopbackCapture capture)
    {
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        capture.Dispose();

        if (ReferenceEquals(_capture, capture))
        {
            _capture = null;
        }
    }

    private void SetState(SourceState next, string? message = null)
    {
        SourceState previous = _state;
        if (previous == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new SourceStateChangedEventArgs(Id, previous, next, message));
    }
}
