package com.audiohub.protocol

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.UUID

/**
 * 发现客户端的本地环回测试：起一个假电脑，让客户端真的问、真的收。
 *
 * 黄金字节那条测试钉的是"报文长什么样"，这一组钉的是"一来一回能不能对上"——
 * 包括 token 对不上要丢、端口被占了要能退让，这些是字节层测试看不到的。
 */
class DiscoveryClientTest {

    private val hubId = UUID.fromString("78563412-8f2c-1d4e-5b6a-4c8d9e0f1a2b")

    @Test
    fun findsTheHubThatAnswers() {
        FakeHub(port = 0, hubId = hubId, name = "DESKTOP-TEST").use { hub ->
            hub.start()

            val found = DiscoveryClient(port = hub.port).probe(timeoutMs = 1_000L)

            assertEquals(1, found.size)
            assertEquals(hubId, found[0].hubId)
            assertEquals("DESKTOP-TEST", found[0].name)
            assertEquals(6_980, found[0].vbanPort)
            assertEquals(SecurityMode.OPTIONAL, found[0].securityMode)
        }
    }

    @Test
    fun ignoresRepliesCarryingADifferentToken() {
        // 回答时把 token 换掉：模拟"上一轮提问的迟到应答"。
        // 收下它会让界面把一台早就走了的电脑显示出来。
        FakeHub(port = 0, hubId = hubId, name = "STALE", tokenOverride = 12345).use { hub ->
            hub.start()

            assertTrue(DiscoveryClient(port = hub.port).probe(timeoutMs = 800L).isEmpty())
        }
    }

    @Test
    fun collapsesSeveralRepliesFromTheSameHub() {
        FakeHub(port = 0, hubId = hubId, name = "TWICE", repliesPerProbe = 3).use { hub ->
            hub.start()

            assertEquals(1, DiscoveryClient(port = hub.port).probe(timeoutMs = 1_000L).size)
        }
    }

    @Test
    fun fallsBackToAnEphemeralPortWhenTheUsualOneIsTaken() {
        // 电脑上 AudioHub 自己就听着发现端口。同一台机器上（比如用模拟器冒充手机）
        // 会撞上，所以客户端必须能退让到临时端口，而不是直接崩掉。
        FakeHub(port = 0, hubId = hubId, name = "FALLBACK").use { hub ->
            hub.start()

            // 把假电脑那个端口先占住，逼客户端退让。
            DatagramSocket(null).apply { reuseAddress = true; bind(InetSocketAddress(hub.port)) }.use {
                val found = DiscoveryClient(port = hub.port).probe(timeoutMs = 1_000L)

                // 应答是回到**提问的那个端口**（退让后的临时端口），不回被占的那个。
                // 能收到就说明退让之后应答照样回得来。
                assertEquals(1, found.size)
                assertEquals("FALLBACK", found[0].name)
            }
        }
    }

    @Test
    fun returnsEmptyRatherThanThrowingWhenNothingAnswers() {
        // 没人回答是常态（电脑没开），必须安静地返回空列表。
        // 抛异常会让"查找"按钮在最常见的场景下弹错误。
        assertTrue(DiscoveryClient(port = 6_979).probe(timeoutMs = 300L).isEmpty())
    }

    /** 一个只会应答的最小假电脑。 */
    private class FakeHub(
        port: Int,
        private val hubId: UUID,
        private val name: String,
        private val tokenOverride: Int? = null,
        private val repliesPerProbe: Int = 1,
    ) : AutoCloseable {

        // 绑通配地址而不是 127.0.0.1：客户端是往 255.255.255.255 广播的，
        // 只绑回环地址的套接字收不到广播，假电脑就会永远不回答。
        private val socket = DatagramSocket(null).apply {
            reuseAddress = true
            bind(InetSocketAddress(port))
        }
        private var thread: Thread? = null

        val port: Int get() = socket.localPort

        fun start() {
            val running = true
            thread = Thread {
                val buffer = ByteArray(DiscoveryProtocol.MAX_DATAGRAM_BYTES)
                while (running) {
                    val packet = DatagramPacket(buffer, buffer.size)
                    try {
                        socket.receive(packet)
                    } catch (ex: java.net.SocketException) {
                        return@Thread
                    }

                    val probe = DiscoveryProtocol.parse(packet.data.copyOfRange(0, packet.length))
                        as? DiscoveryMessage.Probe ?: continue
                    val token = tokenOverride ?: probe.token

                    val answer = DiscoveryProtocol.buildAnnounce(
                        token, hubId, 6_980, SecurityMode.OPTIONAL, name
                    )
                    repeat(repliesPerProbe) {
                        socket.send(DatagramPacket(answer, answer.size, packet.address, packet.port))
                    }
                }
            }.apply { isDaemon = true }
            thread!!.start()
        }

        override fun close() {
            socket.close()
        }
    }
}
