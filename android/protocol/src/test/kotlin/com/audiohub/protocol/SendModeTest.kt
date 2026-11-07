package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * 钉住"电脑要求什么、我有没有条件满足"这张判定表。
 *
 * 这组测试守的是一个会把人骗过去的形态：电脑设成"仅加密"，用户在手机上扫了一张
 * **不带密钥**的二维码（或输了反查不到配对记录的短码），界面把本机模式重置成
 * NONE，于是显示"链路：明文发送"。用户以为没问题就点开始，而电脑把每一个包都拒掉 ——
 * 双方都不报错，声音永远不来。
 */
class SendModeTest {
    private val none = SecurityMode.NONE
    private val optional = SecurityMode.OPTIONAL
    private val required = SecurityMode.REQUIRED

    @Test
    fun bothKeyAndIdentityMeansEncrypted() {
        // 电脑关闭加密的情形另有一条测试（不能覆盖电脑的说法），所以这里不含 NONE。
        for (advertised in listOf(optional, required, null)) {
            val plan = resolveSendMode(advertised, hasKey = true, hasIdentity = true)

            assertTrue("advertised=$advertised 应该加密", plan.encrypt)
            assertTrue(plan.canStart)
            assertEquals(
                "advertised=$advertised",
                SendModeSituation.Encrypted,
                plan.situation,
            )
        }
    }

    @Test
    fun aHubThatTurnedEncryptionOffDoesNotGetSealedPackets() {
        // 只收明文的电脑会把密文当作读不懂的包整个丢掉，两边都不报错。
        // 所以"手上有密钥"不能覆盖"电脑关掉了加密"。
        val plan = resolveSendMode(none, hasKey = true, hasIdentity = true)

        assertEquals(none, plan.advertised)
        assertFalse(plan.encrypt)
        assertEquals(SendModeSituation.PlaintextOnly, plan.situation)
    }

    @Test
    fun requiredWithoutAKeyIsBlockedAndSaysSo() {
        val plan = resolveSendMode(required, hasKey = false, hasIdentity = true)

        assertFalse(plan.encrypt)
        assertFalse("必须拦下，不能悄悄退成明文", plan.canStart)
        assertEquals(SendModeSituation.MissingKey, plan.situation)

        // 电脑的原话必须留着，界面才有办法说"这台电脑要求加密"。
        assertEquals(required, plan.advertised)
    }

    @Test
    fun requiredWithoutAnIdentityIsADifferentProblemFromMissingAKey() {
        // 没走发现、短码也反查不到：不知道该派生给哪台电脑。
        // 这和"没填密钥"补的办法完全不同，所以不能合成一种情况。
        val noIdentity = resolveSendMode(required, hasKey = false, hasIdentity = false)
        assertEquals(SendModeSituation.MissingIdentity, noIdentity.situation)
        assertFalse(noIdentity.canStart)

        // 连密钥都有，仍然加不了密 —— 派生要用身份，两条缺一条都不成立。
        val keyButNoIdentity = resolveSendMode(required, hasKey = true, hasIdentity = false)
        assertEquals(SendModeSituation.MissingIdentity, keyButNoIdentity.situation)
        assertFalse(keyButNoIdentity.encrypt)
        assertFalse(keyButNoIdentity.canStart)
    }

    @Test
    fun optionalWithoutAKeyIsNormalPlaintextNotAnError() {
        val plan = resolveSendMode(optional, hasKey = false, hasIdentity = true)

        assertFalse(plan.encrypt)
        assertTrue("这是正常用法，不该拦", plan.canStart)
        assertEquals(SendModeSituation.PlaintextOptional, plan.situation)
    }

