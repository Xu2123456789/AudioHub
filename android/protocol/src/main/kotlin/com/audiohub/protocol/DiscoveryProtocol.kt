package com.audiohub.protocol

import java.util.UUID

/**
 * 电脑端"你在哪儿"的问答格式，必须与 C# 侧 `AudioHub.Core.Discovery.DiscoveryProtocol`
 * 逐字节一致。两侧各有一条测试断言同一串黄金字节（见 DiscoveryProtocolTest），
 * 任一侧改了布局，测试就会红。
 *
 * 方向是**手机广播问、电脑单播答**：
 * - 手机往 255.255.255.255:6979 发一个 probe；
 * - 每个听到的电脑用单播直接回给发问的那个地址，带上自己的身份。
 *
 * 为什么不让电脑主动广播：收广播在安卓上受 ROM 和省电策略影响很大，收不到时表现为
 * 一片安静、从外面根本没法自证；而"发广播 + 收普通单播"是到处都一致的标准行为，
 * 也不需要额外权限或多播锁。
 *
 * 字段全是定长小端，**只有 hubId 用 RFC 4122 大端**（和 Java UUID 的 msb/lsb 对应），
 * 这样另一边不必知道 .NET 是怎么在内存里摆 GUID 的。
 */
object DiscoveryProtocol {
    /** 电脑监听发现询问的 UDP 端口。 */
    const val PORT = 6979

    /** 接受的报文上限。对本格式很宽裕，同时小到一眼就能看出不是攻击。 */
    const val MAX_DATAGRAM_BYTES = 512

    private const val HEADER_BYTES = 10
    private const val VERSION: Byte = 1

    private val MAGIC = byteArrayOf(0x41, 0x48, 0x55, 0x42) // "AHUB"

    const val TYPE_PROBE: Byte = 1
    const val TYPE_ANNOUNCE: Byte = 2

    /** 构造一次询问。token 会被原样回显，用来把回答和这一次的提问对上。 */
    fun buildProbe(token: Int): ByteArray {
        val packet = ByteArray(HEADER_BYTES + 4)
        writeHeader(packet, TYPE_PROBE, payloadLength = 4)
        writeIntLe(packet, HEADER_BYTES, token)
        return packet
    }

    /** 构造一次应答。 */
    fun buildAnnounce(
        token: Int,
        hubId: UUID,
        vbanPort: Int,
        securityMode: Int,
        name: String,
    ): ByteArray {
        val nameBytes = name.toByteArray(Charsets.UTF_8).copyOf(
            minOf(name.toByteArray(Charsets.UTF_8).size, MAX_DATAGRAM_BYTES - HEADER_BYTES - 24)
        )
        val packet = ByteArray(HEADER_BYTES + 24 + nameBytes.size)
        writeHeader(packet, TYPE_ANNOUNCE, payloadLength = 24 + nameBytes.size)

        var at = HEADER_BYTES
        at = writeIntLe(packet, at, token)
        at = writeUuidBigEndian(packet, at, hubId)
        at = writeShortLe(packet, at, vbanPort)
        packet[at++] = securityMode.toByte()
        packet[at++] = nameBytes.size.toByte()
        nameBytes.copyInto(packet, at)
        return packet
    }

    /**
     * 解析一个报文。失败返回 null 而不抛异常：这里收到的每一个字节都来自网络，
     * 抛异常就等于让陌生人有机会把音频线程搞崩。
     */
    fun parse(packet: ByteArray): DiscoveryMessage? {
        if (packet.size > MAX_DATAGRAM_BYTES) return null
        if (packet.size < HEADER_BYTES) return null
        for (i in MAGIC.indices) {
            if (packet[i] != MAGIC[i]) return null
        }
        if (packet[4] != VERSION) return null

        val type = packet[5]
        val payloadLength = readShortLe(packet, 8)
        if (packet.size != HEADER_BYTES + payloadLength) return null
        if (type != TYPE_PROBE && type != TYPE_ANNOUNCE) return null

        return when (type) {
            TYPE_PROBE -> {
                if (payloadLength < 4) return null
                DiscoveryMessage.Probe(readIntLe(packet, HEADER_BYTES))
            }
            TYPE_ANNOUNCE -> parseAnnounce(packet, payloadLength)
            else -> null
        }
    }

