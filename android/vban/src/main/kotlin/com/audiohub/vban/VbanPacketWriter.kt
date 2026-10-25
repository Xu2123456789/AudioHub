package com.audiohub.vban

/**
 * 把 PCM 采样打成 VBAN 音频报文。
 *
 * 字段布局取自 VBAN Protocol Specification revision 13（28 字节头，小端）：
 * 四字节 "VBAN"、一字节（低 5 位采样率索引 + 高 3 位子协议）、
 * 一字节（采样数 - 1）、一字节（声道数 - 1）、一字节（低 3 位数据类型 + 高 4 位编码器）、
 * 十六字节流名称、四字节递增帧计数。
 *
 * 两个容易写错的地方，这里都按规范做死了：
 * - 采样数与声道数都是**存 n-1**，所以字节值 0 表示 1。
 * - 帧计数是 32 位无符号、小端、逐包递增；回绕由无符号运算自然处理。
 *
 * 本类可以被同一个实例反复调用（复用同一块缓冲区），不要在音频线程里分配对象。
 */
class VbanPacketWriter(
    private val streamName: String,
    private val sampleRate: Int,
    private val channels: Int,
) {
    init {
        require(channels in 1..2) { "声道数只能是 1 或 2，收到 $channels" }
        require(sampleRate in SAMPLE_RATES) {
            "采样率 $sampleRate Hz 不在 VBAN 规范定义的索引表里；受支持的是 $SAMPLE_RATES"
        }
        require(streamName.length <= STREAM_NAME_LENGTH) {
            "流名称最多 $STREAM_NAME_LENGTH 个字符，收到 ${streamName.length}"
        }
        require(streamName.all { it.code in 32..126 }) { "流名称必须是可打印 ASCII，收到 '$streamName'" }
    }

    // 下面这两个必须写成"带初始值的声明"，不能在 init 块里赋值：
    // Kotlin 不允许在属性的声明位置之前对它赋值，而 init 块在它们前面。

    /** 该采样率在规范表里的索引。 */
    val sampleRateIndex: Int = SAMPLE_RATES.indexOf(sampleRate)

    /** 每个音频帧（所有声道合计）占用的字节数。 */
    val payloadBytesPerFrame: Int = channels * BYTES_PER_SAMPLE

    /** 已经写出的包数，也就是下一个包要用的帧计数再 +1 之前的值。 */
    var packetsWritten: Long = 0
        private set

    /** 下一个包会使用的帧计数。 */
    val nextFrameCounter: UInt
        get() = packetsWritten.toUInt()

    /**
     * 把帧计数设到指定值，下一个包就用它。
     *
     * 用途是"从中途接续"：发送端重连时，既可以从 0 重新开始，也可以接着上次的数继续。
     * 接收端靠这个数判断丢包，所以这个选择是有意义的，不是可有可无的开关。
     * 顺带也让"回绕"这件事可以在常数时间内被测试到——真跑 2^32 次是不现实的。
     */
    fun resetFrameCounter(value: UInt) {
        packetsWritten = value.toLong()
    }

    /**
     * 一个包最多能装多少音频帧。规范把上限定为 256，超过这个数就不是一个合法的 VBAN 音频包了。
     */
    val maxFramesPerPacket: Int = MAX_FRAMES_PER_PACKET

    /** 装 [frameCount] 个音频帧所需的完整报文大小（含头）。 */
    fun packetSizeFor(frameCount: Int): Int {
        requireFrameCount(frameCount)
        return HEADER_SIZE + frameCount * payloadBytesPerFrame
    }

    /**
     * 把 [pcm] 中从 [pcmOffset] 开始的 [frameCount] 个音频帧写成报文，放进 [packet]。
     *
     * @return 写出的报文总字节数，可直接交给 DatagramSocket 发送。
     */
    fun writeInto(packet: ByteArray, pcm: ByteArray, pcmOffset: Int, frameCount: Int): Int {
        requireFrameCount(frameCount)

        val payloadBytes = frameCount * payloadBytesPerFrame
        val total = HEADER_SIZE + payloadBytes

        require(packet.size >= total) {
            "目标缓冲区只有 ${packet.size} 字节，装不下 $total 字节的报文"
        }
        require(pcmOffset >= 0) { "pcmOffset 不能为负，收到 $pcmOffset" }
        require(pcmOffset + payloadBytes <= pcm.size) {
            "源数据只有 ${pcm.size} 字节，从 $pcmOffset 起取 $payloadBytes 字节会越界"
        }

        writeHeader(packet, frameCount)
        System.arraycopy(pcm, pcmOffset, packet, HEADER_SIZE, payloadBytes)

        packetsWritten++
        return total
    }

    private fun writeHeader(packet: ByteArray, frameCount: Int) {
        packet[0] = 'V'.code.toByte()
        packet[1] = 'B'.code.toByte()
        packet[2] = 'A'.code.toByte()
        packet[3] = 'N'.code.toByte()

        // 高 3 位是子协议，音频协议为 0；低 5 位是采样率索引。
        packet[4] = sampleRateIndex.toByte()

        // 规范明确要求存 n-1。
        packet[5] = (frameCount - 1).toByte()
        packet[6] = (channels - 1).toByte()

        // 低 3 位数据类型（1 = 16 位整数），第 3 位保留必须为 0，高 4 位编码器（0 = 原生 PCM）。
        packet[7] = DATA_TYPE_INT16.toByte()

        // 流名称 16 字节，NUL 填充。
        for (i in 0 until STREAM_NAME_LENGTH) {
            packet[8 + i] = if (i < streamName.length) streamName[i].code.toByte() else 0
        }

        // 帧计数：32 位无符号小端。
        val counter = packetsWritten.toUInt()
        packet[24] = (counter shr 0).toByte()
        packet[25] = (counter shr 8).toByte()
        packet[26] = (counter shr 16).toByte()
        packet[27] = (counter shr 24).toByte()
    }

    private fun requireFrameCount(frameCount: Int) {
        require(frameCount in 1..MAX_FRAMES_PER_PACKET) {
            "每个包的音频帧数必须在 1..$MAX_FRAMES_PER_PACKET 之间，收到 $frameCount"
        }
    }

    companion object {
        /** VBAN 报文头长度。 */
        const val HEADER_SIZE = 28

        /** 流名称字段长度。 */
        const val STREAM_NAME_LENGTH = 16

        /** 规范规定的单包音频帧数上限。 */
        const val MAX_FRAMES_PER_PACKET = 256

        /** 单个 UDP 报文的上限，用来避免被网络层分片。 */
        const val MAX_PACKET_BYTES = 1464

        /** 子协议：音频。 */
        const val SUB_PROTOCOL_AUDIO = 0x00

        /** 编码器：原生 PCM（免费协议基础只覆盖这一种）。 */
        const val CODEC_PCM = 0x00

        /** 数据类型：16 位有符号整数。 */
        const val DATA_TYPE_INT16 = 0x01

        /** 16 位整数每采样 2 字节。 */
        const val BYTES_PER_SAMPLE = 2

        /**
         * 规范定义的采样率索引表，顺序不可改动：索引就是线上编码。
         */
        val SAMPLE_RATES = intArrayOf(
            6_000, 12_000, 24_000, 48_000, 96_000, 192_000, 384_000,
            8_000, 16_000, 32_000, 64_000, 128_000, 256_000, 512_000,
            11_025, 22_050, 44_100, 88_200, 176_400, 352_800, 705_600,
        )
    }
}
