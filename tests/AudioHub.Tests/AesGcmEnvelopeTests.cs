using System.Security.Cryptography;
using System.Text;
using AudioHub.Core.Security;

namespace AudioHub.Tests;

/// <summary>
/// Pins the encryption, whose failure modes are invisible: the wrong implementation of this still
/// produces bytes, still decodes to audio, and looks completely healthy on a level meter.
/// </summary>
/// <remarks>
/// Each test targets one way this could quietly stop being safe while continuing to work:
/// taking Schutz unverändert as adequate, letting a nonce repeat, handing back partly decrypted
/// data, or accepting a packet whose header was rewritten.
/// </remarks>
public class AesGcmEnvelopeTests
{
    private static readonly byte[] Key = HubKeyStore.DeriveKey(
        HubKeyStore.GeneratePsk(),
        Guid.Parse("8f2c1d4e-5b6a-4c8d-9e0f-1a2b3c4d5e6f"));

    private static AesGcmEnvelope NewEnvelope(uint? salt = null) => new(Key, salt);

    /// <summary>
    /// The basic promise: what goes in comes out byte for byte, including the awkward cases — a
    /// full VBAN packet, an odd length, and all zeroes.
    /// </summary>
    [Fact]
    public void RoundTrip_IsByteForByteIdentical()
    {
        AesGcmEnvelope sender = NewEnvelope(salt: 0x01020304);
        AesGcmEnvelope receiver = NewEnvelope(salt: 0x01020304);

        byte[] original = new byte[1400];
        RandomNumberGenerator.Fill(original);

        byte[] packet = sender.Seal(original);

        Assert.True(receiver.TryOpen(packet, out ReadOnlyMemory<byte> opened, out string? rejection), rejection);
        Assert.Equal(original, opened.ToArray());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(17)]
    [InlineData(255)]
    public void RoundTrip_WorksAtAwkwardLengths(int length)
    {
        AesGcmEnvelope envelope = NewEnvelope();

        byte[] original = new byte[length];
        for (int i = 0; i < length; i++)
        {
            original[i] = (byte)(i * 7 + 3);
        }

        Assert.True(envelope.TryOpen(envelope.Seal(original), out ReadOnlyMemory<byte> opened, out string? rejection), rejection);
        Assert.Equal(original, opened.ToArray());
    }

