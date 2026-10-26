package com.audiohub.protocol

/**
 * 六位短码：把私网 IPv4 直接编成六个字符，手机端纯本地解码，不需要任何网络查询。
 *
 * 为什么要有它：广播发现被挡、但单播还通的环境里，人只要从电脑屏幕上读六个字符，
 * 手机就能拿到地址。它不查网络，所以不会因为"广播被过滤"而失败。
 *
 * 字符集用 Crockford Base32（去掉 I L O U 这四个最容易看错的）。解码时还额外接受
 * 小写、空格、连字符，以及 I/L 当 1、O 当 0、U 当 V —— 抄错一个字符不该等于连不上。
 *
 * 30 位布局：[2 位类别][24 位主体][4 位 CRC-4]。
 * 类别决定主体属于哪个私网：10/8（三整段）、192.168/16（后两段）、172.16/12（第二段减 16 + 后两段）。
 * 公网地址和 CGNAT 编不进去，调用方应当提示改用二维码。
 *
 * 校验位不是安全措施，它只防止"抄错一位、解出同一网段里另一台机器"。
 * 与 C# 的 `AudioHub.Core.Connection.ShortCode` 必须逐位一致，黄金值由 Python 独立算出。
 */
object ShortCode {

    const val LENGTH = 6

    /** 短码不携带端口，固定按默认端口理解。 */
    const val DEFAULT_PORT = 6980

    private const val ALPHABET = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"

    /** 编码一个私网 IPv4；无法表示时返回 null（这是正常结果，不是错误）。 */
    fun encode(address: String): String? {
        val octets = parseIpv4(address) ?: return null

        val ipClass: Int
        val body: Int

        when {
            octets[0] == 10 -> {
                ipClass = 0
                body = (octets[1] shl 16) or (octets[2] shl 8) or octets[3]
            }

            octets[0] == 192 && octets[1] == 168 -> {
                ipClass = 1
                body = (octets[2] shl 8) or octets[3]
            }

            octets[0] == 172 && octets[1] in 16..31 -> {
                ipClass = 2
                body = ((octets[1] - 16) shl 16) or (octets[2] shl 8) or octets[3]
            }

            else -> return null
        }

        val prefix = (ipClass shl 24) or body
        var value = (prefix shl 4) or crc4(prefix, 26)

        val chars = CharArray(LENGTH)
        for (i in LENGTH - 1 downTo 0) {
            chars[i] = ALPHABET[value and 0x1F]
            value = value ushr 5
        }

        return String(chars)
    }

    /** 解码；校验位不对、长度不对、类别保留时一律返回 null，绝不猜一个地址出来。 */
    fun decode(code: String?): String? {
        if (code == null) return null

        val normalized = normalize(code)
        if (normalized.length != LENGTH) return null

        var value = 0
        for (c in normalized) {
            val index = ALPHABET.indexOf(c)
            if (index < 0) return null
            value = (value shl 5) or index
        }

        val prefix = value ushr 4
        if (crc4(prefix, 26) != (value and 0xF)) return null

        val ipClass = (prefix ushr 24) and 0x3
        val body = prefix and 0xFFFFFF
        val octets = IntArray(4)

        when (ipClass) {
            0 -> {
                octets[0] = 10
                octets[1] = (body ushr 16) and 0xFF
                octets[2] = (body ushr 8) and 0xFF
                octets[3] = body and 0xFF
            }

            1 -> {
                if (body > 0xFFFF) return null
                octets[0] = 192
                octets[1] = 168
                octets[2] = (body ushr 8) and 0xFF
                octets[3] = body and 0xFF
            }

            2 -> {
                if (body > 0xFFFFF) return null
                octets[0] = 172
                octets[1] = 16 + ((body ushr 16) and 0xFF)
                octets[2] = (body ushr 8) and 0xFF
                octets[3] = body and 0xFF
            }

            else -> return null
        }

        return octets.joinToString(".")
    }

    /**
     * CRC-4，多项式 x^4 + x + 1，初值 1111，不反射、末尾不异或。
     * 刻意写成最朴素的形式：C#、Kotlin 和算出黄金值的 Python 三份实现必须一模一样。
     */
    internal fun crc4(value: Int, bitCount: Int): Int {
        var crc = 0xF
        for (bit in bitCount - 1 downTo 0) {
            val top = (crc ushr 3) and 1
            val next = (value ushr bit) and 1
            crc = (crc shl 1) and 0xF
            if ((top xor next) != 0) crc = crc xor 0x3
        }
        return crc
    }

    private fun normalize(raw: String): String {
        val out = StringBuilder(raw.length)
        for (c in raw) {
            if (c.isWhitespace() || c == '-' || c == '_') continue

            var upper = c.uppercaseChar()
            upper = when (upper) {
                'I', 'L' -> '1'
                'O' -> '0'
                'U' -> 'V'
                else -> upper
            }
            out.append(upper)
        }
        return out.toString()
    }

    internal fun parseIpv4(text: String): IntArray? {
        val parts = text.trim().split('.')
        if (parts.size != 4) return null

        val result = IntArray(4)
        for (i in 0..3) {
            val part = parts[i]
            if (part.isEmpty()) return null
            if (part.length > 1 && part[0] == '0') return null
            if (part.any { it !in '0'..'9' }) return null

            val value = part.toIntOrNull() ?: return null
            if (value !in 0..255) return null
            result[i] = value
        }
        return result
    }
}
