package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

/**
 * 加密信封的测试。每条都对准一种"静悄悄地不安全"的可能：
 * 篡改只改一个字节也必须被抓、错密钥不能吐出半个有用的字节、nonce 绝不能重复。
 *
 * 其中 `nonceNeverRepeats` 最关键：GCM 下 nonce 重用不是变弱，是泄露认证子密钥。
 * 而它在日常使用里表现为**什么都没有** —— 声音照常响，只是已经不安全了。
 */
class SecureEnvelopeTest {
    private val hubId = UUID.fromString("8f2c1d4e-5b6a-4c8d-9e0f-1a2b3c4d5e6f")
    private val psk = SecureEnvelope.generatePsk()
    private val key = SecureEnvelope.deriveKey(psk, hubId)

    // 下面三个是"金色密封包"用的固定输入。它们和派生密钥那条测试用同一组 PSK / hub，
    // 这样两个锚点互相印证：密钥对得上、封出来的包也对得上。
    private val fixedPsk = ByteArray(16) { it.toByte() }
    private val fixedHub = UUID.fromString("78563412-8f2c-1d4e-5b6a-4c8d9e0f1a2b")
    private val fixedKey = SecureEnvelope.deriveKey(fixedPsk, fixedHub)
    private val fixedPlaintext = ByteArray(32) { (it * 11 + 1).toByte() }

    /** 见 [sealedPacketMatchesTheGoldenBytes]。 */
    private val goldenSealedHex = GOLDEN_SEALED_HEX

    @Test
    fun roundTripIsByteForByteIdentical() {
        val sender = SecureEnvelope.withSalt(key, 0x01020304)
        val receiver = SecureEnvelope.forReceiving(key)
        val original = ByteArray(1400) { (it * 7 + 3).toByte() }

        val opened = receiver.open(sender.seal(original))
        org.junit.Assert.assertNotNull(opened)
        org.junit.Assert.assertArrayEquals(original, opened)
    }

    @Test
    fun roundTripWorksAtAwkwardLengths() {
        for (length in intArrayOf(0, 1, 17, 255)) {
            val envelope = SecureEnvelope.forSending(key)
            val original = ByteArray(length) { (it * 7 + 3).toByte() }
            org.junit.Assert.assertArrayEquals(
                "长度 $length", original, envelope.open(envelope.seal(original))
            )
        }
    }

    @Test
    fun anySingleTamperedByteIsRejected() {
        val envelope = SecureEnvelope.forSending(key)
        val packet = envelope.seal(ByteArray(64) { it.toByte() })

        for (i in packet.indices) {
            val tampered = packet.copyOf()
            tampered[i] = (tampered[i].toInt() xor 0x01).toByte()
            assertNull("第 $i 个字节被改后仍被接受", envelope.open(tampered))
        }
    }

    @Test
    fun truncatedPacketIsRejected() {
        val envelope = SecureEnvelope.forSending(key)
        val packet = envelope.seal(ByteArray(64))
        assertNull(envelope.open(packet.copyOfRange(0, 30)))
        assertNull(envelope.open(packet.copyOfRange(0, packet.size - 1)))
    }

    @Test
    fun wrongMagicIsRejected() {
        val envelope = SecureEnvelope.forSending(key)
        val packet = envelope.seal(ByteArray(32))
        packet[0] = 0
        assertNull(envelope.open(packet))
    }

    @Test
    fun wrongVersionIsRejected() {
        val envelope = SecureEnvelope.forSending(key)
        val packet = envelope.seal(ByteArray(32))
        packet[4] = 99
        assertNull(envelope.open(packet))
    }

    @Test
    fun wrongKeyRevealsNothing() {
        val sender = SecureEnvelope.forSending(key)
        val stranger = SecureEnvelope.forReceiving(
            SecureEnvelope.deriveKey(SecureEnvelope.generatePsk(), hubId)
        )
        assertNull(stranger.open(sender.seal(ByteArray(64) { it.toByte() })))
    }

