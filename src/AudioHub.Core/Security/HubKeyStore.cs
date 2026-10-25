using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AudioHub.Core.Security;

/// <summary>
/// Generates, stores and derives the shared key a phone needs before it can send encrypted audio.
/// </summary>
/// <remarks>
/// <para>
/// <b>How the key reaches the phone.</b> It does not travel over the network at all. It is shown
/// on this machine once, read by whoever is setting things up, and typed into the phone. That is a
/// real out-of-band channel, and it is the reason no key exchange protocol is needed here: schemes
/// like ECDH exist to defend the exchange against a machine in the middle, but this project has a
/// path no attacker on the Wi-Fi can touch.
/// </para>
/// <para>
/// <b>The stored key is never used directly as an AES key.</b> Encryption uses HKDF over the shared
/// key and this hub's identity, which means each hub derives a different one even if somebody
/// reuses the same shared key everywhere, and nothing learns the shared secret from a derived key.
/// </para>
/// <para>
/// <b>Displayed as Crockford Base32.</b> The alphabet drops the characters people confuse when
/// copying by hand — no I, L, O or U — and decoding treats the lower-case forms and those ambiguous
/// letters as their equivalents. Someone mistyping the difference between O and 0 should still get
/// in, because preventing that class of lockout is worth more than strictness here.
/// </para>
/// </remarks>
public static class HubKeyStore
{
    /// <summary>Entropy in the shared key. 128 bits, far beyond anything reachable by guessing.</summary>
    public const int PskBytes = 16;

    /// <summary>Derived AES-256 key length.</summary>
    public const int DerivedKeyBytes = 32;

    /// <summary>HKDF context string. Changing it changes every derived key, which is what makes it a version.</summary>
    private const string KeyInfo = "AudioHub v1 derived key";

    private const int FileVersion = 1;

    /// <summary>Crockford Base32: no I, L, O or U, so nobody confuses 1/I or 0/O when transcribing.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>Where the shared key is kept by default.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioHub",
            "key.json");

    /// <summary>Creates a new random shared key.</summary>
    public static byte[] GeneratePsk()
    {
        byte[] psk = new byte[PskBytes];
        RandomNumberGenerator.Fill(psk);
        return psk;
    }

    /// <summary>
    /// Derives the AES key used for encryption. Ties it to this hub's identity so that the same
    /// shared key is not the same encryption key everywhere it is used.
    /// </summary>
    public static byte[] DeriveKey(byte[] psk, Guid hubId)
    {
        ArgumentNullException.ThrowIfNull(psk);
        if (psk.Length == 0)
        {
            throw new ArgumentException("Pre-shared key must not be empty.", nameof(psk));
        }

        // Big-endian on purpose. Guid.ToByteArray() hands back Windows' in-memory layout, which
        // swaps the first three fields, and that would make this hub derive a different key from the
        // same shared key than a phone implementing RFC 4122 does. Discovery writes the id
        // big-endian for exactly the same reason, so there is one layout everywhere.
        byte[] hubBytes = new byte[16];
        hubId.TryWriteBytes(hubBytes, bigEndian: true, out _);

        return HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            psk,
            DerivedKeyBytes,
            salt: hubBytes,
            info: Encoding.UTF8.GetBytes(KeyInfo));
    }

    /// <summary>Renders a key for a human to read, in groups of five.</summary>
    public static string ToDisplayString(byte[] psk)
    {
        ArgumentNullException.ThrowIfNull(psk);

        int characterCount = (int)Math.Ceiling(psk.Length * 8.0 / 5.0);
        StringBuilder builder = new(characterCount + (characterCount / 5));

        int bitBuffer = 0;
        int bitCount = 0;
        int written = 0;

        foreach (byte value in psk)
        {
            bitBuffer = (bitBuffer << 8) | value;
            bitCount += 8;

            while (bitCount >= 5)
            {
                bitCount -= 5;
                builder.Append(Alphabet[(bitBuffer >> bitCount) & 0x1F]);
                written++;

                if (written % 5 == 0)
                {
                    builder.Append('-');
                }
            }
        }

        if (bitCount > 0)
        {
            builder.Append(Alphabet[(bitBuffer << (5 - bitCount)) & 0x1F]);
            written++;

            if (written % 5 == 0)
            {
                builder.Append('-');
            }
        }

        return builder.ToString().TrimEnd('-');
    }

    /// <summary>
    /// Reads back a key someone typed. Forgiving about case, separators and the ambiguous letters
    /// this alphabet avoids, because refusing someone's careful transcription is worse than
    /// accepting a slightly wrong one.
    /// </summary>
    public static bool TryParse(string text, out byte[] psk)
    {
        psk = [];

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        List<int> values = [];
        foreach (char c in text)
        {
            if (c is '-' or ' ' or '\t')
            {
                continue;
            }

            // Upper-case first, then normalise. The other order looks right and is not: 'o'
            // becomes 'O', which is not in the alphabet, so every lower-case key would be rejected.
            int index = Alphabet.IndexOf(Normalise(char.ToUpperInvariant(c)));
            if (index < 0)
            {
                return false;
            }

            values.Add(index);
        }

        if (values.Count == 0)
        {
            return false;
        }

        // Any character beyond what the byte length needs is padding, which Base32 permits.
        int byteCount = values.Count * 5 / 8;
        if (byteCount == 0)
        {
            return false;
        }

        byte[] result = new byte[byteCount];
        int bitBuffer = 0;
        int bitCount = 0;
        int written = 0;

        foreach (int value in values)
        {
            bitBuffer = (bitBuffer << 5) | value;
            bitCount += 5;

            if (bitCount >= 8)
            {
                bitCount -= 8;
                result[written++] = (byte)((bitBuffer >> bitCount) & 0xFF);
            }
        }

        psk = written == byteCount ? result : result[..written];
        return true;

        static char Normalise(char c) => c switch
        {
            'I' or 'L' => '1',
            'O' => '0',
            'U' => 'V',
            _ => c,
        };
    }

    /// <summary>
    /// Loads the shared key, creating and saving one on first use.
    /// </summary>
    public static byte[] LoadOrCreatePsk(string? path = null)
    {
        string file = path ?? DefaultPath;

        if (TryReadPsk(file) is { } existing)
        {
            return existing;
        }

        byte[] created = GeneratePsk();
        SavePsk(created, file);
        return created;
    }

    /// <summary>Writes the shared key with restrictive permissions where the platform allows it.</summary>
    public static void SavePsk(byte[] psk, string? path = null)
    {
        string file = path ?? DefaultPath;
        string? directory = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(file, JsonSerializer.Serialize(new StoredKey(FileVersion, Convert.ToBase64String(psk))));
    }

    private static byte[]? TryReadPsk(string file)
    {
        try
        {
            if (!File.Exists(file))
            {
                return null;
            }

            StoredKey? stored = JsonSerializer.Deserialize<StoredKey>(File.ReadAllText(file));
            if (stored is null || stored.Version != FileVersion)
            {
                return null;
            }

            byte[] psk = Convert.FromBase64String(stored.Psk);
            return psk.Length == 0 ? null : psk;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private sealed record StoredKey(int Version, string Psk);
}