    @Test
    fun anUnheardHubIsTreatedAsOptionalSoPlaintextStillWorks() {
        // 还没听到广播 = 不知道。保守取值是"加密可选"：明文能通，
        // 有密钥时照样加密，不因为没听到就白丢一次加密机会。
        val withKey = resolveSendMode(null, hasKey = true, hasIdentity = true)
        assertTrue(withKey.encrypt)
        assertEquals(optional, withKey.advertised)

        val withoutKey = resolveSendMode(null, hasKey = false, hasIdentity = true)
        assertFalse(withoutKey.encrypt)
        assertEquals(SendModeSituation.PlaintextOptional, withoutKey.situation)
    }

    @Test
    fun aHandTypedAddressOnlyEverGoesPlaintext() {
        // 不知道对方是谁：既没法验证、也没法派生，只能明文。
        for (hasKey in listOf(true, false)) {
            val plan = resolveSendMode(optional, hasKey = hasKey, hasIdentity = false)

            assertFalse(plan.encrypt)
            assertTrue(plan.canStart)
            assertEquals(SendModeSituation.UnknownHub, plan.situation)
        }
    }

    @Test
    fun theComputersOwnWordsAreNeverRewrittenAsPlaintext() {
        // 这条是那个故障的直接反证：无论缺什么，advertised 都不能被改写成 NONE，
        // 否则界面就会把"要求加密"说成"明文发送"。
        for (hasKey in listOf(true, false)) {
            for (hasIdentity in listOf(true, false)) {
                val plan = resolveSendMode(required, hasKey, hasIdentity)
                assertEquals(
                    "hasKey=$hasKey hasIdentity=$hasIdentity",
                    required,
                    plan.advertised,
                )
            }
        }
    }

    @Test
    fun theDecisionReachesTheWireInsteadOfOnlyTheLabel() {
        // 上面那条只钉住了"判定对不对"。真实故障里判定是对的、文案也是对的，
        // 错在**发包动作没读结论**，所以这里把链子一路走到字节：
        //   resolveSendMode → senderCredentialsFor → interpretSenderSecurity
        // 中间那一步的产物，就是发送端真正读到的两条 extra。
        val key = ByteArray(16) { it.toByte() }
        val hub = "11111111-2222-3333-4444-555555555555"

        // 电脑关闭加密、手机还存着密钥：界面说"明文"，线上也必须是明文。
        val off = resolveSendMode(none, hasKey = true, hasIdentity = true)
        val offWire = senderCredentialsFor(off, hub, key)

        assertFalse("电脑关掉加密时不许封包", offWire.encrypts)
        assertNull(offWire.hubIdText)
        assertNull(offWire.psk)
        assertTrue(
            "发送端读到的必须是明文，而不是'界面上写着明文'",
            interpretSenderSecurity(offWire.psk, offWire.hubIdText) is SenderSecurity.Plaintext,
        )

        // 电脑要求加密且条件齐备：结论必须同样一路走到封包。
        val on = resolveSendMode(required, hasKey = true, hasIdentity = true)
        val onWire = senderCredentialsFor(on, hub, key)

        assertTrue(onWire.encrypts)
        assertTrue(
            interpretSenderSecurity(onWire.psk, onWire.hubIdText) is SenderSecurity.Encrypted,
        )
    }

    @Test
    fun credentialsAreWithheldWhenEitherHalfIsMissing() {
        // 派生密钥要用身份和密钥两样。缺一样就只能明文（界面上已说明原因），
        // 绝不能只传一半 —— 只传身份会被发送端判成"参数不成立"，整条通路起不来。
        val key = ByteArray(16)
        val hub = "11111111-2222-3333-4444-555555555555"
        val encrypting = resolveSendMode(required, hasKey = true, hasIdentity = true)

        val cases = listOf(
            senderCredentialsFor(encrypting, null, key),
            senderCredentialsFor(encrypting, hub, null),
            senderCredentialsFor(encrypting, hub, ByteArray(0)),
        )

        for (creds in cases) {
            assertFalse(creds.encrypts)
            assertNull(creds.hubIdText)
            assertNull(creds.psk)
        }
    }
}