    @Test
    fun nonceNeverRepeats() {
        val sender = SecureEnvelope.withSalt(key, 0xDEADBEEF.toInt())
        val seen = HashSet<String>()
        repeat(2_000) {
            val packet = sender.seal(ByteArray(8))
            // nonce 就是明文头里那个 salt + 计数器。
            val nonce = packet.copyOfRange(6, 18).joinToString("") { "%02X".format(it) }
            assertTrue("第 $it 个包的 nonce 重复了", seen.add(nonce))
        }
        assertEquals(2_000L, sender.packetsSealed)
    }

    @Test
    fun separateSendersGetDifferentSalts() {
        assertNotEquals(
            SecureEnvelope.forSending(key).nonceSalt,
            SecureEnvelope.forSending(key).nonceSalt
        )
    }

    @Test
    fun samePlaintextSealsDifferentlyEachTime() {
        val envelope = SecureEnvelope.forSending(key)
        assertNotEquals(
            envelope.seal(ByteArray(32)).toList(),
            envelope.seal(ByteArray(32)).toList()
        )
    }

    @Test
    fun plainVbanPacketIsRecognisedAsNotEncrypted() {
        val envelope = SecureEnvelope.forReceiving(key)
        val vban = ByteArray(104).also {
            "VBAN".toByteArray(Charsets.US_ASCII).copyInto(it)
        }
        assertNull(envelope.open(vban))
    }

    @Test
    fun derivedKeyIsThirtyTwoBytesAndTiedToTheHub() {
        assertEquals(32, key.size)
        assertNotEquals(
            SecureEnvelope.deriveKey(psk, UUID.randomUUID()).toList(),
            SecureEnvelope.deriveKey(psk, UUID.randomUUID()).toList()
        )
        org.junit.Assert.assertArrayEquals(
            SecureEnvelope.deriveKey(psk, hubId),
            SecureEnvelope.deriveKey(psk, hubId)
        )
    }

    /**
     * 就地封装必须和便捷封装逐字节一致，否则发送路径和这条测试走的是两套代码，
     * 测试通过也证明不了真正发出去的包是对的。
     */
    @Test
    fun sealIntoMatchesSeal() {
        val plaintext = ByteArray(300) { (it * 5 + 1).toByte() }

        val viaSeal = SecureEnvelope.withSalt(key, 0x11223344).seal(plaintext)
        val buffer = ByteArray(plaintext.size + SecureEnvelope.OVERHEAD + 40)
        val written = SecureEnvelope.withSalt(key, 0x11223344).sealInto(plaintext, plaintext.size, buffer)

        assertEquals(viaSeal.size, written)
        org.junit.Assert.assertArrayEquals(viaSeal, buffer.copyOfRange(0, written))
        // 缓冲区后面的字节不能被动过，否则复用缓冲时会串包。
        org.junit.Assert.assertArrayEquals(
            ByteArray(buffer.size - written), buffer.copyOfRange(written, buffer.size)
        )
    }

    @Test
    fun sealIntoCountsUpExactlyLikeSeal() {
        val plaintext = ByteArray(64) { it.toByte() }
        val sender = SecureEnvelope.withSalt(key, 0x0A0B0C0D)
        val buffer = ByteArray(256)

        repeat(3) { sender.sealInto(plaintext, plaintext.size, buffer) }
        assertEquals(3, sender.packetsSealed)
    }

    /**
     * 证明手机封出来的包，电脑能原样解开的那个值。
     *
     * 密钥、salt、计数、明文全部固定，AES-GCM 就是确定性的，所以这个包可以写死。
     * 它锚定的是**信封的字节布局**（魔数、版本、类型、salt、计数、长度、标签的位置），
     * 和上面那条锚定 HKDF 的测试是两回事：少任何一条，都还可能出现
     * "两边各自算得都对、互相解不开"的静默故障。
     *
     * 电脑端 `AesGcmEnvelopeTests.Seal_MatchesThePacketPhonesProduce` 断言的是同一串。
     */
    @Test
    fun sealedPacketMatchesTheGoldenBytes() {
        val envelope = SecureEnvelope.withSalt(fixedKey, 0x01020304)
        val sealed = envelope.seal(fixedPlaintext)
        assertEquals(
            goldenSealedHex,
            sealed.joinToString("") { "%02X".format(it) }
        )
    }

