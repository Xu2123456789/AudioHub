namespace AudioHub.Core;

/// <summary>Carries a source lifecycle transition.</summary>
public sealed class SourceStateChangedEventArgs : EventArgs
{
    /// <summary>Creates a new instance.</summary>
    public SourceStateChangedEventArgs(string sourceId, SourceState previous, SourceState current, string? message = null)
    {
        SourceId = sourceId;
        Previous = previous;
        Current = current;
        Message = message;
    }

    /// <summary>Identifier of the source that changed state.</summary>
    public string SourceId { get; }

    /// <summary>State before the transition.</summary>
    public SourceState Previous { get; }

    /// <summary>State after the transition.</summary>
    public SourceState Current { get; }

    /// <summary>Optional diagnostic detail, populated especially on <see cref="SourceState.Faulted"/>.</summary>
    public string? Message { get; }
}
