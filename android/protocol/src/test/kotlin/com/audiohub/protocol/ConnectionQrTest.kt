package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 二维码载荷的测试。格式是两端唯一共享的字符串，改一个字段名就是一次静默不兼容，
 * 所以这里既钉"生成出来的确切文本"，也钉"别人家的二维码必须被拒"。
 */
class ConnectionQrTest {

    private val hubId = "11111111-2222-3333-4444-555555555555"

    @Test
    fun buildRendersEveryFieldInTheDocumentedOrder() {
        val payload = QrPayload(
            host = "10.20.30.40",
            port = 6980,
            pairingKey = "ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2",
            hubId = hubId,
            fingerprint = "b321-0f13-afca-6cd2",
            name = "DESKTOP-1",
            securityMode = 2,
        )

        // 这一串是两端共享的黄金值：C# 的 ConnectionQrTests 断言同一串文本。
        // 字段顺序或名字改一个字符，就是一次静默不兼容。
        assertEquals(
            "audiohub://c/1?h=10.20.30.40&p=6980&k=ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2" +
                "&i=11111111-2222-3333-4444-555555555555&f=b321-0f13-afca-6cd2&n=DESKTOP-1&s=2",
            ConnectionQr.build(payload),
        )
    }

    @Test
    fun theSecurityModeTravelsWithTheCode() {
        // 没有这个字段时，扫码连上的手机会一律按"加密可选"处理：
        // 电脑关掉了加密、手机仍逐包封口，电脑那边连信封都没有，整包丢掉，两边零报错。
        for (mode in 0..2) {
            val parsed = ConnectionQr.parse(
                ConnectionQr.build(QrPayload("10.0.0.1", 6980, hubId = hubId, securityMode = mode)),
            )

            assertEquals(mode, parsed!!.securityMode)
        }
    }

    @Test
    fun anUnreadableSecurityModeIsTreatedAsAbsent() {
        // 认不出时宁可当"没带"，也不要默认成"加密可选" —— 那正是这条字段要修的故障。
        for (raw in listOf("3", "-1", "required", "")) {
            val parsed = ConnectionQr.parse("audiohub://c/1?h=10.0.0.1&p=6980&s=$raw")

            assertNull("s=$raw 不该被当成模式", parsed!!.securityMode)
        }
    }

    @Test
    fun buildOmitsTheKeyWhenTheUserTurnedItOff() {
        val text = ConnectionQr.build(QrPayload("192.168.1.5", 6980, name = "PC"))

        assertFalse(text.contains("k="))
        assertTrue(text.contains("h=192.168.1.5"))
        assertTrue(text.contains("n=PC"))
    }

    @Test
    fun parseRoundTripsAKeyAndANonAsciiName() {
        val original = QrPayload(
            host = "172.20.3.4",
            port = 7000,
            pairingKey = "ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2",
            hubId = hubId,
            fingerprint = "b321-0f13-afca-6cd2",
            name = "我的电脑",
        )

        val parsed = ConnectionQr.parse(ConnectionQr.build(original))

        assertEquals(original, parsed)
        assertTrue(parsed!!.carriesKey)
    }

    @Test
    fun parseAcceptsAPayloadWithoutTheOptionalFields() {
        val parsed = ConnectionQr.parse("audiohub://c/1?h=10.0.0.1&p=6980")

        assertEquals("10.0.0.1", parsed!!.host)
        assertEquals(6980, parsed.port)
        assertNull(parsed.pairingKey)
        assertNull(parsed.hubId)
        assertFalse(parsed.carriesKey)
    }

    @Test
    fun parseRejectsAnythingItCannotUseCompletely() {
        val bad = listOf(
            "https://example.com/c/1?h=10.0.0.1&p=6980",
            "audiohub://c/2?h=10.0.0.1&p=6980",
            "audiohub://other/1?h=10.0.0.1&p=6980",
            "audiohub://c/1?p=6980",
            "audiohub://c/1?h=not-an-ip&p=6980",
            "audiohub://c/1?h=::1&p=6980",
            "audiohub://c/1?h=10.0.0.1",
            "audiohub://c/1?h=10.0.0.1&p=0",
            "audiohub://c/1?h=10.0.0.1&p=70000",
            "不是二维码",
            "",
        )

        for (text in bad) {
            assertNull("不该接受：$text", ConnectionQr.parse(text))
        }
    }

    @Test
    fun parseIgnoresUnknownFieldsSoOlderPhonesKeepWorking() {
        val parsed = ConnectionQr.parse("audiohub://c/1?h=10.0.0.1&p=6980&z=future&n=PC")

        assertEquals("PC", parsed!!.name)
    }

    @Test
    fun parseKeepsTheFirstValueWhenAFieldRepeats() {
        val parsed = ConnectionQr.parse("audiohub://c/1?h=10.0.0.1&p=6980&h=10.0.0.2")

        assertEquals("10.0.0.1", parsed!!.host)
    }
}