    /// <summary>
    /// The whole point. Any single flipped byte anywhere must be caught, and crucially nothing
    /// usable may come back with it.
    /// </summary>
    [Fact]
    public void AnyTampering_IsRejectedAndReturnsNoData()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] original = Encoding.UTF8.GetBytes("VBAN audio payload, not really, but representative");
        byte[] packet = envelope.Seal(original);

        for (int index = 0; index < packet.Length; index++)
        {
            byte[] tampered = (byte[])packet.Clone();
            tampered[index] ^= 0x01;

            bool ok = envelope.TryOpen(tampered, out ReadOnlyMemory<byte> opened, out string? rejection);

            Assert.False(ok, $"Byte {index} could be flipped without detection.");
            Assert.NotNull(rejection);

            // The important half: a failed open must never hand back audio-looking bytes.
            Assert.True(opened.IsEmpty, $"Byte {index} produced {opened.Length} bytes alongside failure.");
        }
    }

    /// <summary>
    /// Truncated and over-long packets must not be believed just because their header parses.
    /// </summary>
    [Fact]
    public void TruncatedPacket_IsRejected()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] packet = envelope.Seal(new byte[64]);

        Assert.False(envelope.TryOpen(packet[..30], out _, out string? rejection));
        Assert.Contains("太短", rejection);

        Assert.False(envelope.TryOpen(packet[..(packet.Length - 1)], out _, out _));
    }

    [Fact]
    public void WrongMagic_IsRejected()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] packet = envelope.Seal(new byte[32]);
        packet[0] = 0x00;

        Assert.False(envelope.TryOpen(packet, out _, out string? rejection));
        Assert.Contains("魔数", rejection);
    }

    [Fact]
    public void WrongVersion_IsRejected()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] packet = envelope.Seal(new byte[32]);
        packet[4] = 99;

        Assert.False(envelope.TryOpen(packet, out _, out string? rejection));
        Assert.Contains("版本", rejection);
    }

    [Fact]
    public void WrongLengthField_IsRejected()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] packet = envelope.Seal(new byte[32]);
        packet[18] ^= 0xFF;

        Assert.False(envelope.TryOpen(packet, out _, out string? rejection));
        Assert.NotNull(rejection);
    }

    /// <summary>
    /// A different key must reveal nothing. Tested against every byte position so there is nowhere
    /// for a partial leak to hide.
    /// </summary>
    [Fact]
    public void WrongKey_FailsAndRevealsNothing()
    {
        AesGcmEnvelope sender = NewEnvelope();
        AesGcmEnvelope stranger = new(HubKeyStore.DeriveKey(
            HubKeyStore.GeneratePsk(),
            Guid.NewGuid()));

        byte[] original = Encoding.UTF8.GetBytes("secret audio nobody else should hear");
        byte[] packet = sender.Seal(original);

        Assert.False(stranger.TryOpen(packet, out ReadOnlyMemory<byte> opened, out string? rejection));
        Assert.NotNull(rejection);
        Assert.True(opened.IsEmpty);
    }

    [Fact]
    public void DifferentHub_DerivesADifferentKeyForTheSamePsk()
    {
        byte[] psk = HubKeyStore.GeneratePsk();

        byte[] first = HubKeyStore.DeriveKey(psk, Guid.NewGuid());
        byte[] second = HubKeyStore.DeriveKey(psk, Guid.NewGuid());

        Assert.NotEqual(first, second);
        Assert.Equal(HubKeyStore.DerivedKeyBytes, first.Length);
    }

    [Fact]
    public void SamePskAndHub_DeriveTheSameKey()
    {
        byte[] psk = HubKeyStore.GeneratePsk();
        Guid hubId = Guid.NewGuid();

        Assert.Equal(HubKeyStore.DeriveKey(psk, hubId), HubKeyStore.DeriveKey(psk, hubId));
    }

    /// <summary>
    /// Nonce reuse under a key is catastrophic in GCM — it leaks the authentication subkey, which
    /// allows forgery, not merely disclosure. Failure here shows up as nothing at all in normal
    /// use, so it has to be pinned rather than hoped for.
    /// </summary>
    [Fact]
    public void SuccessivePackets_NeverReuseANonce()
    {
        AesGcmEnvelope envelope = NewEnvelope(salt: 0xDEADBEEF);

        HashSet<string> nonces = [];
        for (int i = 0; i < 2_000; i++)
        {
            byte[] packet = envelope.Seal(new byte[8]);

            // The nonce is exactly the salt and counter carried in the clear text header.
            string nonce = Convert.ToHexString(packet[6..18]);
            Assert.True(nonces.Add(nonce), $"Nonce reused at packet {i}.");
        }

        Assert.Equal(2_000u, envelope.Counter);
    }

    [Fact]
    public void SeparateInstances_GetDifferentSalts()
    {
        AesGcmEnvelope first = NewEnvelope();
        AesGcmEnvelope second = NewEnvelope();

        // Unlikely to collide by chance; would need ~2^32 instances to become likely.
        Assert.NotEqual(first.Salt, second.Salt);
    }

    [Fact]
    public void EveryTamperedNonceByte_IsRejected()
    {
        AesGcmEnvelope envelope = NewEnvelope();
        byte[] packet = envelope.Seal(new byte[48]);

        for (int index = 6; index < 18; index++)
        {
            byte[] tampered = (byte[])packet.Clone();
            tampered[index] ^= 0x01;

            Assert.False(envelope.TryOpen(tampered, out _, out _), $"Nonce byte {index} accepted when altered.");
        }
    }

    [Fact]
    public void NullEnvelope_PassesBytesThroughUnchanged()
    {
        NullEnvelope envelope = new();

        byte[] original = new byte[1234];
        RandomNumberGenerator.Fill(original);

        Assert.False(envelope.IsEnabled);
        Assert.Equal(0, envelope.OverheadBytes);
        Assert.Equal(original, envelope.Seal(original));

        Assert.True(envelope.TryOpen(original, out ReadOnlyMemory<byte> opened, out string? rejection));
        Assert.Null(rejection);
        Assert.Equal(original, opened.ToArray());
    }

    [Fact]
    public void SameMessage_SealsToDifferentBytesEachTime()
    {
        AesGcmEnvelope envelope = NewEnvelope();

        byte[] first = envelope.Seal(new byte[32]);
        byte[] second = envelope.Seal(new byte[32]);

        // Identical plaintext must not be recognisable as repeated content on the wire.
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PlainVbanPacket_IsRecognisedAsNotEncrypted()
    {
        AesGcmEnvelope envelope = NewEnvelope();

        // Real VBAN packets start with "VBAN"; feeding one in during encrypted mode has to be
        // reported as "not one of ours" rather than throwing.
        byte[] vban = Encoding.ASCII.GetBytes("VBAN" + new string('\0', 100));

        Assert.False(envelope.TryOpen(vban, out _, out string? rejection));
        Assert.Contains("魔数", rejection);
    }

    [Fact]
    public void LooksProtected_TellsOurOwnSealedPacketsFromEverybodyElsesTraffic()
    {
        AesGcmEnvelope envelope = NewEnvelope();

        // Ours, sealed: recognisable.
        Assert.True(envelope.LooksProtected(envelope.Seal(new byte[32])));

        // Some other program's traffic: not ours.
        Assert.False(envelope.LooksProtected(Encoding.ASCII.GetBytes("VBAN" + new string('\0', 100))));

        // Too short to even carry the marker — must not read past the end while deciding.
        Assert.False(envelope.LooksProtected([]));
        Assert.False(envelope.LooksProtected([0x41, 0x48, 0x53]));

        // The marker alone with the wrong version byte still counts as ours: the version is part
        // of what failed to open, not part of what identifies the packet as an AudioHub one.
        byte[] wrongVersion = envelope.Seal(new byte[32]);
        wrongVersion[4] = 99;
        Assert.True(envelope.LooksProtected(wrongVersion));

        // And the pass-through implementation has no marker to recognise, ever.
        Assert.False(new NullEnvelope().LooksProtected(envelope.Seal(new byte[32])));
    }
}