    private fun parseAnnounce(packet: ByteArray, payloadLength: Int): DiscoveryMessage? {
        if (payloadLength < 24) return null
        val at = HEADER_BYTES
        val token = readIntLe(packet, at)
        val hubId = readUuidBigEndian(packet, at + 4)
        val vbanPort = readShortLe(packet, at + 20)
        val securityMode = packet[at + 22].toInt() and 0xFF
        if (securityMode !in 0..2) return null
        val nameLength = packet[at + 23].toInt() and 0xFF
        if (payloadLength != 24 + nameLength) return null
        val name = if (nameLength == 0) {
            ""
        } else {
            String(packet, at + 24, nameLength, Charsets.UTF_8)
        }
        return DiscoveryMessage.Announce(token, hubId, vbanPort, securityMode, name)
    }

    private fun writeHeader(packet: ByteArray, type: Byte, payloadLength: Int) {
        MAGIC.copyInto(packet)
        packet[4] = VERSION
        packet[5] = type
        packet[6] = 0
        packet[7] = 0
        writeShortLe(packet, 8, payloadLength)
    }

    private fun writeIntLe(packet: ByteArray, offset: Int, value: Int): Int {
        packet[offset] = (value and 0xFF).toByte()
        packet[offset + 1] = ((value shr 8) and 0xFF).toByte()
        packet[offset + 2] = ((value shr 16) and 0xFF).toByte()
        packet[offset + 3] = ((value shr 24) and 0xFF).toByte()
        return offset + 4
    }

    private fun readIntLe(packet: ByteArray, offset: Int): Int =
        (packet[offset].toInt() and 0xFF) or
            ((packet[offset + 1].toInt() and 0xFF) shl 8) or
            ((packet[offset + 2].toInt() and 0xFF) shl 16) or
            ((packet[offset + 3].toInt() and 0xFF) shl 24)

    private fun writeShortLe(packet: ByteArray, offset: Int, value: Int): Int {
        packet[offset] = (value and 0xFF).toByte()
        packet[offset + 1] = ((value shr 8) and 0xFF).toByte()
        return offset + 2
    }

    private fun readShortLe(packet: ByteArray, offset: Int): Int =
        (packet[offset].toInt() and 0xFF) or ((packet[offset + 1].toInt() and 0xFF) shl 8)

    /** RFC 4122 大端。与 C# 的 `Guid.TryWriteBytes(bigEndian: true)` 一致。 */
    private fun writeUuidBigEndian(packet: ByteArray, offset: Int, uuid: UUID): Int {
        val raw = ByteArray(16)
        val msb = uuid.mostSignificantBits
        val lsb = uuid.leastSignificantBits
        for (i in 0..7) {
            raw[i] = ((msb shr (8 * (7 - i))) and 0xFF).toByte()
            raw[8 + i] = ((lsb shr (8 * (7 - i))) and 0xFF).toByte()
        }
        raw.copyInto(packet, offset)
        return offset + 16
    }

    private fun readUuidBigEndian(packet: ByteArray, offset: Int): UUID {
        var msb = 0L
        var lsb = 0L
        for (i in 0..7) {
            msb = (msb shl 8) or (packet[offset + i].toLong() and 0xFF)
            lsb = (lsb shl 8) or (packet[offset + 8 + i].toLong() and 0xFF)
        }
        return UUID(msb, lsb)
    }
}

/** 一条解析出来的发现消息。 */
sealed class DiscoveryMessage {
    /** 手机发出的询问。 */
    data class Probe(val token: Int) : DiscoveryMessage()

    /** 电脑给出的应答。 */
    data class Announce(
        val token: Int,
        val hubId: UUID,
        val vbanPort: Int,
        val securityMode: Int,
        val name: String,
    ) : DiscoveryMessage()
}

/** 电脑对发送方的要求，对应 C# 的 `SecurityMode`。 */
object SecurityMode {
    /** 明文，也就是现在的行为。 */
    const val NONE = 0

    /** 加密的收，明文的也收（过渡用）。 */
    const val OPTIONAL = 1

    /** 只收加密的。 */
    const val REQUIRED = 2
}
