namespace AudioHub.Core;

/// <summary>
/// The wire representation of a single sample, independent of where those bytes came from.
/// </summary>
/// <remarks>
/// Network transports describe their payloads with their own enumerations (VBAN uses a
/// three-bit data type field, for example). Map those onto these values before handing
/// bytes to <see cref="PcmConverter"/>, so the conversion logic stays in one place and
/// every transport benefits from it.
/// </remarks>
public enum PcmSampleType
{
    /// <summary>8-bit unsigned integer. Silence sits at 128.</summary>
    Unsigned8 = 0,

    /// <summary>16-bit signed integer, little-endian.</summary>
    Int16 = 1,

    /// <summary>24-bit signed integer stored in three little-endian bytes.</summary>
    Int24 = 2,

    /// <summary>32-bit signed integer, little-endian.</summary>
    Int32 = 3,

    /// <summary>32-bit IEEE-754 single precision float.</summary>
    Float32 = 4,

    /// <summary>64-bit IEEE-754 double precision float.</summary>
    Float64 = 5,
}
