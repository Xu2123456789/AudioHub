using System.IO;
using System.Text.Json;

namespace AudioHub.App;

/// <summary>
/// The handful of interface choices worth remembering between runs.
/// </summary>
/// <remarks>
/// Deliberately a separate file from <c>identity.json</c>: losing this one costs a checkbox
/// position, while losing the identity would cost every pairing the phone has made. Keeping
/// them apart means a corrupt UI preference cannot take the identity with it.
/// </remarks>
internal sealed class UiSettings
{
    /// <summary>
    /// Whether the QR code carries the pairing key. On by default: the common case is a first
    /// pairing, and one scan beats "scan, then type 25 characters". Users who would rather not
    /// put the key on screen can turn it off.
    /// </summary>
    public bool QrIncludesPairingKey { get; set; } = true;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AudioHub",
        "ui.json");

    /// <summary>
    /// Reads the settings, falling back to defaults for a missing, unreadable or corrupt file.
    /// A preference file must never be the reason the app fails to open.
    /// </summary>
    public static UiSettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                return new UiSettings();
            }

            return JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path)) ?? new UiSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new UiSettings();
        }
    }

    /// <summary>Writes the settings. Failure is ignored: it costs one checkbox position.</summary>
    public void Save()
    {
        try
        {
            string path = FilePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing here is worth interrupting the user for.
        }
    }
}
