using System.Globalization;
using System.Net;
using System.Text;
using AudioHub.Core.Discovery;

namespace AudioHub.Core.Connection;

/// <summary>Everything a phone needs to reach this hub, as carried by the QR code.</summary>
/// <param name="Host">IPv4 address of the PC on the network the phone can reach.</param>
/// <param name="Port">UDP port the audio goes to.</param>
/// <param name="PairingKey">
/// The pairing key, when the user chose to include it. Null means "address only": the phone
/// must already have the key, or ask for it.
/// </param>
/// <param name="HubId">Stable identity, so the phone can file the pairing under the right hub.</param>
/// <param name="Fingerprint">Human-checkable digest, shown for eyeballing only.</param>
/// <param name="Name">Machine name, so the phone can say which computer it just scanned.</param>
/// <param name="Mode">
/// What this hub currently expects of senders, carried in the code so a scanned connection does
/// not have to guess.
/// </param>
/// <remarks>
/// <para>
/// <b>Why the security mode travels in the code.</b> Before this field existed the phone learned
/// the mode from discovery replies only, so a scan made without running discovery fell back to
/// "encryption optional" — and a phone holding a key would then seal every packet. Against a hub
/// with encryption turned off, sealed packets are not readable at all (there is no envelope on
/// that side), so every single packet was dropped and <i>neither end reported anything</i>. The
/// user saw a phone that said "sending" and a PC that said "nothing arrived".
/// </para>
/// <para>
/// The mode is advisory rather than authoritative: a hostile code could claim anything. It is
/// used to decide the send format, not to decide whether to trust the hub — that is the key's job.
/// </para>
/// </remarks>
public sealed record ConnectionQrPayload(
    string Host,
    int Port,
    string? PairingKey = null,
    Guid? HubId = null,
    string? Fingerprint = null,
    string? Name = null,
    SecurityMode? Mode = null)
{
    /// <summary>Whether this payload can encrypt without asking the user for anything else.</summary>
    public bool CarriesKey => !string.IsNullOrWhiteSpace(PairingKey);
}

/// <summary>
/// Builds and parses the URI a QR code carries.
/// </summary>
/// <remarks>
/// <para>
/// Format: <c>audiohub://c/1?h=&lt;ip&gt;&amp;p=&lt;port&gt;[&amp;k=][&amp;i=][&amp;f=][&amp;n=][&amp;s=]</c>.
/// The version segment is there so a future format can be rejected instead of half-understood:
/// a phone that does not know version 2 must say so rather than silently drop fields.
/// </para>
/// <para>
/// The key is optional on purpose. Putting it in the code makes first-time pairing a single
/// scan, but it also means the key sits on the screen in machine-readable form; that is the
/// user's call, so the PC exposes a switch and this type simply records which way it went.
/// </para>
/// </remarks>
public static class ConnectionQr
{
    /// <summary>URI scheme. Registered by the phone app for optional deep linking.</summary>
    public const string Scheme = "audiohub";

    /// <summary>Format version. Bump only for an incompatible layout change.</summary>
    public const int Version = 1;

    private const int MaxHostLength = 45;
    private const int MaxKeyLength = 64;
    private const int MaxFingerprintLength = 32;
    private const int MaxNameLength = 64;

    /// <summary>Renders the payload as the URI that goes into the QR image.</summary>
    public static string Build(ConnectionQrPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        StringBuilder text = new();
        text.Append(Scheme).Append("://c/").Append(Version.ToString(CultureInfo.InvariantCulture));
        text.Append("?h=").Append(Uri.EscapeDataString(payload.Host));
        text.Append("&p=").Append(payload.Port.ToString(CultureInfo.InvariantCulture));

        if (!string.IsNullOrWhiteSpace(payload.PairingKey))
        {
            text.Append("&k=").Append(Uri.EscapeDataString(payload.PairingKey));
        }

        if (payload.HubId is { } hubId)
        {
            text.Append("&i=").Append(hubId.ToString("D"));
        }

        if (!string.IsNullOrWhiteSpace(payload.Fingerprint))
        {
            text.Append("&f=").Append(Uri.EscapeDataString(payload.Fingerprint));
        }

        if (!string.IsNullOrWhiteSpace(payload.Name))
        {
            text.Append("&n=").Append(Uri.EscapeDataString(payload.Name));
        }

        // Written as a bare number rather than a name: an older phone must be able to ignore a
        // field it does not know, and a newer one must not have to parse English.
        if (payload.Mode is { } mode)
        {
            text.Append("&s=").Append(((int)mode).ToString(CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    /// <summary>
    /// Parses a scanned string. Returns false for another app's QR, an unknown version, a
    /// malformed address or a port outside 1..65535 — never a partially filled payload.
    /// </summary>
    public static bool TryParse(string? text, out ConnectionQrPayload? payload)
    {
        payload = null;

        if (string.IsNullOrWhiteSpace(text) || text.Length > 512)
        {
            return false;
        }

        if (!Uri.TryCreate(text.Trim(), UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        return TryParse(uri, out payload);
    }

    /// <summary>Parses an already-created URI.</summary>
    public static bool TryParse(Uri? uri, out ConnectionQrPayload? payload)
    {
        payload = null;

        if (uri is null
            || !string.Equals(uri.Scheme, Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(uri.AbsolutePath.Trim('/'), NumberStyles.None, CultureInfo.InvariantCulture, out int version)
            || version != Version)
        {
            return false;
        }

        Dictionary<string, string> fields = ParseQuery(uri.Query);

        if (!fields.TryGetValue("h", out string? host)
            || host.Length is 0 or > MaxHostLength
            || !IPAddress.TryParse(host, out IPAddress? address)
            || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return false;
        }

        if (!fields.TryGetValue("p", out string? portText)
            || !int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            return false;
        }

        string? key = fields.TryGetValue("k", out string? rawKey) && rawKey.Length is > 0 and <= MaxKeyLength
            ? rawKey
            : null;

        Guid? hubId = fields.TryGetValue("i", out string? idText) && Guid.TryParse(idText, out Guid parsedId)
            ? parsedId
            : null;

        string? fingerprint = fields.TryGetValue("f", out string? fp) && fp.Length is > 0 and <= MaxFingerprintLength
            ? fp
            : null;

        string? name = fields.TryGetValue("n", out string? rawName) && rawName.Length is > 0 and <= MaxNameLength
            ? rawName
            : null;

        // An unknown or out-of-range mode is dropped rather than defaulted: guessing "optional"
        // is what made a scanned connection seal packets at a hub that had encryption off.
        SecurityMode? mode = fields.TryGetValue("s", out string? modeText)
            && int.TryParse(modeText, NumberStyles.None, CultureInfo.InvariantCulture, out int modeValue)
            && modeValue is 0 or 1 or 2
                ? (SecurityMode)modeValue
                : null;

        payload = new ConnectionQrPayload(address.ToString(), port, key, hubId, fingerprint, name, mode);
        return true;
    }

    /// <summary>
    /// Splits <c>?a=1&amp;b=2</c>. Escapes are decoded per value; a key without a value is
    /// ignored rather than stored as empty, and repeated keys keep the first one.
    /// </summary>
    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);

        foreach (string part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            string name = part[..separator];
            string value = part[(separator + 1)..];

            if (result.ContainsKey(name))
            {
                continue;
            }

            try
            {
                result[name] = Uri.UnescapeDataString(value);
            }
            catch (UriFormatException)
            {
                // A broken escape makes the whole code untrustworthy; dropping the field is
                // enough because every consumer below tolerates a missing optional field.
            }
        }

        return result;
    }
}
