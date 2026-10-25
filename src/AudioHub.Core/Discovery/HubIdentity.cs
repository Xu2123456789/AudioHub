using System.Security.Cryptography;
using System.Text.Json;

namespace AudioHub.Core.Discovery;

/// <summary>
/// This machine's stable identity, used so a phone can tell one hub from another and remember
/// which one it paired with.
/// </summary>
/// <remarks>
/// <para>
/// It has to survive restarts and reinstalls of nothing at all, but it also must not change when
/// the network does. Identity and address are deliberately separate here: the address answers
/// "where is it right now", which changes every time DHCP feels like it, while this answers
/// "is it the same one I paired with last week", which must not.
/// </para>
/// <para>
/// The reason that separation matters is the security work that comes later. A pairing is a
/// promise made to a specific hub. If identity were derived from something that drifts — an IP,
/// a hostname, a MAC address that changes with the adapter — every network change would silently
/// break that promise, and the phone would have no way to tell a re-paired hub from a stranger.
/// See the four-layer plan for why this layer stays put while discovery and security get swapped.
/// </para>
/// </remarks>
public sealed class HubIdentity
{
    /// <summary>Version stamped into the stored file, so a future format change can be detected.</summary>
    private const int FileVersion = 1;

    private HubIdentity(Guid hubId)
    {
        HubId = hubId;
        Fingerprint = MakeFingerprint(hubId);
    }

    /// <summary>Stable identifier for this installation. Never changes once created.</summary>
    public Guid HubId { get; }

    /// <summary>
    /// Short human-checkable string derived from the id. Sixteen nibbles arranged in groups of
    /// four, which is enough to eyeball whether two windows are showing the same machine without
    /// being long enough for anyone to bother transcribing.
    /// </summary>
    public string Fingerprint { get; }

    /// <summary>Where the identity is kept by default.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioHub",
            "identity.json");

    /// <summary>
    /// Reads the identity, creating and saving one on first use. A corrupt or unreadable file is
    /// replaced rather than thrown, because a machine that cannot run audio because a small text
    /// file got damaged is worse than one that quietly picks a new id.
    /// </summary>
    public static HubIdentity LoadOrCreate(string? path = null)
    {
        string file = path ?? DefaultPath;

        if (TryRead(file) is { } existing)
        {
            return existing;
        }

        HubIdentity created = new(Guid.NewGuid());
        created.Save(file);
        return created;
    }

    /// <summary>Creates an identity without touching the disk. Used by tests and nothing else.</summary>
    public static HubIdentity FromGuid(Guid hubId) => new(hubId);

    /// <summary>Writes this identity, creating the directory if needed.</summary>
    public void Save(string? path = null)
    {
        string file = path ?? DefaultPath;
        string? directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(file, JsonSerializer.Serialize(new StoredIdentity(FileVersion, HubId)));
    }

    private static HubIdentity? TryRead(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            StoredIdentity? stored = JsonSerializer.Deserialize<StoredIdentity>(File.ReadAllText(file));
            if (stored is null || stored.Version != FileVersion || stored.HubId == Guid.Empty)
            {
                return null;
            }

            return new HubIdentity(stored.HubId);
        }
        catch (JsonException)
        {
            // Corrupt file. Falling through regenerates rather than blocking startup.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// First eight bytes of a SHA-256 over the id, rendered as hex groups. Hashing rather than
    /// taking the leading characters of the GUID keeps it from leaking the id's internal layout,
    /// which is not a secret but is also nobody's business, and gives both halves of the id a say
    /// in the result.
    /// </summary>
    private static string MakeFingerprint(Guid hubId)
    {
        byte[] hash = SHA256.HashData(hubId.ToByteArray());
        string hex = Convert.ToHexString(hash, 0, 8).ToLowerInvariant();
        return string.Join('-', hex[..4], hex[4..8], hex[8..12], hex[12..16]);
    }

    private sealed record StoredIdentity(int Version, Guid HubId);
}