    /**
     * 证明手机和电脑派生出同一把密钥的那个值。
     *
     * 两边各自独立派生，一旦不一致，故障是**静默**的：谁都不会报错，声音就是不来，
     * 日志看着跟网络断了完全一样。所以这个数字是写死的，不是现算的；
     * 电脑端 `HubKeyStoreTests.DeriveKey_MatchesTheValuePhonesDerive` 断言的是同一串。
     *
     * 它出自第三方实现（Python 手写 RFC 5869），不是这份代码自己跟自己对答案。
     * hubId 必须按大端写：.NET 的 `Guid.ToByteArray()` 返回 Windows 的混合端序，
     * 照它算就会和手机算出不同的密钥。
     */
    @Test
    fun derivedKeyMatchesTheGoldenValue() {
        val psk = ByteArray(16) { it.toByte() }
        val hub = UUID.fromString("78563412-8f2c-1d4e-5b6a-4c8d9e0f1a2b")
        assertEquals(
            "83B2A4A3CC5715E9646F5D417BBBD7633B09616FD7EF62287E94351D2EAEFF19",
            SecureEnvelope.deriveKey(psk, hub).joinToString("") { "%02X".format(it) }
        )
    }
}

/** 密钥的显示与输入 —— 这是整套设计里唯一要人动手的部分。 */
class PairingKeyTest {
    @Test
    fun roundTripSurvivesBeingTyped() {
        val psk = SecureEnvelope.generatePsk()
        val shown = SecureEnvelope.toDisplayString(psk)
        org.junit.Assert.assertArrayEquals(psk, SecureEnvelope.parse(shown))
    }

    @Test
    fun generatedKeyHasFullEntropy() {
        val psk = SecureEnvelope.generatePsk()
        assertEquals(SecureEnvelope.PSK_BYTES, psk.size)
        assertNotEquals(
            SecureEnvelope.generatePsk().toList(),
            SecureEnvelope.generatePsk().toList()
        )
    }

    @Test
    fun displayStringAvoidsCharactersPeopleConfuse() {
        val shown = SecureEnvelope.toDisplayString(SecureEnvelope.generatePsk())
        assertTrue(shown.contains("-"))
        assertTrue(shown.none { it in "ILOU" })
        assertTrue(shown.all { it.isDigit() || it.isUpperCase() || it == '-' })
    }

    @Test
    fun parseForgivesTheCharactersPeopleConfuse() {
        val psk = SecureEnvelope.generatePsk()
        val shown = SecureEnvelope.toDisplayString(psk)
        val asLetters = shown.replace('0', 'O').replace('1', 'I')

        org.junit.Assert.assertArrayEquals(psk, SecureEnvelope.parse(asLetters))
        org.junit.Assert.assertArrayEquals(psk, SecureEnvelope.parse(asLetters.lowercase()))
    }

    @Test
    fun parseRejectsGarbage() {
        for (input in listOf("", "   ", "!!!", "@@@@")) {
            assertNull("'$input' 应当被拒", SecureEnvelope.parse(input))
        }
    }
}

/**
 * 两侧共用的金色密封包（固定密钥 + salt=0x01020304 + 计数 1 + 32 字节明文）。
 *
 * 由本文件首次运行时产出后写死。javax.crypto 与 .NET 的 AesGcm 是两套独立实现，
 * 两边都断言这一串，就等于让两个实现互相验了一遍。
 */
private const val GOLDEN_SEALED_HEX =
    "4148533101010403020101000000000000003000D74E9970E65DED8E4E443243B82C60" +
        "BF16EED80441C7E62F9327B5C884FC22C2676B3C41EC8F258AEBAAE0DA71B1DB5E"
