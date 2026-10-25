namespace AudioHub.Platform.Windows;

/// <summary>A Windows audio endpoint that AudioHub can render to or capture from.</summary>
/// <param name="Id">WASAPI endpoint identifier.</param>
/// <param name="Name">Friendly name shown in the UI.</param>
/// <param name="IsDefault">Whether this is the system default endpoint for its direction.</param>
public sealed record AudioEndpointInfo(string Id, string Name, bool IsDefault);