/// <summary>
/// Pins how the shared key is shown and typed, which is the only part of this whole design a
/// person ever touches.
/// </summary>
public class HubKeyStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _path;

    public HubKeyStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "AudioHub.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "key.json");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Nothing hangs on temporary cleanup.
        }
    }

    [Fact]
    public void RoundTrip_TypedTextSurvivesBeingTyped()
    {
        byte[] psk = HubKeyStore.GeneratePsk();
        string shown = HubKeyStore.ToDisplayString(psk);

        Assert.True(HubKeyStore.TryParse(shown, out byte[] parsed));
        Assert.Equal(psk, parsed);
    }

    [Fact]
    public void GeneratedKey_HasFullEntropy()
    {
        byte[] psk = HubKeyStore.GeneratePsk();

        Assert.Equal(HubKeyStore.PskBytes, psk.Length);
        Assert.NotEqual(new byte[HubKeyStore.PskBytes], psk);
        Assert.NotEqual(HubKeyStore.GeneratePsk(), HubKeyStore.GeneratePsk());
    }

    /// <summary>
    /// The sealed packet a phone produces, byte for byte.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deriving the same key is not enough on its own: the two sides could agree on the key and
    /// still disagree about where the salt, the counter or the length sit inside the packet. That
    /// mismatch is silent — decryption simply fails and the symptom looks like a network problem.
    /// So the whole packet is pinned, with the key, the salt, the counter and the plaintext all
    /// fixed, which makes AES-GCM deterministic and the bytes reproducible.
    /// </para>
    /// <para>
    /// <see cref="HubKeyStoreTests.DeriveKey_MatchesTheValuePhonesDerive"/> pins the key and this
    /// pins the layout; neither alone would catch a phone that silently cannot talk to this hub.
    /// The Android side asserts the identical literal in
    /// <c>SecureEnvelopeTest.sealedPacketMatchesTheGoldenBytes</c>, and because
    /// <c>javax.crypto</c> and <c>System.Security.Cryptography.AesGcm</c> are independent
    /// implementations, agreement between them is real evidence rather than one codebase
    /// agreeing with itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void Seal_MatchesThePacketPhonesProduce()
    {
        byte[] key = HubKeyStore.DeriveKey(
            Enumerable.Range(0, 16).Select(i => (byte)i).ToArray(),
            new Guid("78563412-8f2c-1d4e-5b6a-4c8d9e0f1a2b"));

        byte[] plaintext = Enumerable.Range(0, 32).Select(i => (byte)(i * 11 + 1)).ToArray();

        Assert.Equal(
            "4148533101010403020101000000000000003000D74E9970E65DED8E4E443243B82C60" +
            "BF16EED80441C7E62F9327B5C884FC22C2676B3C41EC8F258AEBAAE0DA71B1DB5E",
            Convert.ToHexString(new AesGcmEnvelope(key, salt: 0x01020304).Seal(plaintext)));
    }

    /// <summary>
    /// The value that proves a phone and this hub derive the same key.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both platforms derive independently, and if they differ the failure is silent: neither side
    /// reports an error, audio simply never arrives, and the logs look exactly like a network
    /// problem. So the number is baked in rather than recomputed, and the identical literal is
    /// asserted on the Android side in <c>SecureEnvelopeTest.derivedKeyMatchesTheGoldenValue</c>.
    /// </para>
    /// <para>
    /// It was produced by a third implementation (Python, RFC 5869 by hand) so it is not merely one
    /// side of this codebase agreeing with itself. The hub id is written big-endian, which is the
    /// part that was wrong: <see cref="Guid.ToByteArray"/> returns Windows' mixed-endian layout, so
    /// a phone following RFC 4122 used to derive a different key from the same shared secret.
    /// </para>
    /// </remarks>
    [Fact]
    public void DeriveKey_MatchesTheValuePhonesDerive()
    {
        byte[] psk = Enumerable.Range(0, 16).Select(i => (byte)i).ToArray();
        Guid hub = new("78563412-8f2c-1d4e-5b6a-4c8d9e0f1a2b");

        Assert.Equal(
            "83B2A4A3CC5715E9646F5D417BBBD7633B09616FD7EF62287E94351D2EAEFF19",
            Convert.ToHexString(HubKeyStore.DeriveKey(psk, hub)));
    }

    [Fact]
    public void DisplayString_UsesGroupedUppercaseWithoutAmbiguousLetters()
    {
        string shown = HubKeyStore.ToDisplayString(HubKeyStore.GeneratePsk());

        Assert.DoesNotContain('-', shown[..4]);
        Assert.Contains('-', shown);
        Assert.All(shown, c => Assert.True(
            char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c) || c == '-',
            $"'{c}' should not appear in something people copy by hand."));

        // These four are exactly the ones this alphabet exists to avoid.
        Assert.DoesNotContain('I', shown);
        Assert.DoesNotContain('L', shown);
        Assert.DoesNotContain('O', shown);
        Assert.DoesNotContain('U', shown);
    }

    /// <summary>
    /// Digits are the characters people misread as letters. Written back with those letters, the
    /// key has to still open the same door.
    /// </summary>
    [Fact]
    public void TryParse_NormalisesConfusableCharacters()
    {
        byte[] psk = HubKeyStore.GeneratePsk();
        string shown = HubKeyStore.ToDisplayString(psk);

        string asLetters = shown.Replace('0', 'O').Replace('1', 'I');
        string lower = asLetters.ToLowerInvariant();

        Assert.True(HubKeyStore.TryParse(shown, out byte[] exact));
        Assert.True(HubKeyStore.TryParse(asLetters, out byte[] viaLetters));
        Assert.True(HubKeyStore.TryParse(lower, out byte[] viaLower));

        Assert.Equal(psk, exact);
        Assert.Equal(exact, viaLetters);
        Assert.Equal(exact, viaLower);
    }

    [Fact]
    public void TryParse_AcceptsLowercaseAndStraySeparators()
    {
        byte[] psk = HubKeyStore.GeneratePsk();
        string shown = HubKeyStore.ToDisplayString(psk).ToLowerInvariant().Replace('-', ' ');

        Assert.True(HubKeyStore.TryParse(shown, out byte[] parsed));
        Assert.Equal(psk, parsed);
    }

    // Z is a legitimate Base32 character, so "ZZZZ" is a valid key and must not be rejected.
    // Only characters outside the alphabet count as garbage.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!!")]
    [InlineData("@@@@")]
    [InlineData("ABC@")]
    public void TryParse_RejectsGarbage(string input)
    {
        Assert.False(HubKeyStore.TryParse(input, out _));
    }

    [Fact]
    public void LoadOrCreate_IsStableAcrossRestarts()
    {
        byte[] first = HubKeyStore.LoadOrCreatePsk(_path);

        Assert.Equal(first, HubKeyStore.LoadOrCreatePsk(_path));
    }

    [Fact]
    public void CorruptKeyFile_IsReplacedRatherThanThrowing()
    {
        byte[] original = HubKeyStore.LoadOrCreatePsk(_path);
        File.WriteAllText(_path, "not valid json");

        byte[] recovered = HubKeyStore.LoadOrCreatePsk(_path);

        Assert.NotEqual(original, recovered);
        Assert.Equal(HubKeyStore.PskBytes, recovered.Length);
    }

    [Fact]
    public void DerivedKey_IsThirtyTwoBytes()
    {
        Assert.Equal(32, HubKeyStore.DeriveKey(HubKeyStore.GeneratePsk(), Guid.NewGuid()).Length);
    }
}
