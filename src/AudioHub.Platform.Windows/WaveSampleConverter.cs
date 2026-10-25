using System.Runtime.InteropServices;
using NAudio.Wave;

namespace AudioHub.Platform.Windows;

/// <summary>
/// Converts between device PCM byte buffers and the interleaved float PCM AudioHub uses internally.
/// </summary>
/// <remarks>
/// Kept in one place because both directions are needed: capture hands back whatever
/// the device mix format happens to be, and render has to produce whatever the device
/// accepted when it was opened.
/// </remarks>
public static class WaveSampleConverter
{
    /// <summary>Decodes <paramref name="source"/> into <paramref name="destination"/> as floats.</summary>
    public static void ToFloat(ReadOnlySpan<byte> source, WaveFormat format, Span<float> destination)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0 || format.Channels <= 0)
        {
            destination.Clear();
            return;
        }

        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32:
                Copy(MemoryMarshal.Cast<byte, float>(source), destination, static v => v);
                return;

            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 64:
                Copy(MemoryMarshal.Cast<byte, double>(source), destination, static v => (float)v);
                return;

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 16:
                Copy(MemoryMarshal.Cast<byte, short>(source), destination, static v => v / 32768f);
                return;

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 32:
                Copy(MemoryMarshal.Cast<byte, int>(source), destination, static v => v / 2147483648f);
                return;

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 8:
                // 8 bit PCM is unsigned with silence at 128.
                Copy(source, destination, static v => (v - 128) / 128f);
                return;

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 24:
                Copy24(source, destination);
                return;

            default:
                destination.Clear();
                return;
        }
    }

    /// <summary>Encodes <paramref name="source"/> floats into <paramref name="destination"/> bytes.</summary>
    public static int FromFloat(ReadOnlySpan<float> source, WaveFormat format, Span<byte> destination)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample <= 0)
        {
            return 0;
        }

        switch (format.Encoding)
        {
            case WaveFormatEncoding.IeeeFloat when format.BitsPerSample == 32:
            {
                Span<float> target = MemoryMarshal.Cast<byte, float>(destination);
                int count = Math.Min(source.Length, target.Length);
                source[..count].CopyTo(target[..count]);
                target[count..].Clear();
                return count * 4;
            }

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 16:
            {
                Span<short> target = MemoryMarshal.Cast<byte, short>(destination);
                int count = Math.Min(source.Length, target.Length);
                for (int i = 0; i < count; i++)
                {
                    target[i] = (short)Math.Clamp(source[i] * 32767f, short.MinValue, short.MaxValue);
                }

                target[count..].Clear();
                return count * 2;
            }

            case WaveFormatEncoding.Pcm when format.BitsPerSample == 32:
            {
                Span<int> target = MemoryMarshal.Cast<byte, int>(destination);
                int count = Math.Min(source.Length, target.Length);
                for (int i = 0; i < count; i++)
                {
                    target[i] = (int)Math.Clamp(source[i] * 2147483647f, int.MinValue, int.MaxValue);
                }

                target[count..].Clear();
                return count * 4;
            }

            default:
                destination.Clear();
                return 0;
        }
    }

    private static void Copy<T>(ReadOnlySpan<T> source, Span<float> destination, Func<T, float> convert)
    {
        int count = Math.Min(source.Length, destination.Length);
        for (int i = 0; i < count; i++)
        {
            destination[i] = convert(source[i]);
        }

        if (count < destination.Length)
        {
            destination[count..].Clear();
        }
    }

    private static void Copy24(ReadOnlySpan<byte> source, Span<float> destination)
    {
        int available = source.Length / 3;
        int count = Math.Min(available, destination.Length);

        for (int i = 0; i < count; i++)
        {
            int raw = source[i * 3] | (source[(i * 3) + 1] << 8) | (source[(i * 3) + 2] << 16);
            if ((raw & 0x800000) != 0)
            {
                raw |= unchecked((int)0xFF000000);
            }

            destination[i] = raw / 8388608f;
        }

        if (count < destination.Length)
        {
            destination[count..].Clear();
        }
    }
}
