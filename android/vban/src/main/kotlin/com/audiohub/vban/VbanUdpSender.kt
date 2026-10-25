package com.audiohub.vban

import java.io.Closeable
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * 把 PCM 数据切成 VBAN 报文，用 UDP 发出去。
 *
 * 这个类放在 `:vban` 模块而不是安卓 app 模块里，因为它**一行 Android API 都没用到**，
 * 只依赖 `java.net`。放这里的两个好处：
 * 1. 安卓端和"模拟手机"的开发工具走的是同一份发送代码，模拟测试才有意义；
 * 2. 可以用普通 JVM 单元测试做 UDP 本地环回，验证线上字节完全正确。
 *
 * 数据格式必须和 PC 接收端严格一致：48000 Hz、立体声、16 位有符号整数小端。
 * VBAN 没有协商机制，格式对不上接收端会直接丢包，所以这里不接受"随便试试"的取值。
 *
 * 本类同时统计"发了多少个包、其中有多少个不是静音、峰值多大"。
 * 这三个数字是用来定位问题的：包数在涨说明发送正常；包数在涨但全是静音，
 * 说明捕获被系统或播放方禁掉了；包数不涨说明发送侧就没跑起来。
 *
 * 非线程安全：请让同一个线程完成 [send] 和统计读取。
 */
class VbanUdpSender(
    host: String,
    private val port: Int,
    streamName: String,
    sampleRate: Int,
    channels: Int,
    /**
     * 可选的加密接缝。为 null 时发出去的字节和以前一模一样（明文 VBAN），
     * 这是"加密必须能关掉"这条要求的实现方式：不传就是不加密。
     */
    private val sealer: PacketSealer? = null,
) : Closeable {

    private val socket = DatagramSocket()
    private val target: InetAddress = InetAddress.getByName(host)
    private val writer = VbanPacketWriter(streamName, sampleRate, channels)
    private val buffer = ByteArray(VbanPacketWriter.MAX_PACKET_BYTES)

    /** 加密后的落脚点。多留 64 字节：信封开销固定 36，余量给将来的版本号扩展。 */
    private val sealedBuffer = ByteArray(VbanPacketWriter.MAX_PACKET_BYTES + 64)

    private val datagram = DatagramPacket(buffer, 0)
    private val bytesPerFrame = writer.payloadBytesPerFrame

    /** 实际发出去的字节数（含加密开销）。 */
    var bytesOnWire: Long = 0
        private set

    /** 已成功交给系统网络栈的报文数。 */
    var packetsSent: Long = 0
        private set

    /** 已发送的音频帧数。 */
    var framesSent: Long = 0
        private set

    /** 已发送的 UDP 负载字节数（不含 IP/UDP 头）。 */
    var bytesSent: Long = 0
        private set

    /**
     * 内容不是静音的报文数。阈值见 [SILENCE_THRESHOLD]。
     * 如果这个值一直为 0，而 [packetsSent] 在涨，说明发出去的是静音。
     */
    var nonSilentPackets: Long = 0
        private set

    /** 到目前为止观察到的最大绝对采样值（16 位有符号，范围 0..32767）。 */
    var peakAmplitude: Int = 0
        private set

    /** 最近一次发送失败的原因；成功时为 null。 */
    var lastError: String? = null
        private set

    /** 形如 "192.168.1.5:6980"，用于显示目标。 */
    val targetDescription: String
        get() = "${target.hostAddress}:$port"

    /**
     * 把 [pcm] 中从 [offset] 开始、长度为 [length] 字节的数据切成报文发出去。
     *
     * 长度不是整数帧时会忽略末尾不足一帧的零头——发送侧永远不该产生半个采样，
     * 出现这种情况说明捕获侧出了问题，宁可丢掉也不要发一个畸形包。
     */
    fun send(pcm: ByteArray, offset: Int, length: Int) {
        var consumed = 0
        while (length - consumed >= bytesPerFrame) {
            val framesLeft = (length - consumed) / bytesPerFrame
            val frameCount = minOf(framesLeft, writer.maxFramesPerPacket)
            sendOne(pcm, offset + consumed, frameCount)
            consumed += frameCount * bytesPerFrame
        }
    }

    private fun sendOne(pcm: ByteArray, pcmOffset: Int, frameCount: Int) {
        val payloadBytes = frameCount * bytesPerFrame
        val size = writer.writeInto(buffer, pcm, pcmOffset, frameCount)

        val peak = measurePeak(pcm, pcmOffset, payloadBytes)
        if (peak > peakAmplitude) {
            peakAmplitude = peak
        }
        if (peak >= SILENCE_THRESHOLD) {
            nonSilentPackets++
        }

        val wireSize: Int
        val wireBuffer: ByteArray

        if (sealer == null) {
            wireBuffer = buffer
            wireSize = size
        } else {
            wireSize = sealer.seal(buffer, size, sealedBuffer)
            wireBuffer = sealedBuffer
        }

        datagram.setData(wireBuffer, 0, wireSize)
        datagram.address = target
        datagram.port = port

        try {
            socket.send(datagram)
        } catch (ex: IOException) {
            lastError = ex.message ?: ex.javaClass.simpleName
            return
        }

        packetsSent++
        framesSent += frameCount
        // bytesSent 记的是 VBAN 报文的长度，用来跟"该发多少音频"对账；
        // bytesOnWire 记的是真正上了网的长度，两者之差就是加密开销。
        bytesSent += size.toLong()
        bytesOnWire += wireSize.toLong()
        lastError = null
    }

    /**
     * 求这段 PCM 的最大绝对采样值。
     * 逐字节手工解小端，避免为每个包分配 ShortArray——这条路每秒要走几百次。
     */
    private fun measurePeak(pcm: ByteArray, offset: Int, byteCount: Int): Int {
        var peak = 0
        var i = offset
        val end = offset + byteCount
        while (i + 1 < end) {
            val low = pcm[i].toInt() and 0xFF
            val high = pcm[i + 1].toInt()
            val sample = (high shl 8) or low
            // -32768 取绝对值在 16 位下会溢出，但这里用 Int 运算，不会溢出。
            val magnitude = if (sample < 0) -sample else sample
            if (magnitude > peak) {
                peak = magnitude
            }
            i += 2
        }
        return peak
    }

    override fun close() {
        socket.close()
    }

    companion object {
        /**
         * 低于这个绝对幅度的包算作静音。
         * 取 8 而不是 0，是为了容忍捕获路径上可能存在的极小底噪。
         */
        const val SILENCE_THRESHOLD = 8

        /** 满量程值，用于把幅度换算成 dBFS 显示。 */
        const val FULL_SCALE = 32768.0

        /** 把幅度换算成 dBFS（满量程为 0），无信号时返回 null。 */
        fun amplitudeToDbfs(amplitude: Int): Double? {
            if (amplitude <= 0) {
                return null
            }
            return 20.0 * kotlin.math.log10(
                maxOf(amplitude, 1) / FULL_SCALE,
            )
        }
    }
}
