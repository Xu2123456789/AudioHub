using System.Buffers.Binary;

namespace AudioHub.Core;

/// <summary>
/// Converts byte-oriented PCM into the interleaved 32-bit float samples AudioHub carries
/// inside the process.
/// </summary>
/// <remarks>
/// <para>
/// Everything AudioHub uses internally is float in the nominal range -1.0 to +1.0, which
/// means every transport, capture device and file decoder has to cross this boundary once.
/// Doing it here keeps the conversion unit-tested once instead of once per transport.
/// </para>
/// <para>
/// Integer formats are scaled by their full-scale positive value rather than by
/// <c>maximum - 1</c>. That makes a full-scale negative sample slightly under 0 dBFS,
/// which is the conventional reading and keeps positive and negative peaks symmetric.
/// </para>
/// </remarks>
public static class PcmConverter
{
    private const float Scale8 = 1f / 128f;
    private const float Scale16 = 1f / 32768f;
    private const float Scale24 = 1f / 8388608f;
    private const float Scale32 = 1f / 2147483648f;

    /// <summary>Number of bytes one sample occupies for the given wire type.</summary>
    /// <param name="type">Wire representation.</param>
    /// <exception cref="ArgumentOutOfRangeException">The type is not one we can convert.</exception>
    public static int BytesPerSample(PcmSampleType type) => type switch
    {
        PcmSampleType.Unsigned8 => 1,
        PcmSampleType.Int16 => 2,
        PcmSampleType.Int24 => 3,
        PcmSampleType.Int32 => 4,
        PcmSampleType.Float32 => 4,
        PcmSampleType.Float64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported sample type."),
    };

    /// <summary>Number of complete samples held in <paramref name="byteCount"/> bytes.</summary>
    /// <param name="type">Wire representation.</param>
    /// <param name="byteCount">Payload size in bytes.</param>
    public static int SampleCount(PcmSampleType type, int byteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(byteCount);
        return byteCount / BytesPerSample(type);
    }

    /// <summary>Converts interleaved wire samples to interleaved floats.</summary>
    /// <param name="source">Bytes straight off the wire.</param>
    /// <param name="type">Wire representation of each sample in <paramref name="source"/>.</param>
    /// <param name="destination">
    /// Receives <c>source.Length / <see cref="BytesPerSample"/></c> floats. Must already be
    /// sized exactly; the converter never allocates.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The payload length does not match the destination length. A mismatched packet is a
    /// corrupt packet, and guessing would silently distort the audio, so it is rejected.
    /// </exception>
    public static void ToFloat(ReadOnlySpan<byte> source, PcmSampleType type, Span<float> destination)
    {
        int bytesPerSample = BytesPerSample(type);
        if (source.Length != destination.Length * bytesPerSample)
        {
            throw new ArgumentException(
                $"Payload of {source.Length} bytes does not hold exactly {destination.Length} samples of {bytesPerSample} byte(s).",
                nameof(source));
        }

        for (int i = 0, offset = 0; i < destination.Length; i++, offset += bytesPerSample)
        {
            destination[i] = type switch
            {
                PcmSampleType.Unsigned8 => (source[offset] - 128) * Scale8,
                PcmSampleType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(source.Slice(offset, 2)) * Scale16,
                PcmSampleType.Int24 => ReadInt24LittleEndian(source.Slice(offset, 3)) * Scale24,
                PcmSampleType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(source.Slice(offset, 4)) * Scale32,
                PcmSampleType.Float32 => BinaryPrimitives.ReadSingleLittleEndian(source.Slice(offset, 4)),
                PcmSampleType.Float64 => (float)BinaryPrimitives.ReadDoubleLittleEndian(source.Slice(offset, 8)),
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unsupported sample type."),
            };
        }
    }

    private static int ReadInt24LittleEndian(ReadOnlySpan<byte> source)
    {
        int value = source[0] | (source[1] << 8) | (source[2] << 16);

        // Two's complement: extend the 24th bit through the top byte.
        return (value & 0x00800000) != 0
            ? value | unchecked((int)0xFF000000)
            : value;
    }
}
