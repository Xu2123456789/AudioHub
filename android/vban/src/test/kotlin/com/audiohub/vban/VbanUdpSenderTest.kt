package com.audiohub.vban

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.UnknownHostException

/**
 * UDP 本地环回：把 [VbanUdpSender] 真正发出去的字节收回来逐字节核对。
 *
 * 这一组测试回答的是"手机发出去的包，电脑认不认"这个问题——而且**不需要手机**，
 * 也不需要电脑端程序在跑，因为它验证的是线上字节本身，不是某一端的内部状态。
 *
 * 和 `VbanPacketWriterTest` 的分工：那边钉的是报文编码，这边钉的是"编码 + 发送"
 * 整条路，包括分片、计数递增、统计口径和静音判定。
 */
class VbanUdpSenderTest {

    // ---------------------------------------------------------------- 报文布局

    @Test
    fun `packets are accepted back on the loopback socket and carry the vban magic`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            assertEquals('V'.code.toByte(), packet[0])
            assertEquals('B'.code.toByte(), packet[1])
            assertEquals('A'.code.toByte(), packet[2])
            assertEquals('N'.code.toByte(), packet[3])
        }
    }

    @Test
    fun `sample rate is encoded as the 48000 index with an audio sub protocol`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            // 48000 在规范表里是索引 3；高 3 位子协议为 0（音频）。
            assertEquals(3, packet[4].toInt())
        }
    }

    @Test
    fun `frame and channel counts are stored as n minus one`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            // 存的是 n-1：255 表示 256 帧，1 表示 2 声道。
            assertEquals(255, packet[5].toInt() and 0xFF)
            assertEquals(1, packet[6].toInt())
        }
    }

    @Test
    fun `data type is int16 in the low three bits with a pcm codec up top`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            // 1 = 16 位整数，第 3 位保留必须为 0，编码器 0 = 原生 PCM。
            assertEquals(0x01, packet[7].toInt())
        }
    }

    @Test
    fun `stream name is nul padded into the sixteen byte field`() {
        withLoopback(streamName = "AudioHub") { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()
            val name = ByteArray(VbanPacketWriter.STREAM_NAME_LENGTH)
            System.arraycopy(packet, 8, name, 0, name.size)

            assertEquals("AudioHub", String(name, Charsets.US_ASCII).trimEnd('\u0000'))
            // 后面 8 个字节必须是 NUL 填充，不是残留数据。
            for (i in 8 until VbanPacketWriter.STREAM_NAME_LENGTH) {
                assertEquals(0, name[i].toInt())
            }
        }
    }

    @Test
    fun `sequence counter starts at zero and increments per packet`() {
        withLoopback { sender, receiver ->
            // 4 个包：256 + 256 + 256 + 232 帧。
            sender.send(tone(frameCount = 1000, amplitude = 8192), 0, 1000 * BYTES_PER_FRAME)

            val counters = ArrayList<Int>()
            repeat(4) {
                val packet = receiver.receiveOne()
                counters.add(
                    (packet[24].toInt() and 0xFF) or
                        ((packet[25].toInt() and 0xFF) shl 8) or
                        ((packet[26].toInt() and 0xFF) shl 16) or
                        ((packet[27].toInt() and 0xFF) shl 24),
                )
            }

            assertEquals(listOf(0, 1, 2, 3), counters)
        }
    }

    // ---------------------------------------------------------------- 载荷与分片

    @Test
    fun `payload is byte identical to the source`() {
        val pcm = tone(frameCount = 256, amplitude = 12345)

        withLoopback { sender, receiver ->
            sender.send(pcm, 0, pcm.size)

            val packet = receiver.receiveOne()
            val payload = packet.copyOfRange(VbanPacketWriter.HEADER_SIZE, packet.size)

            // 逐字节相同，意味着不存在字节序、符号或缩放上的任何改写。
            assertArrayEquals(pcm, payload)
        }
    }

    @Test
    fun `a payload larger than one packet is split into full packets`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 1000, amplitude = 8192), 0, 1000 * BYTES_PER_FRAME)

            val sizes = (0 until 4).map { receiver.receiveOne().size }

            // 三个满包（256 帧）加一个收尾包（232 帧）。
            assertEquals(
                listOf(
                    VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME,
                    VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME,
                    VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME,
                    VbanPacketWriter.HEADER_SIZE + 232 * BYTES_PER_FRAME,
                ),
                sizes,
            )
        }
    }

    @Test
    fun `no packet ever exceeds the vban frame limit`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 1000, amplitude = 8192), 0, 1000 * BYTES_PER_FRAME)

            repeat(4) {
                val packet = receiver.receiveOne()
                val frames = (packet[5].toInt() and 0xFF) + 1
                assertTrue("帧数 $frames 超过了单包上限", frames <= VbanPacketWriter.MAX_FRAMES_PER_PACKET)
            }
        }
    }

    @Test
    fun `a trailing partial frame is dropped rather than sent as a malformed packet`() {
        val pcm = tone(frameCount = 256, amplitude = 8192)

        withLoopback { sender, receiver ->
            // 多给 3 个字节 —— 不到一整帧（4 字节），必须被丢掉。
            sender.send(pcm, 0, pcm.size + 3)

            val packet = receiver.receiveOne()
            assertEquals(VbanPacketWriter.HEADER_SIZE + pcm.size, packet.size)
            assertEquals("只应该产生一个包", 1L, sender.packetsSent)

            // 后面不应该还有第二个包。
            receiver.soTimeout = 300
            assertNull(receiver.receiveOrNull())
        }
    }

    @Test
    fun `an offset skips leading bytes exactly`() {
        val pcm = tone(frameCount = 512, amplitude = 4321)

        withLoopback { sender, receiver ->
            // 从第 256 帧开始，只发后 256 帧。
            sender.send(pcm, 256 * BYTES_PER_FRAME, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()
            val payload = packet.copyOfRange(VbanPacketWriter.HEADER_SIZE, packet.size)

            assertArrayEquals(pcm.copyOfRange(256 * BYTES_PER_FRAME, pcm.size), payload)
        }
    }

    // ---------------------------------------------------------------- 统计口径

    @Test
    fun `statistics count packets frames and bytes`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 512, amplitude = 8192), 0, 512 * BYTES_PER_FRAME)

            assertEquals(2L, sender.packetsSent)
            assertEquals(512L, sender.framesSent)
            // 每个包 = 28 字节头 + 256 帧 × 4 字节。
            assertEquals(2L * (VbanPacketWriter.HEADER_SIZE + 1024), sender.bytesSent)
            assertNull(sender.lastError)

            receiver.receiveOne()
            receiver.receiveOne()
        }
    }

    @Test
    fun `peak tracks the largest absolute sample`() {
        withLoopback { sender, _ ->
            sender.send(tone(frameCount = 256, amplitude = 20000), 0, 256 * BYTES_PER_FRAME)

            assertEquals(20000, sender.peakAmplitude)
        }
    }

    @Test
    fun `peak is the magnitude so negative samples count`() {
        val pcm = int16Buffer(frameCount = 8, channels = 2, amplitude = -30000)

        withLoopback { sender, _ ->
            sender.send(pcm, 0, pcm.size)

            // -30000 的绝对值是 30000；写成 ==-30000 就说明符号处理错了。
            assertEquals(30000, sender.peakAmplitude)
        }
    }

    @Test
    fun `an all zero buffer counts as silence`() {
        withLoopback { sender, _ ->
            sender.send(int16Buffer(frameCount = 256, channels = 2, amplitude = 0), 0, 256 * BYTES_PER_FRAME)

            assertEquals(1L, sender.packetsSent)
            assertEquals("全零不该算作含声音", 0L, sender.nonSilentPackets)
            assertEquals(0, sender.peakAmplitude)
            assertNull("没有信号时 dBFS 应为 null", VbanUdpSender.amplitudeToDbfs(sender.peakAmplitude))
        }
    }

    @Test
    fun `a signal above the silence threshold counts as content`() {
        withLoopback { sender, _ ->
            sender.send(tone(frameCount = 256, amplitude = 1000), 0, 256 * BYTES_PER_FRAME)

            assertEquals(1L, sender.nonSilentPackets)
        }
    }

    @Test
    fun `dbfs conversion puts full scale at zero`() {
        assertEquals(0.0, VbanUdpSender.amplitudeToDbfs(32768)!!, 0.001)
        // 半量程是 -6.02 dB。
        assertEquals(-6.02, VbanUdpSender.amplitudeToDbfs(16384)!!, 0.01)
    }

    // ---------------------------------------------------------------- 目标与失败

    @Test
    fun `target description reports the resolved host and port`() {
        withLoopback { sender, receiver ->
            assertEquals("127.0.0.1:${receiver.localPort}", sender.targetDescription)
        }
    }

    @Test(expected = UnknownHostException::class)
    fun `an unresolvable host fails immediately instead of silently dropping audio`() {
        // .invalid 是保留后缀，永远不解析。这里要的就是"当场抛错"，
        // 而不是每包静默失败——静默失败会让现场排查变成猜谜。
        VbanUdpSender("audiohub-does-not-exist.invalid", 6980, "AudioHub", 48_000, 2)
    }

    // ---------------------------------------------------------------- 加密接缝

    @Test
    fun `without a sealer the bytes are exactly the plain vban packet`() {
        withLoopback { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            assertEquals(VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME, packet.size)
            // 不加密时"报文长度"和"上线长度"必须相等，否则说明接缝偷偷改了东西。
            assertEquals(sender.bytesSent, sender.bytesOnWire)
        }
    }

    /**
     * 查封缝有没有拿到**完整的** VBAN 报文。
     *
     * 这一点决定加密有没有真的盖住头：如果只把音频载荷交给加密层，
     * 采样率、声道数、流名称仍然是明文的，别人照样能看出你在听什么、什么格式。
     */
    @Test
    fun `the sealer receives the complete packet including the vban header`() {
        var seenSize = -1
        val recorder = PacketSealer { source, size, destination ->
            seenSize = size
            source.copyInto(destination, MARKER.size, 0, size)
            MARKER.copyInto(destination)
            MARKER.size + size
        }

        withLoopback(sealer = recorder) { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)

            val packet = receiver.receiveOne()

            assertEquals(VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME, seenSize)
            // 发出去的是封好的整包：前面是标记，后面原封不动是 VBAN 报文（含它的头）。
            assertEquals(MARKER.size + seenSize, packet.size)
            assertArrayEquals(MARKER, packet.copyOfRange(0, MARKER.size))
            assertEquals('V'.code.toByte(), packet[MARKER.size])
        }
    }

    @Test
    fun `encryption overhead shows up on the wire but not in the audio byte count`() {
        val padder = PacketSealer { source, size, destination ->
            source.copyInto(destination, MARKER.size, 0, size)
            MARKER.copyInto(destination)
            MARKER.size + size
        }

        withLoopback(sealer = padder) { sender, receiver ->
            sender.send(tone(frameCount = 256, amplitude = 8192), 0, 256 * BYTES_PER_FRAME)
            receiver.receiveOne()

            val plain = VbanPacketWriter.HEADER_SIZE + 256 * BYTES_PER_FRAME
            assertEquals(plain.toLong(), sender.bytesSent)
            assertEquals((plain + MARKER.size).toLong(), sender.bytesOnWire)
        }
    }

    // ---------------------------------------------------------------- 工具

    private fun <T> withLoopback(
        streamName: String = "AudioHub",
        sampleRate: Int = 48_000,
        channels: Int = 2,
        sealer: PacketSealer? = null,
        block: (VbanUdpSender, DatagramSocket) -> T,
    ): T {
        DatagramSocket(0, InetAddress.getByName("127.0.0.1")).use { receiver ->
            receiver.soTimeout = 2_000
            VbanUdpSender(
                "127.0.0.1",
                receiver.localPort,
                streamName,
                sampleRate,
                channels,
                sealer,
            ).use { sender ->
                return block(sender, receiver)
            }
        }
    }

    private fun DatagramSocket.receiveOne(): ByteArray {
        val packet = receiveOrNull()
        assertNotNull("2 秒内没有收到任何报文", packet)
        return packet!!
    }

    private fun DatagramSocket.receiveOrNull(): ByteArray? {
        val buffer = ByteArray(VbanPacketWriter.MAX_PACKET_BYTES + 64)
        val packet = DatagramPacket(buffer, buffer.size)
        return try {
            receive(packet)
            buffer.copyOfRange(packet.offset, packet.offset + packet.length)
        } catch (ex: java.net.SocketTimeoutException) {
            null
        }
    }

    /** 交替正负的方波，幅度固定。核对方便，也保证能跨过静音阈值。 */
    private fun tone(frameCount: Int, amplitude: Int): ByteArray =
        int16Buffer(frameCount, channels = 2, amplitude = amplitude)

    private fun int16Buffer(frameCount: Int, channels: Int, amplitude: Int): ByteArray {
        val bytes = ByteArray(frameCount * channels * 2)
        var i = 0
        for (frame in 0 until frameCount) {
            for (channel in 0 until channels) {
                val sample = if ((frame + channel) % 2 == 0) amplitude else -amplitude
                bytes[i++] = (sample and 0xFF).toByte()
                bytes[i++] = ((sample shr 8) and 0xFF).toByte()
            }
        }
        return bytes
    }

    private companion object {
        const val BYTES_PER_FRAME = 2 * 2

        /** 假密封器加在报文前面的标记，用来确认"发的是密封器的输出"。 */
        val MARKER = byteArrayOf(0x41, 0x48, 0x53, 0x31)
    }
}
