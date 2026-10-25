using AudioHub.Core;
using AudioHub.Platform.Windows;
using NAudio.Wave;

namespace AudioHub.Sinks.Wasapi;

/// <summary>
/// Bridges the pull model AudioHub uses internally onto NAudio's <see cref="IWaveProvider"/>.
/// </summary>
/// <remarks>
/// NAudio calls <see cref="Read"/> on the render thread; this class asks the mixer for
/// exactly that many samples and converts them into whatever format the device accepted.
/// No queue sits in between, so the device clock drives the mixer directly.
/// </remarks>
internal sealed class SourceWaveProvider : IWaveProvider
{
    private readonly IPcmSource _source;
    private float[] _scratch = Array.Empty<float>();

    public SourceWaveProvider(IPcmSource source, WaveFormat waveFormat)
    {
        _source = source;
        WaveFormat = waveFormat;
    }

    public WaveFormat WaveFormat { get; }

    public int Read(byte[] buffer, int offset, int count)
    {
        int bytesPerSample = WaveFormat.BitsPerSample / 8;
        if (bytesPerSample <= 0 || count <= 0)
        {
            return 0;
        }

        int samples = count / bytesPerSample;
        if (_scratch.Length < samples)
        {
            _scratch = new float[samples];
        }

        int read = _source.Read(_scratch.AsSpan(0, samples));
        int bytes = WaveSampleConverter.FromFloat(_scratch.AsSpan(0, read), WaveFormat, buffer.AsSpan(offset, count));

        if (bytes == 0)
        {
            // The device did not accept the format we offered; hand back silence rather
            // than crashing the render thread. WasapiSurface checks for this at startup.
            Array.Clear(buffer, offset, count);
            return count;
        }

        return bytes;
    }
}
