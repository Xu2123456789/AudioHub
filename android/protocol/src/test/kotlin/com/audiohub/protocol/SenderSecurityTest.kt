package com.audiohub.protocol

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.UUID

/**
 * 钉住"这次发送要不要加密"的判定。
 *
 * 这组断言存在的直接原因是一次真实故障：界面和服务各自实现了这条规则，
 * 对同一个组合（选了电脑、没输密钥、电脑是"加密可选"）给出了不同答案 ——
 * 界面认为这是正常的明文用法，服务认为那是参数错误，
 * 于是最普通的一条通路直接起不来。规则现在只写在一处，这组测试守住它。
 */
class SenderSecurityTest {
    private val hub = UUID.fromString("11111111-2222-3333-4444-555555555555")
    private val psk = ByteArray(16) { (it * 7 + 1).toByte() }

    @Test
    fun bothMissingMeansTheUserWantsPlaintext() {
        assertIs<SenderSecurity.Plaintext>(
            interpretSenderSecurity(null, null),
        )

        // 空字符串和空数组要和"没给"等价：Intent 取不到 extra 和取到空值
        // 在调用方看来是同一件事，不该得到两种待遇。
        assertIs<SenderSecurity.Plaintext>(
            interpretSenderSecurity(ByteArray(0), null),
        )
        assertIs<SenderSecurity.Plaintext>(
            interpretSenderSecurity(null, "   "),
        )
    }

    @Test
    fun bothPresentMeansEncrypted() {
        val encrypted = assertIs<SenderSecurity.Encrypted>(
            interpretSenderSecurity(psk, hub.toString()),
        )
        assertArrayEquals(psk, encrypted.psk)
        assertEquals(hub, encrypted.hubId)
    }

    @Test
    fun aHubIdWithoutAKeyIsRejectedRatherThanSentInTheClear() {
        // 这是那个真实故障的形状。它必须是 Rejected，不能是 Plaintext：
        // 调用方拿到 Plaintext 就会照常明文发出去，而在只收加密包的电脑上，
        // 那表现为"声音永远不来、双方都不报错"。
        val rejected = assertIs<SenderSecurity.Rejected>(
            interpretSenderSecurity(null, hub.toString()),
        )
        assertTrue("原因里要说清是缺密钥：${rejected.reason}", rejected.reason.contains("密钥"))
    }

    @Test
    fun aKeyWithoutAHubIdIsRejected() {
        val rejected = assertIs<SenderSecurity.Rejected>(
            interpretSenderSecurity(psk, null),
        )
        assertTrue("原因里要说清是缺身份：${rejected.reason}", rejected.reason.contains("身份"))
    }

    @Test
    fun anUnparsableHubIdIsRejectedWithTheOffendingText() {
        val rejected = assertIs<SenderSecurity.Rejected>(
            interpretSenderSecurity(psk, "not-a-uuid"),
        )
        assertTrue("要回显原文才算可诊断：${rejected.reason}", rejected.reason.contains("not-a-uuid"))
    }

    @Test
    fun surroundingWhitespaceOnTheHubIdIsTolerated() {
        val encrypted = assertIs<SenderSecurity.Encrypted>(
            interpretSenderSecurity(psk, "  $hub\n"),
        )
        assertEquals(hub, encrypted.hubId)
    }

    @Test
    fun anEmptyKeyIsTreatedAsNoKeyRatherThanAsAKey() {
        // 长度为零的密钥派生不出任何东西。把它当成"给了密钥"会让服务一路走到
        // HKDF 才炸，报出来的错和"没给密钥"完全一样但更难找。
        val rejected = assertIs<SenderSecurity.Rejected>(
            interpretSenderSecurity(ByteArray(0), hub.toString()),
        )
        assertTrue(rejected.reason.contains("密钥"))
    }

    private inline fun <reified T> assertIs(value: Any): T {
        assertTrue(
            "期望 ${T::class.java.simpleName}，实际 ${value.javaClass.simpleName}",
            value is T,
        )
        return value as T
    }
}
