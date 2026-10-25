package com.audiohub.protocol

import java.security.SecureRandom
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * 与电脑端 `AudioHub.Core.Security` 对齐的密钥与加密信封。
 *
 * 两侧必须能互相解开，所以这一份是照着 C# 那份的字节布局写的，任何一方改布局
 * 都会在 `SecureEnvelopeTest` 里红掉（那里断言的是同一串黄金字节）。
 *
 * 几个决定，都不是随手定的：
 * - **PSK 不直接当 AES 密钥用**，而是用 HKDF 把它和电脑身份绑在一起派生。
 *   这样同一个 PSK 在不同电脑上派生出的密钥不同，也泄露不出 PSK 本身。
 * - **nonce = 4 字节 salt ‖ 8 字节递增计数**，salt 随包发送。
 *   GCM 里 nonce 重用不是"弱一点"，是会直接泄露认证子密钥、让攻击者能伪造。
 *   单靠计数器不够：发送方重启后计数从 0 开始，正好撞上。所以 salt 每次实例随机。
 * - **显示用 Crockford Base32**，去掉了手抄最容易混的 I L O U，
 *   并且解码时把 O→0、I/L→1、U→V 当等价，避免"看着抄对了却连不上"。
 *
 * **非线程安全**：计数器、Cipher 和几个复用缓冲都是实例状态。
 * 请让同一个线程完成封装（或拆封），这一点和 `VbanUdpSender` 一致。
 */
