package com.audiohub.protocol

import java.net.URI
import java.net.URLDecoder
import java.net.URLEncoder

/**
 * 二维码里那串 URI 的内容。
 *
 * 格式：`audiohub://c/1?h=<ip>&p=<port>[&k=][&i=][&f=][&n=]`
 *
 * 密钥是可选的：带上它，手机扫一次就完成配对；不带它，手机还得输一次密钥。
 * 这是用户的选择（电脑端有开关），协议层只如实记录。
 */
data class QrPayload(
    val host: String,
    val port: Int,
    val pairingKey: String? = null,
    val hubId: String? = null,
    val fingerprint: String? = null,
    val name: String? = null,
) {
    /** 扫完这一张，是否已经具备加密所需的全部条件。 */
    val carriesKey: Boolean get() = !pairingKey.isNullOrBlank()
}

/**
 * 构造与解析二维码载荷。与 C# 的 `AudioHub.Core.Connection.ConnectionQr` 一一对应，
 * 两边用同一组往返测试保证不会各写一套。
 */
object ConnectionQr {

    const val SCHEME = "audiohub"
    const val VERSION = 1

    private const val MAX_HOST_LENGTH = 45
    private const val MAX_KEY_LENGTH = 64
    private const val MAX_FINGERPRINT_LENGTH = 32
    private const val MAX_NAME_LENGTH = 64
    private const val MAX_TEXT_LENGTH = 512

    fun build(payload: QrPayload): String {
        val text = StringBuilder()
        text.append(SCHEME).append("://c/").append(VERSION)
        text.append("?h=").append(escape(payload.host))
        text.append("&p=").append(payload.port)

        payload.pairingKey?.takeIf { it.isNotBlank() }?.let {
            text.append("&k=").append(escape(it))
        }

        payload.hubId?.takeIf { it.isNotBlank() }?.let {
            text.append("&i=").append(escape(it))
        }

        payload.fingerprint?.takeIf { it.isNotBlank() }?.let {
            text.append("&f=").append(escape(it))
        }

        payload.name?.takeIf { it.isNotBlank() }?.let {
            text.append("&n=").append(escape(it))
        }

        return text.toString()
    }

    /** 解析失败一律返回 null，不返回"填了一半"的对象。 */
    fun parse(text: String?): QrPayload? {
        if (text.isNullOrBlank() || text.length > MAX_TEXT_LENGTH) return null

        val uri = try {
            URI(text.trim())
        } catch (_: Exception) {
            return null
        }

        if (!SCHEME.equals(uri.scheme, ignoreCase = true)) return null
        if (!"c".equals(uri.host, ignoreCase = true)) return null

        val version = uri.path?.trim('/')?.toIntOrNull() ?: return null
        if (version != VERSION) return null

        val fields = parseQuery(uri.rawQuery ?: return null)

        val host = fields["h"]?.takeIf { it.length in 1..MAX_HOST_LENGTH } ?: return null
        if (ShortCode.parseIpv4(host) == null) return null

        val port = fields["p"]?.toIntOrNull() ?: return null
        if (port !in 1..65535) return null

        val key = fields["k"]?.takeIf { it.length in 1..MAX_KEY_LENGTH }
        val hubId = fields["i"]?.takeIf { it.isNotBlank() }
        val fingerprint = fields["f"]?.takeIf { it.length in 1..MAX_FINGERPRINT_LENGTH }
        val name = fields["n"]?.takeIf { it.length in 1..MAX_NAME_LENGTH }

        return QrPayload(host, port, key, hubId, fingerprint, name)
    }

    private fun parseQuery(query: String): Map<String, String> {
        val result = LinkedHashMap<String, String>()

        for (part in query.split('&')) {
            val separator = part.indexOf('=')
            if (separator <= 0) continue

            val name = part.substring(0, separator)
            if (result.containsKey(name)) continue

            val value = try {
                URLDecoder.decode(part.substring(separator + 1), Charsets.UTF_8.name())
            } catch (_: Exception) {
                // 坏转义让整个字段不可信；可选字段缺失比用半个值更安全。
                null
            } ?: continue

            result[name] = value
        }

        return result
    }

    /** 与 C# 的 Uri.EscapeDataString 对齐：空格编成 %20，而不是 URLEncoder 默认的 '+'。 */
    private fun escape(value: String): String =
        URLEncoder.encode(value, Charsets.UTF_8.name()).replace("+", "%20")
}
