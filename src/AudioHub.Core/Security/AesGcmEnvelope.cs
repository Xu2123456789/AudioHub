using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AudioHub.Core.Security;

/// <summary>
/// AES-256-GCM in a thin envelope: just enough cleartext to be recognised and routed, everything
/// else encrypted and authenticated.
/// </summary>
/// <remarks>
/// <para>
/// <b>The layout.</b> Twenty cleartext bytes lead each packet — a magic number, a version, a
/// type, the four-byte salt and eight-byte counter that together form the nonce, and a length.
/// Then the ciphertext, then the sixteen-byte authentication tag. Every one of those leading
/// bytes is fed to GCM as additional authenticated data, so changing any of them fails the tag
/// just as surely as changing the payload does. Nothing beyond those twenty bytes is readable.
/// </para>
/// <para>
/// <b>Why the salt travels in the packet.</b> The nonce must never repeat under one key; in GCM a
/// repeat is not merely weak, it leaks the authentication subkey and lets an attacker forge. A
/// counter alone would restart every time the hub restarts, producing exactly that collision. So
/// the salt is drawn fresh per sender instance and carried with the packet, and the counter rises
/// monotonously beneath it. Reading it costs four bytes; not reading it costs the security.
/// </para>
/// <para>
/// <b>Why there is no handshake.</b> The alternative to sharing a pre-shared key is a key exchange
/// that has to be protected against a machine in the middle, which is what every password-based
/// handshake is really for. This scheme has a channel those do not: whoever paired the phone can
/// read the key off this screen and type it into the phone. That is a genuine out-of-band path, so
/// the extra protocol would buy complexity rather than safety.
/// </para>
/// </remarks>
public sealed class AesGcmEnvelope : ISecureEnvelope
{
    /// <summary>Bytes added to every packet: header plus tag.</summary>
    public const int Overhead = HeaderBytes + TagBytes;

    private const int HeaderBytes = 20;
    private const int TagBytes = 16;
    private const byte FormatVersion = 1;
    private const byte TypeAudio = 1;

    private const ulong MaxPacketsPerSalt = 1UL << 48;

    private readonly byte[] _key;
    private readonly uint _salt;
    private ulong _counter;

