package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 六位短码的测试。
 *
 * 这里的黄金值由一份独立的 Python 实现算出（不是从 Kotlin 或 C# 里抄的），
 * 目的很具体：电脑端打印、手机端解码是两套实现，只要有一边偏移了一位，
 * 手机就会拨到一个**看起来合法但错误的地址**上去 —— 不会报错，只是没有声音。
 * 所以钉住常数，比"自己编码自己解码"有意义得多。
 */
class ShortCodeTest {

    @Test
    fun encodeMatchesThePythonGoldenVectors() {
        assertEquals("0M3RM2", ShortCode.encode("10.20.30.40"))
        assertEquals("80042R", ShortCode.encode("192.168.1.5"))
        assertEquals("G40C29", ShortCode.encode("172.20.3.4"))
        assertEquals("00000P", ShortCode.encode("10.0.0.1"))
        assertEquals("7ZZZZY", ShortCode.encode("10.255.255.255"))
    }

    @Test
    fun decodeReadsTheGoldenVectorsBack() {
        assertEquals("10.20.30.40", ShortCode.decode("0M3RM2"))
        assertEquals("192.168.1.5", ShortCode.decode("80042R"))
        assertEquals("172.20.3.4", ShortCode.decode("G40C29"))
    }

    @Test
    fun roundTripCoversEveryClass() {
        val addresses = listOf("10.1.2.3", "192.168.0.1", "172.16.0.1", "172.31.255.254", "10.0.0.1")

        for (address in addresses) {
            val code = ShortCode.encode(address)
            assertTrue("应能编码 $address", code != null)
            assertEquals(ShortCode.LENGTH, code!!.length)
            assertEquals(address, ShortCode.decode(code))
        }
    }

    @Test
    fun decodeToleratesCaseSeparatorsAndSpaces() {
        assertEquals("10.20.30.40", ShortCode.decode("0m3rm2"))
        assertEquals("10.20.30.40", ShortCode.decode("0M3-RM2"))
        assertEquals("10.20.30.40", ShortCode.decode(" 0m3 rm2 "))
    }

    @Test
    fun decodeFoldsTheCharactersCrockfordRemoves() {
        // 8OO42R 读作 80042R：O 因为像零而被字符集去掉。
        assertEquals("192.168.1.5", ShortCode.decode("8OO42R"))
    }

    @Test
    fun decodeRejectsASingleMistypedCharacter() {
        assertNull(ShortCode.decode("2PZZDJ"))
    }

    @Test
    fun decodeRejectsWrongLengthAndIllegalCharacters() {
        assertNull(ShortCode.decode("2PZZD"))
        assertNull(ShortCode.decode("0M3RM2X"))
        assertNull(ShortCode.decode("2PZZD!"))
        assertNull(ShortCode.decode(""))
        assertNull(ShortCode.decode(null))
    }

    @Test
    fun encodeRefusesAddressesTheLayoutCannotRepresent() {
        for (address in listOf("8.8.8.8", "1.1.1.1", "192.169.1.1", "172.32.1.1", "172.15.1.1", "100.64.0.1")) {
            assertNull("不该能编码 $address", ShortCode.encode(address))
        }
    }

    @Test
    fun encodeRefusesIpv6RatherThanTruncatingIt() {
        assertNull(ShortCode.encode("::1"))
        assertNull(ShortCode.encode("2001:db8::1"))
    }

    @Test
    fun carriesKeyFlagReflectsThePayload() {
        assertTrue(QrPayload("10.0.0.1", 6980, pairingKey = "ABCDE").carriesKey)
        assertFalse(QrPayload("10.0.0.1", 6980).carriesKey)
    }
}
