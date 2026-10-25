namespace AudioHub.Hosting;

/// <summary>
/// Which network transport should feed the Android channel, if any.
/// </summary>
/// <remarks>
/// Keeping the choice as an enum on the options record means adding another transport later
/// (the self-hosted Kotlin client, or an RTP receiver) is a new enum member plus one switch
/// arm in the composition root. Nothing else in the app needs to learn about it, which is
/// exactly what the architecture promises.
/// </remarks>
public enum TransportKind
{
    /// <summary>No network source. Local capture only, which is the safe default.</summary>
    None = 0,

    /// <summary>Listen for VBAN packets carrying the Android device's audio.</summary>
    Vban = 1,
}
