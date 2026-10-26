using System.Net;

namespace AudioHub.Core.Connection;

/// <summary>
/// Six-character code that spells out a private IPv4 address.
/// </summary>
/// <remarks>
/// <para>
/// The point is the case where discovery is blocked but unicast still works: the phone can be
/// told an address by a human reading six characters off the PC screen, without any lookup
/// round trip. Decoding happens entirely on the phone, so it cannot fail because a broadcast
/// was filtered.
/// </para>
/// <para>
/// Crockford Base32 is used because it drops I, L, O and U — the characters people mix up when
/// copying from a screen. Decoding additionally accepts lower case and the usual look-alikes,
/// so a misread does not turn into "cannot connect" for no reason.
/// </para>
/// <para>
/// Layout, 30 bits (six characters at five bits each):
/// <c>[2 bits class][24 bits body][4 bits CRC-4]</c>. The class picks which private range the
/// body belongs to: 10/8 (full three octets), 192.168/16 (last two octets), 172.16/12
/// (second octet minus 16, then two octets). Anything else — public addresses, CGNAT — cannot
/// be encoded, and the caller is expected to offer the QR code instead.
/// </para>
/// <para>
/// The checksum is not security. It exists so a single mistyped character is rejected instead of
/// silently decoding to a different machine on the same network.
/// </para>
/// </remarks>
public static class ShortCode
{
    /// <summary>How many characters a code has.</summary>
    public const int Length = 6;

    /// <summary>The audio port a code implies. Codes do not carry a port.</summary>
    public const int DefaultPort = 6980;

    /// <summary>Alphabet after removing I, L, O and U.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    /// <summary>
    /// Encodes a private IPv4 address. Returns false for anything the layout cannot represent,
    /// which is a normal answer rather than an error: the UI then shows "use the QR code".
    /// </summary>
    public static bool TryEncode(IPAddress address, out string code)
    {
        ArgumentNullException.ThrowIfNull(address);

        code = string.Empty;

        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length != 4)
        {
            return false;
        }

        int ipClass;
        uint body;

        if (bytes[0] == 10)
        {
            ipClass = 0;
            body = (uint)((bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        }
        else if (bytes[0] == 192 && bytes[1] == 168)
        {
            ipClass = 1;
            body = (uint)((bytes[2] << 8) | bytes[3]);
        }
        else if (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
        {
            ipClass = 2;
            body = (uint)(((bytes[1] - 16) << 16) | (bytes[2] << 8) | bytes[3]);
        }
        else
        {
            return false;
        }

        uint prefix = ((uint)ipClass << 24) | body;
        uint value = (prefix << 4) | (uint)Crc4(prefix, 26);

        Span<char> chars = stackalloc char[Length];
        for (int i = Length - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        code = new string(chars);
        return true;
    }

    /// <summary>Convenience overload that parses the address first.</summary>
    public static bool TryEncode(string address, out string code)
    {
        code = string.Empty;
        return IPAddress.TryParse(address, out IPAddress? parsed) && TryEncode(parsed, out code);
    }

    /// <summary>
    /// Decodes a code. Whitespace, dashes and lower case are tolerated; so are the four
    /// characters Crockford removes (I/L read as 1, O as 0, U as V). A wrong checksum or an
    /// unrepresentable class returns false, never a guess.
    /// </summary>
    public static bool TryDecode(string? code, out IPAddress? address)
    {
        address = null;

        if (code is null)
        {
            return false;
        }

        string normalized = Normalize(code);
        if (normalized.Length != Length)
        {
            return false;
        }

        uint value = 0;
        foreach (char c in normalized)
        {
            int index = Alphabet.IndexOf(c);
            if (index < 0)
            {
                return false;
            }

            value = (value << 5) | (uint)index;
        }

        uint prefix = value >> 4;
        int expected = (int)(value & 0xF);
        if (Crc4(prefix, 26) != expected)
        {
            return false;
        }

        int ipClass = (int)(prefix >> 24) & 0x3;
        uint body = prefix & 0xFFFFFF;

        byte[] bytes = new byte[4];

        switch (ipClass)
        {
            case 0:
                bytes[0] = 10;
                bytes[1] = (byte)(body >> 16);
                bytes[2] = (byte)(body >> 8);
                bytes[3] = (byte)body;
                break;

            case 1:
                // Only two octets are meaningful here; a code that sets the spare bits is
                // malformed rather than "almost right".
                if (body > 0xFFFF)
                {
                    return false;
                }

                bytes[0] = 192;
                bytes[1] = 168;
                bytes[2] = (byte)(body >> 8);
                bytes[3] = (byte)body;
                break;

            case 2:
                if (body > 0xFFFFF)
                {
                    return false;
                }

                bytes[0] = 172;
                bytes[1] = (byte)(16 + (byte)(body >> 16));
                bytes[2] = (byte)(body >> 8);
                bytes[3] = (byte)body;
                break;

            default:
                return false;
        }

        address = new IPAddress(bytes);
        return true;
    }

    /// <summary>
    /// CRC-4 with polynomial x^4 + x + 1, initial value 1111, no reflection and no final xor.
    /// Deliberately the simplest possible form: the same seven lines exist in Kotlin and in the
    /// Python script that produced the golden vectors, and all three must agree.
    /// </summary>
    internal static int Crc4(uint value, int bitCount)
    {
        int crc = 0xF;

        for (int bit = bitCount - 1; bit >= 0; bit--)
        {
            int top = (crc >> 3) & 1;
            int next = (int)((value >> bit) & 1);

            crc = (crc << 1) & 0xF;
            if ((top ^ next) != 0)
            {
                crc ^= 0x3;
            }
        }

        return crc;
    }

    /// <summary>Upper-cases and folds the characters people misread.</summary>
    private static string Normalize(string raw)
    {
        Span<char> buffer = stackalloc char[raw.Length];
        int length = 0;

        foreach (char c in raw)
        {
            if (char.IsWhiteSpace(c) || c == '-' || c == '_')
            {
                continue;
            }

            char upper = char.ToUpperInvariant(c);
            upper = upper switch
            {
                'I' or 'L' => '1',
                'O' => '0',
                'U' => 'V',
                _ => upper,
            };

            buffer[length++] = upper;
        }

        return new string(buffer[..length]);
    }
}
