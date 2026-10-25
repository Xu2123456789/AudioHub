package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Test
import java.util.UUID

/**
 * 发现协议的字节层测试。
 *
 * 最重要的一条是 `announceMatchesTheGoldenBytesFromThePc`：那串字节是 C# 侧
 * `DiscoveryProtocolTests.Announce_LayoutIsByteForByteStable` 断言的同一串。
 * 两侧各自都能自洽地往返，但只有这一条能证明它们**互相**认得 ——
 * 否则每边各自改一点布局，两边测试都还是绿的，真机一连上就全废。
 */
class DiscoveryProtocolTest {
    private val sampleHubId = UUID.fromString("8f2c1d4e-5b6a-4c8d-9e0f-1a2b3c4d5e6f")

    /** 与 C# 侧同一组输入、同一串输出。 */
    private val goldenAnnounceHex =
        "41485542010200001B00785634128F2C1D4E5B6A4C8D9E0F1A2B3C4D5E6F441B0103687562"

    @Test
    fun announceMatchesTheGoldenBytesFromThePc() {
        val packet = DiscoveryProtocol.buildAnnounce(
            token = 0x12345678,
            hubId = sampleHubId,
            vbanPort = 6980,
            securityMode = SecurityMode.OPTIONAL,
            name = "hub",
        )
        assertEquals(goldenAnnounceHex, packet.joinToString("") { "%02X".format(it) })
    }

    @Test
    fun goldenBytesParseBackToTheSameValues() {
        val packet = hexToBytes(goldenAnnounceHex)
        val message = DiscoveryProtocol.parse(packet) as? DiscoveryMessage.Announce
        assertNotNull("黄金字节应当能被解析", message)
        assertEquals(0x12345678, message!!.token)
        assertEquals(sampleHubId, message.hubId)
        assertEquals(6980, message.vbanPort)
        assertEquals(SecurityMode.OPTIONAL, message.securityMode)
        assertEquals("hub", message.name)
    }

    @Test
    fun probeRoundTrips() {
        val message = DiscoveryProtocol.parse(DiscoveryProtocol.buildProbe(0xCAFEBABE.toInt()))
        assertNotNull(message)
        assertEquals(0xCAFEBABE.toInt(), (message as DiscoveryMessage.Probe).token)
    }

    @Test
    fun announceRoundTripsEveryField() {
        val message = DiscoveryProtocol.parse(
            DiscoveryProtocol.buildAnnounce(
                token = 1, hubId = sampleHubId, vbanPort = 6980,
                securityMode = SecurityMode.REQUIRED, name = "dell-g15",
            )
        ) as DiscoveryMessage.Announce

        assertEquals(1, message.token)
        assertEquals(sampleHubId, message.hubId)
        assertEquals(6980, message.vbanPort)
        assertEquals(SecurityMode.REQUIRED, message.securityMode)
        assertEquals("dell-g15", message.name)
    }

    @Test
    fun emptyNameRoundTrips() {
        val message = DiscoveryProtocol.parse(
            DiscoveryProtocol.buildAnnounce(0, sampleHubId, 6980, SecurityMode.NONE, "")
        ) as DiscoveryMessage.Announce
        assertEquals("", message.name)
    }

    @Test
    fun wrongMagicIsRejected() {
        val packet = DiscoveryProtocol.buildProbe(1)
        packet[0] = 0
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun wrongVersionIsRejected() {
        val packet = DiscoveryProtocol.buildProbe(1)
        packet[4] = 99
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun unknownTypeIsRejected() {
        val packet = DiscoveryProtocol.buildProbe(1)
        packet[5] = 0x7F
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun truncatedPacketIsRejected() {
        for (length in intArrayOf(0, 4, 9)) {
            assertNull(
                "截断到 $length 字节应当被拒",
                DiscoveryProtocol.parse(DiscoveryProtocol.buildProbe(1).copyOfRange(0, length))
            )
        }
    }

    @Test
    fun wrongDeclaredLengthIsRejected() {
        val packet = DiscoveryProtocol.buildProbe(1)
        packet[8] = 200.toByte()
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun oversizedPacketIsRejected() {
        val packet = ByteArray(DiscoveryProtocol.MAX_DATAGRAM_BYTES + 1)
        packet[0] = 0x41; packet[1] = 0x48; packet[2] = 0x55; packet[3] = 0x42
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun unknownSecurityModeIsRejected() {
        val packet = DiscoveryProtocol.buildAnnounce(0, sampleHubId, 6980, SecurityMode.NONE, "")
        packet[10 + 22] = 200.toByte()
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun wrongDeclaredNameLengthIsRejected() {
        val packet = DiscoveryProtocol.buildAnnounce(0, sampleHubId, 6980, SecurityMode.NONE, "abc")
        packet[10 + 23] = 42
        assertNull(DiscoveryProtocol.parse(packet))
    }

    @Test
    fun portIsSixteenBits() {
        val message = DiscoveryProtocol.parse(
            DiscoveryProtocol.buildAnnounce(0, sampleHubId, 65535, SecurityMode.NONE, "")
        ) as DiscoveryMessage.Announce
        assertEquals(65535, message.vbanPort)
    }

    private fun hexToBytes(hex: String): ByteArray {
        require(hex.length % 2 == 0)
        return ByteArray(hex.length / 2) { hex.substring(it * 2, it * 2 + 2).toInt(16).toByte() }
    }
}