class SecureEnvelope private constructor(
    private val key: ByteArray,
    private val salt: Int,
) {
    private var counter = 0L

    // 下面三个缓冲是复用的。加密每秒要走近两百次，每次都新建 Cipher 或 ByteArray
    // 会持续制造垃圾，而 GC 停顿正是音频链路里最不该出现的东西。
    private val cipher = Cipher.getInstance("AES/GCM/NoPadding")
    private val secretKey = SecretKeySpec(key, "AES")
    private val header = ByteArray(HEADER_BYTES)
    private val nonce = ByteArray(12)

    /** 该实例使用的 nonce 前缀。 */
    val nonceSalt: Int get() = salt

    /** 已发出的包数，也是目前用到的最大 nonce 计数。 */
    val packetsSealed: Long get() = counter

    /** 封装一个包：明文 → 可发送的字节。 */
    fun seal(plaintext: ByteArray): ByteArray {
        val packet = ByteArray(HEADER_BYTES + plaintext.size + TAG_BYTES)
        sealInto(plaintext, plaintext.size, packet)
        return packet
    }

    /**
     * 就地封装：把 [source] 的前 [length] 字节封装进 [destination]，返回实际写入的字节数。
     *
     * 音频发送路径上唯一该用的入口 —— 它不分配任何临时对象。
     * [destination] 至少要能放下 `HEADER_BYTES + length + TAG_BYTES` 字节。
     */
    fun sealInto(source: ByteArray, length: Int, destination: ByteArray): Int {
        require(length <= 65535 - OVERHEAD) { "单个包太大" }
        require(destination.size >= HEADER_BYTES + length + TAG_BYTES) { "目标缓冲区太小" }

        val next = ++counter

        header[0] = 0x41
        header[1] = 0x48
        header[2] = 0x53
        header[3] = 0x31
        header[4] = FORMAT_VERSION
        header[5] = TYPE_AUDIO
        writeIntLe(header, 6, salt)
        writeLongLe(header, 10, next)
        writeShortLe(header, 18, length + TAG_BYTES)

        writeIntLe(nonce, 0, salt)
        writeLongLe(nonce, 4, next)

        cipher.init(Cipher.ENCRYPT_MODE, secretKey, GCMParameterSpec(TAG_BYTES * 8, nonce))
        // 整个明文头都参与认证：头被改，标签一样验不过。
        cipher.updateAAD(header, 0, HEADER_BYTES)
        val produced = cipher.doFinal(source, 0, length, destination, HEADER_BYTES)

        header.copyInto(destination)
        return HEADER_BYTES + produced
    }

    /** 拆开一个包。失败返回 null —— 绝不返回"看起来解开了"的半截数据。 */
    fun open(packet: ByteArray): ByteArray? {
        if (packet.size < HEADER_BYTES + TAG_BYTES) return null

        val header = packet.copyOfRange(0, HEADER_BYTES)
        if (header[0] != 0x41.toByte() || header[1] != 0x48.toByte() ||
            header[2] != 0x53.toByte() || header[3] != 0x31.toByte()
        ) return null
        if (header[4] != FORMAT_VERSION) return null

        val declared = readShortLe(header, 18)
        if (packet.size != HEADER_BYTES + declared) return null
        if (declared < TAG_BYTES) return null

        val salt = readIntLe(header, 6)
        val counter = readLongLe(header, 10)
        writeIntLe(nonce, 0, salt)
        writeLongLe(nonce, 4, counter)

        return try {
            cipher.init(Cipher.DECRYPT_MODE, secretKey, GCMParameterSpec(TAG_BYTES * 8, nonce))
            cipher.updateAAD(packet, 0, HEADER_BYTES)
            cipher.doFinal(packet, HEADER_BYTES, packet.size - HEADER_BYTES)
        } catch (e: javax.crypto.AEADBadTagException) {
            null
        } catch (e: javax.crypto.IllegalBlockSizeException) {
            null
        } catch (e: java.security.GeneralSecurityException) {
            null
        }
    }

    companion object {
        const val HEADER_BYTES = 20
        const val TAG_BYTES = 16

        /** 每个包增加的字节数。 */
        const val OVERHEAD = HEADER_BYTES + TAG_BYTES

        private const val FORMAT_VERSION: Byte = 1
        private const val TYPE_AUDIO: Byte = 1

        /** PSK 的熵：16 字节 = 128 bit。 */
        const val PSK_BYTES = 16

        const val DERIVED_KEY_BYTES = 32

        private const val KEY_INFO = "AudioHub v1 derived key"

        /** Crockford Base32：没有 I L O U。 */
        private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

        /** 按已派生的密钥建一个发送用信封，salt 随机。 */
        fun forSending(derivedKey: ByteArray): SecureEnvelope {
            val bytes = ByteArray(4)
            SecureRandom().nextBytes(bytes)
            return SecureEnvelope(derivedKey, readIntLe(bytes, 0))
        }

        /** 按已派生的密钥建一个接收用信封，salt 无意义（每个包自带）。 */
        fun forReceiving(derivedKey: ByteArray): SecureEnvelope =
            SecureEnvelope(derivedKey, 0)

        /** 仅供测试：固定 salt，好断言 nonce 行为。 */
        internal fun withSalt(derivedKey: ByteArray, salt: Int): SecureEnvelope =
            SecureEnvelope(derivedKey, salt)

        fun generatePsk(): ByteArray {
            val psk = ByteArray(PSK_BYTES)
            SecureRandom().nextBytes(psk)
            return psk
        }

        /**
         * RFC 5869 HKDF-SHA256。Java 标准库没有 HKDF，所以这里自己实现；
         * 与 .NET 的 `HKDF.DeriveKey(SHA256, ...)` 同一算法，两边派生结果一致。
         */
        fun deriveKey(psk: ByteArray, hubId: UUID): ByteArray {
            require(psk.isNotEmpty()) { "PSK 不能为空" }
            val salt = uuidToBigEndianBytes(hubId)

            // extract: prk = HMAC(salt, ikm)
            val prk = hmacSha256(salt, psk)

            // expand: okm = HMAC(prk, info || 0x01)（32 字节只需一轮）
            val info = KEY_INFO.toByteArray(Charsets.UTF_8) + byteArrayOf(0x01)
            return hmacSha256(prk, info)
        }

        /** 渲染成给人看的样子，五字符一组。 */
        fun toDisplayString(psk: ByteArray): String {
            var bitBuffer = 0
            var bitCount = 0
            val out = StringBuilder()
            var written = 0
            for (b in psk) {
                bitBuffer = (bitBuffer shl 8) or (b.toInt() and 0xFF)
                bitCount += 8
                while (bitCount >= 5) {
                    bitCount -= 5
                    out.append(ALPHABET[(bitBuffer shr bitCount) and 0x1F])
                    written++
                    if (written % 5 == 0) out.append('-')
                }
            }
            if (bitCount > 0) {
                out.append(ALPHABET[(bitBuffer shl (5 - bitCount)) and 0x1F])
                written++
                if (written % 5 == 0) out.append('-')
            }
            return out.toString().trimEnd('-')
        }

        /**
         * 读回人手敲的密钥。大小写、分隔符都容忍，并把容易混的字符当成等价：
         * 让人因为抄错一个字符就永远连不上，不值得。
         */
        fun parse(text: String): ByteArray? {
            if (text.isBlank()) return null
            val values = ArrayList<Int>()
            for (c in text) {
                if (c == '-' || c == ' ' || c == '\t') continue
                val normalised = when (c.uppercaseChar()) {
                    'I', 'L' -> '1'
                    'O' -> '0'
                    'U' -> 'V'
                    else -> c.uppercaseChar()
                }
                val index = ALPHABET.indexOf(normalised)
                if (index < 0) return null
                values.add(index)
            }
            if (values.isEmpty()) return null

            val byteCount = values.size * 5 / 8
            if (byteCount == 0) return null

            val result = ByteArray(byteCount)
            var bitBuffer = 0
            var bitCount = 0
            var written = 0
            for (v in values) {
                bitBuffer = (bitBuffer shl 5) or v
                bitCount += 5
                if (bitCount >= 8) {
                    bitCount -= 8
                    result[written++] = ((bitBuffer shr bitCount) and 0xFF).toByte()
                }
            }
            return if (written == byteCount) result else result.copyOfRange(0, written)
        }

        private fun hmacSha256(key: ByteArray, data: ByteArray): ByteArray {
            val mac = Mac.getInstance("HmacSHA256")
            mac.init(SecretKeySpec(key, "HmacSHA256"))
            return mac.doFinal(data)
        }

        private fun uuidToBigEndianBytes(uuid: UUID): ByteArray {
            val raw = ByteArray(16)
            val msb = uuid.mostSignificantBits
            val lsb = uuid.leastSignificantBits
            for (i in 0..7) {
                raw[i] = ((msb shr (8 * (7 - i))) and 0xFF).toByte()
                raw[8 + i] = ((lsb shr (8 * (7 - i))) and 0xFF).toByte()
            }
            return raw
        }

        private fun writeIntLe(target: ByteArray, offset: Int, value: Int) {
            target[offset] = (value and 0xFF).toByte()
            target[offset + 1] = ((value shr 8) and 0xFF).toByte()
            target[offset + 2] = ((value shr 16) and 0xFF).toByte()
            target[offset + 3] = ((value shr 24) and 0xFF).toByte()
        }

        private fun readIntLe(source: ByteArray, offset: Int): Int =
            (source[offset].toInt() and 0xFF) or
                ((source[offset + 1].toInt() and 0xFF) shl 8) or
                ((source[offset + 2].toInt() and 0xFF) shl 16) or
                ((source[offset + 3].toInt() and 0xFF) shl 24)

        private fun writeShortLe(target: ByteArray, offset: Int, value: Int) {
            target[offset] = (value and 0xFF).toByte()
            target[offset + 1] = ((value shr 8) and 0xFF).toByte()
        }

        private fun readShortLe(source: ByteArray, offset: Int): Int =
            (source[offset].toInt() and 0xFF) or ((source[offset + 1].toInt() and 0xFF) shl 8)

        private fun writeLongLe(target: ByteArray, offset: Int, value: Long) {
            writeIntLe(target, offset, (value and 0xFFFFFFFFL).toInt())
            writeIntLe(target, offset + 4, ((value shr 32) and 0xFFFFFFFFL).toInt())
        }

        private fun readLongLe(source: ByteArray, offset: Int): Long =
            (readIntLe(source, offset).toLong() and 0xFFFFFFFFL) or
                (readIntLe(source, offset + 4).toLong() shl 32)
    }
}

