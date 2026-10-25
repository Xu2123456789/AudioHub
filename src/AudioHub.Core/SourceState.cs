namespace AudioHub.Core;

/// <summary>Lifecycle state of an <see cref="IAudioSource"/>.</summary>
public enum SourceState
{
    /// <summary>Created but never started.</summary>
    Idle,

    /// <summary>Acquiring the underlying device or transport.</summary>
    Connecting,

    /// <summary>Producing frames.</summary>
    Streaming,

    /// <summary>Producing frames, but the connection is degraded (e.g. resending after a dropout).</summary>
    Degraded,

    /// <summary>Stopped after running at least once.</summary>
    Stopped,

    /// <summary>Hit an unrecoverable error.</summary>
    Faulted,
}

/// <summary>How a source reaches AudioHub. Drives iconography and per-kind defaults in the UI.</summary>
public enum SourceKind
{
    /// <summary>Sound produced by the machine running AudioHub itself.</summary>
    Local,

    /// <summary>Sound arriving over IP (the Android client, or an adapter over a third-party stream).</summary>
    Network,

    /// <summary>Sound arriving over a Bluetooth A2DP sink.</summary>
    Bluetooth,

    /// <summary>Sound arriving over AirPlay (the planned iPhone path).</summary>
    AirPlay,

    /// <summary>Anything else.</summary>
    Unknown,
}