    /// <summary>Creates an envelope using <paramref name="key"/> (32 bytes for AES-256).</summary>
    /// <param name="key">Derived key. Never the raw pre-shared key — see <see cref="HubKeyStore"/>.</param>
    /// <param name="salt">
    /// Nonce prefix for this sender instance. Pass nothing to have one generated, which is right
    /// for sending. Supplying a fixed one lets tests reason about nonce reuse deliberately.
    /// about nonce reuse.
    /// </param>
    /// <param name="rng">Random source, injectable so tests can be deterministic.</param>
    public AesGcmEnvelope(byte[] key, uint? salt = null, RandomNumberGenerator? rng = null)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != 32)
        {
            throw new ArgumentException("AES-256 needs a 32-byte key.", nameof(key));
        }

        _key = (byte[])key.Clone();

        if (salt is { } given)
        {
            _salt = given;
        }
        else
        {
            Span<byte> bytes = stackalloc byte[4];
            if (rng is not null)
            {
                rng.GetBytes(bytes);
            }
            else
            {
                RandomNumberGenerator.Fill(bytes);
            }

            _salt = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        }
    }

    /// <summary>The salt this instance prefixes its nonces with.</summary>
    public uint Salt => _salt;

    /// <summary>Counter used so far. Rises by one per packet and is what makes nonces distinct.</summary>
    public ulong Counter => Volatile.Read(ref _counter);

    /// <inheritdoc />
    public bool IsEnabled => true;

    /// <inheritdoc />
    public int OverheadBytes => Overhead;

    /// <inheritdoc />
    public bool LooksProtected(ReadOnlySpan<byte> packet) =>
        packet.Length >= HeaderBytes && HasMagic(packet);

    private static bool HasMagic(ReadOnlySpan<byte> packet) =>
        packet[0] == 0x41 && packet[1] == 0x48 && packet[2] == 0x53 && packet[3] == 0x31;

    /// <inheritdoc />
    public byte[] Seal(ReadOnlySpan<byte> plaintext)
    {
        if (plaintext.Length > ushort.MaxValue - Overhead)
        {
            throw new ArgumentException("Payload too large for one packet.", nameof(plaintext));
        }

        ulong counter = Interlocked.Increment(ref _counter);
        if (counter >= MaxPacketsPerSalt)
        {
            // Reached rather than passed: at this point the counter has run out of room inside the
            // salt's remaining nonce space and reuse would become possible.
            throw new CryptographicException("Nonce space exhausted; a new salt is required.");
        }

        byte[] packet = new byte[HeaderBytes + plaintext.Length + TagBytes];
        Span<byte> header = packet.AsSpan(0, HeaderBytes);

        header[0] = 0x41;
        header[1] = 0x48;
        header[2] = 0x53;
        header[3] = 0x31;
        header[4] = FormatVersion;
        header[5] = TypeAudio;
        BinaryPrimitives.WriteUInt32LittleEndian(header[6..10], _salt);
        BinaryPrimitives.WriteUInt64LittleEndian(header[10..18], counter);
        BinaryPrimitives.WriteUInt16LittleEndian(header[18..20], (ushort)(plaintext.Length + TagBytes));

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, _salt, counter);

        using AesGcm gcm = new(_key, TagBytes);
        gcm.Encrypt(
            nonce,
            plaintext,
            packet.AsSpan(HeaderBytes, plaintext.Length),
            packet.AsSpan(HeaderBytes + plaintext.Length, TagBytes),
            header);

        return packet;
    }

    /// <inheritdoc />
    public bool TryOpen(ReadOnlySpan<byte> packet, out ReadOnlyMemory<byte> plaintext, out string? rejection)
    {
        plaintext = ReadOnlyMemory<byte>.Empty;
        rejection = null;

        if (packet.Length < HeaderBytes + TagBytes)
        {
            rejection = $"包太短（{packet.Length} 字节，至少 {HeaderBytes + TagBytes}）";
            return false;
        }

        ReadOnlySpan<byte> header = packet[..HeaderBytes];

        if (!HasMagic(header))
        {
            rejection = "不是加密的 AudioHub 包（魔数不符）；若当前是明文模式，这是本该如此的";
            return false;
        }

        if (header[4] != FormatVersion)
        {
            rejection = $"加密格式版本不符（收到 {header[4]}，要求 {FormatVersion}）";
            return false;
        }

        int declared = BinaryPrimitives.ReadUInt16LittleEndian(header[18..20]);
        if (packet.Length != HeaderBytes + declared)
        {
            rejection = $"声明的长度 {declared} 与实际 {packet.Length - HeaderBytes} 不符";
            return false;
        }

        if (declared < TagBytes)
        {
            rejection = "声明的长度连认证标签都放不下";
            return false;
        }

        uint salt = BinaryPrimitives.ReadUInt32LittleEndian(header[6..10]);
        ulong counter = BinaryPrimitives.ReadUInt64LittleEndian(header[10..18]);

        Span<byte> nonce = stackalloc byte[12];
        WriteNonce(nonce, salt, counter);

        byte[] output = new byte[declared - TagBytes];

        try
        {
            using AesGcm gcm = new(_key, TagBytes);
            gcm.Decrypt(
                nonce,
                packet[HeaderBytes..(packet.Length - TagBytes)],
                packet[(packet.Length - TagBytes)..],
                output,
                header);
        }
        catch (CryptographicException)
        {
            // The only reliable outcome: either the key is wrong, or something was altered. Never
            // report which, and never hand back partially decrypted bytes.
            plaintext = ReadOnlyMemory<byte>.Empty;
            rejection = "认证失败：密钥不对，或包在传输中被改动、重放过";
            return false;
        }

        plaintext = output;
        return true;
    }

    private static void WriteNonce(Span<byte> destination, uint salt, ulong counter)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(destination[..4], salt);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], counter);
    }
}