/**
 * 这次发送用不用加密 —— 由"配对密钥"和"电脑身份"两条信息整理出的三种结果之一。
 *
 * 之所以做成三态而不是一个可空返回值，是因为**可空有歧义**：
 * `null` 既可能表示"用户要明文"，也可能表示"要加密但拿不到"，
 * 而这两者需要完全相反的处理（继续 / 停下报错）。修这个歧义是拿真实故障换来的 ——
 * 早先的模拟器、界面和服务各写了一遍"要不要加密"的规则，其中两份对同一个组合
 * 给出了相反答案：界面认为"选了电脑、没输密钥"是正常的明文用法，服务认为那是参数错误。
 * 于是最普通的一条通路直接起不来。规则现在只写在一处，三方都问它。
 *
 * 三种情况没有第四种：密钥和身份**要么都给，要么都不给**。
 * 只给一个不是"部分加密"，而是参数不成立 —— 派生密钥两步都要用。
 */
sealed interface SenderSecurity {
    /** 用户要明文发送：两条信息都没给。 */
    object Plaintext : SenderSecurity

    /** 用户要加密：两条信息都给了，而且都看得懂。 */
    class Encrypted(val psk: ByteArray, val hubId: UUID) : SenderSecurity

    /** 参数不成立，调用方必须停下来报错，**不能**悄悄退回明文。 */
    class Rejected(val reason: String) : SenderSecurity
}

/**
 * 把界面（或命令行）交来的两条信息整理成 [SenderSecurity]。
 *
 * 纯函数：不碰 Android API，不读文件，不认识 Intent，所以能直接单测。
 * 密钥以**字节**而不是文本传入，是因为这一路上本来就有字节，
 * 中间多一次编码/解码只多一处会出错的地方，不多一点安全性。
 */
fun interpretSenderSecurity(psk: ByteArray?, hubIdText: String?): SenderSecurity {
    // 空数组和空串一律当"没给"：Intent 取不到 extra 和取到空值，
    // 在调用方看来是同一件事，不该得到两种待遇。
    val key = psk?.takeIf { it.isNotEmpty() }
    val hubText = hubIdText?.takeIf { it.isNotBlank() }

    if (key == null && hubText == null) {
        return SenderSecurity.Plaintext
    }

    if (key == null) {
        return SenderSecurity.Rejected("给了电脑身份却没给密钥：没有共享密钥派生不出加密密钥")
    }

    if (hubText == null) {
        return SenderSecurity.Rejected("给了密钥却没给电脑身份：不知道是哪台电脑就派生不出密钥")
    }

    val hubId = runCatching { UUID.fromString(hubText.trim()) }.getOrNull()
        ?: return SenderSecurity.Rejected("电脑身份不是合法的 UUID：$hubText")

    return SenderSecurity.Encrypted(key, hubId)
}
