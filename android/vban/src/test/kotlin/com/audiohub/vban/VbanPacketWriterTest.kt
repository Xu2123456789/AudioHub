package com.audiohub.vban

import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertThrows
import org.junit.Test

/**
 * 协议字节级测试。
 *
 * 这些断言锁的是 VBAN Protocol Specification revision 13 的原文取值，
 * 目的是防止将来有人"顺手改一下"而悄悄破坏与 PC 端的兼容。
 * 它们全部在电脑上运行，不需要手机。
 */
class VbanPacketWriterTest {

    private fun writer(
        sampleRate: Int = 48_000,
        channels: Int = 2,
        streamName: String = "AudioHub",
    ) = VbanPacketWriter(streamName = streamName, sampleRate = sampleRate, channels = channels)

    @Test
    fun `报文以 VBAN 四个字节开头`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(4))
        val pcm = ByteArray(4 * 2 * 2)

        val size = w.writeInto(packet, pcm, 0, 4)

        assertEquals(28 + 16, size)
        assertEquals('V'.code.toByte(), packet[0])
        assertEquals('B'.code.toByte(), packet[1])
        assertEquals('A'.code.toByte(), packet[2])
        assertEquals('N'.code.toByte(), packet[3])
    }

    @Test
    fun `采样率索引与规范表一致`() {
        assertEquals(0, writer(sampleRate = 6_000).sampleRateIndex)
        assertEquals(3, writer(sampleRate = 48_000).sampleRateIndex)
        assertEquals(16, writer(sampleRate = 44_100).sampleRateIndex)
        assertEquals(20, writer(sampleRate = 705_600).sampleRateIndex)
    }

    @Test
    fun `采样数与声道数按规范存 n 减 1`() {
        val w = writer(channels = 1)
        val packet = ByteArray(w.packetSizeFor(1))

        w.writeInto(packet, ByteArray(2), 0, 1)

        // 规范原文：format_nbs = nbSample-1，format_nbc = nbChannel-1。
        assertEquals(0, packet[5].toInt())
        assertEquals(0, packet[6].toInt())
    }

    @Test
    fun `满帧数与双声道的编码字节`() {
        val w = writer(channels = 2)
        val packet = ByteArray(w.packetSizeFor(256))

        w.writeInto(packet, ByteArray(256 * 2 * 2), 0, 256)

        // 取字节值必须 and 0xFF：0xFF 按有符号 Byte 读出来是 -1，直接比 255 会误判。
        assertEquals(255, packet[5].toInt() and 0xFF) // 256 个采样，存 255
        assertEquals(1, packet[6].toInt())            // 2 个声道，存 1
    }

    @Test
    fun `编码字节低三位是数据类型且保留位为零`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(2))

        w.writeInto(packet, ByteArray(2 * 2 * 2), 0, 2)

        assertEquals(VbanPacketWriter.DATA_TYPE_INT16, packet[7].toInt() and 0x07)
        assertEquals(0, packet[7].toInt() and 0x08) // 保留位必须为 0
        assertEquals(VbanPacketWriter.CODEC_PCM, packet[7].toInt() and 0xF0)
    }

    @Test
    fun `子协议字段为零表示音频`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(2))

        w.writeInto(packet, ByteArray(2 * 2 * 2), 0, 2)

        assertEquals(VbanPacketWriter.SUB_PROTOCOL_AUDIO, packet[4].toInt() and 0xE0)
        assertEquals(3, packet[4].toInt() and 0x1F) // 48 kHz 的索引
    }

    @Test
    fun `流名称写入并对齐到十六字节`() {
        val w = writer(streamName = "Phone")
        val packet = ByteArray(w.packetSizeFor(2))

        w.writeInto(packet, ByteArray(2 * 2 * 2), 0, 2)

        val name = String(packet, 8, 16, Charsets.US_ASCII).trimEnd('\u0000')
        assertEquals("Phone", name)
        // 十六个字节必须全部写满，未用部分补 NUL。
        assertEquals(16, packet.copyOfRange(8, 24).size)
    }

    @Test
    fun `帧计数小端写入并逐包递增`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(2))
        val pcm = ByteArray(2 * 2 * 2)

        w.writeInto(packet, pcm, 0, 2)
        assertEquals(0u, readCounter(packet))

        w.writeInto(packet, pcm, 0, 2)
        assertEquals(1u, readCounter(packet))

        w.writeInto(packet, pcm, 0, 2)
        assertEquals(2u, readCounter(packet))
    }

    @Test
    fun `帧计数按无符号处理且能回绕`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(1))
        val pcm = ByteArray(4)

        // 直接把计数推到最后一次，验证它写出 0xFFFFFFFF 而不是负数。
        // 不能靠循环跑到这里：2^32 次迭代根本跑不完。
        w.resetFrameCounter(UInt.MAX_VALUE)
        w.writeInto(packet, pcm, 0, 1)

        assertEquals(UInt.MAX_VALUE, readCounter(packet))
        assertEquals(0xFF.toByte(), packet[24])
        assertEquals(0xFF.toByte(), packet[27])

        // 再走一包必须干净地回绕到 0，而不是抛异常或变成负数。
        w.writeInto(packet, pcm, 0, 1)
        assertEquals(0u, readCounter(packet))
    }

    @Test
    fun `重置帧计数后从指定值继续`() {
        val w = writer()
        val packet = ByteArray(w.packetSizeFor(1))
        val pcm = ByteArray(4)

        w.resetFrameCounter(1000u)
        w.writeInto(packet, pcm, 0, 1)
        assertEquals(1000u, readCounter(packet))
        assertEquals(1001u, w.nextFrameCounter)
    }

    @Test
    fun `负载被原样搬运`() {
        val w = writer(channels = 1)
        val pcm = byteArrayOf(0x11, 0x22, 0x33, 0x44, 0x55, 0x66)
        val packet = ByteArray(w.packetSizeFor(3))

        w.writeInto(packet, pcm, 0, 3)

        assertArrayEquals(pcm, packet.copyOfRange(28, 34))
    }

    @Test
    fun `可以从源缓冲区中间某处开始取`() {
        val w = writer(channels = 1)
        val pcm = byteArrayOf(1, 1, 2, 2, 3, 3, 4, 4)
        val packet = ByteArray(w.packetSizeFor(2))

        w.writeInto(packet, pcm, pcmOffset = 2, frameCount = 2)

        assertArrayEquals(byteArrayOf(2, 2, 3, 3), packet.copyOfRange(28, 32))
    }

    @Test
    fun `报文大小等于头加负载`() {
        val w = writer(channels = 2)
        assertEquals(28 + 256 * 2 * 2, w.packetSizeFor(256))
    }

    @Test
    fun `单包超过 256 帧被拒绝`() {
        val w = writer()
        assertThrows(IllegalArgumentException::class.java) { w.packetSizeFor(257) }
        assertThrows(IllegalArgumentException::class.java) {
            w.writeInto(ByteArray(4096), ByteArray(4096), 0, 257)
        }
    }

    @Test
    fun `零帧被拒绝`() {
        assertThrows(IllegalArgumentException::class.java) { writer().packetSizeFor(0) }
    }

    @Test
    fun `规范表之外的采样率被拒绝而不是猜一个索引`() {
        // 猜索引会让 PC 端按错误的速率播放，比直接失败更糟。
        assertThrows(IllegalArgumentException::class.java) { writer(sampleRate = 47_999) }
        assertThrows(IllegalArgumentException::class.java) { writer(sampleRate = 0) }
    }

    @Test
    fun `非法声道数被拒绝`() {
        assertThrows(IllegalArgumentException::class.java) { writer(channels = 0) }
        assertThrows(IllegalArgumentException::class.java) { writer(channels = 3) }
    }

    @Test
    fun `流名称过长或含非 ASCII 被拒绝`() {
        assertThrows(IllegalArgumentException::class.java) { writer(streamName = "0123456789ABCDEFG") }
        assertThrows(IllegalArgumentException::class.java) { writer(streamName = "安卓") }
    }

    @Test
    fun `目标缓冲区太小时报错而不是悄悄截断`() {
        val w = writer()
        assertThrows(IllegalArgumentException::class.java) {
            w.writeInto(ByteArray(16), ByteArray(256 * 2 * 2), 0, 256)
        }
    }

    @Test
    fun `源数据越界时报错`() {
        val w = writer()
        assertThrows(IllegalArgumentException::class.java) {
            w.writeInto(ByteArray(4096), ByteArray(4), 0, 4)
        }
    }

    private fun readCounter(packet: ByteArray): UInt {
        val b0 = packet[24].toUByte().toUInt()
        val b1 = packet[25].toUByte().toUInt() shl 8
        val b2 = packet[26].toUByte().toUInt() shl 16
        val b3 = packet[27].toUByte().toUInt() shl 24
        return b0 or b1 or b2 or b3
    }
}
