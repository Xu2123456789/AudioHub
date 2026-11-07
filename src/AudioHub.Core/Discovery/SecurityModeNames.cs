namespace AudioHub.Core.Discovery;

/// <summary>
/// The persisted (name) form of <see cref="SecurityMode"/>.
/// </summary>
/// <remarks>
/// Settings are stored by name rather than by number so a hand-edited file stays readable and
/// reordering the enum cannot silently change what a saved value means. Parsing is
/// case-insensitive and trims whitespace; an unknown value falls back to
/// <see cref="SecurityMode.Optional"/> (accepts both) rather than preventing startup or silently
/// turning the receiver into plaintext-only.
/// </remarks>
public static class SecurityModeNames
{
    /// <summary>The canonical name written to settings.</summary>
    public static string ToName(SecurityMode mode) => mode switch
    {
        SecurityMode.None => "None",
        SecurityMode.Required => "Required",
        _ => "Optional",
    };

    /// <summary>Parses a saved name, falling back to <see cref="SecurityMode.Optional"/>.</summary>
    public static SecurityMode Parse(string? name) => name?.Trim().ToUpperInvariant() switch
    {
        "NONE" => SecurityMode.None,
        "REQUIRED" => SecurityMode.Required,
        _ => SecurityMode.Optional,
    };
}
