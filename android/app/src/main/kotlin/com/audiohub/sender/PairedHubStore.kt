package com.audiohub.sender

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import org.json.JSONObject
import java.security.GeneralSecurityException
import java.security.KeyStore
import java.util.UUID
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * 记住"哪台电脑配过了、密钥是什么"。
 *
 * 为什么必须记：配对要人从电脑屏幕上读一串字符再敲进手机，这件事做一次就够了。
 * 忘了的话每换一次 Wi-Fi、每次重开 App 都要重来一遍，"开箱即用"就没了。
 *
 * 为什么密钥不直接写进 SharedPreferences：
 * SharedPreferences 是应用私有的，别的 App 读不到，`allowBackup` 也已经关了，
 * 所以在"同一 Wi-Fi 下的旁人"这个威胁模型下它其实是够的。但拿到已解锁手机、
 * 或有 root 的人可以直接把那个 xml 读出来。Android Keystore 的密钥材料
 * **永远不会进入应用进程**，只能由系统用它加解密，这类人拿走文件也解不开。
 * 代价只是几十行代码，所以做。
 *
 * **降级必须可见。** 少数设备（尤其是模拟器、或 Keystore 硬件异常的机器）
 * 拿不到 Keystore 密钥。这时不能悄悄退化成明文——那等于"安全"两个字是假的。
 * 降级时 [storageNote] 会带上原因，界面把它显示出来，用户至少知道实情。
 */
class PairedHubStore(context: Context) {

    private val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
    private val wrappingKey: SecretKey?
    private val wrapCipher: Cipher?

    /**
     * 密钥没有被硬件保护时的原因。null 表示一切正常（Keystore 可用）。
     * 界面应当把它显示出来，而不是吞掉。
     */
    val storageNote: String?

    init {
        val key = runCatching { loadOrCreateWrappingKey() }.getOrNull()
        if (key != null) {
            wrappingKey = key
            wrapCipher = runCatching { Cipher.getInstance(TRANSFORMATION) }.getOrNull()
            storageNote = if (wrapCipher == null) "系统不支持 $TRANSFORMATION，密钥未加密存储" else null
        } else {
            wrappingKey = null
            wrapCipher = null
            storageNote = "取不到 Android Keystore 密钥，密钥未加密存储"
        }
    }

    /** 一台配过的电脑。 */
    data class Pairing(
        val hubId: UUID,
        val name: String,
        val host: String,
        val port: Int,
    )

    /** 记下（或更新）一台电脑。这就是 TOFU：第一次连，信它，之后认它的身份。 */
    fun remember(hubId: UUID, psk: ByteArray, name: String, host: String, port: Int) {
        val record = JSONObject()
            .put("name", name)
            .put("host", host)
            .put("port", port)
            .put("psk", wrap(psk))

        prefs.edit().putString(key(hubId), record.toString()).apply()
    }

    /** 取回某台电脑的密钥；没配过返回 null。 */
    fun keyFor(hubId: UUID): ByteArray? {
        val stored = prefs.getString(key(hubId), null) ?: return null
        val psk = runCatching { JSONObject(stored).getString("psk") }.getOrNull() ?: return null
        return runCatching { unwrap(psk) }.getOrNull()
    }

    /** 已配过的电脑，按名字排序。 */
    fun all(): List<Pairing> {
        val result = ArrayList<Pairing>()
        for (entry in prefs.all) {
            result.add(read(entry.key, entry.value) ?: continue)
        }
        return result.sortedBy { it.name }
    }

    /** 解析一条记录。任何一条坏了都不该让整个列表打不开，所以坏了就跳过。 */
    private fun read(key: String, value: Any?): Pairing? {
        if (value !is String || !key.startsWith(PREFIX)) {
            return null
        }

        val id = runCatching { UUID.fromString(key.removePrefix(PREFIX)) }.getOrNull() ?: return null
        val record = runCatching { JSONObject(value) }.getOrNull() ?: return null

        return Pairing(
            hubId = id,
            name = record.optString("name"),
            host = record.optString("host"),
            port = record.optInt("port", AudioCaptureService.DEFAULT_PORT),
        )
    }

    /** 忘掉一台电脑。电脑身份变了（重装、换机）时用得着。 */
    fun forget(hubId: UUID) {
        prefs.edit().remove(key(hubId)).apply()
    }

    private fun key(hubId: UUID) = "$PREFIX$hubId"

    private fun wrap(psk: ByteArray): String {
        val cipher = wrapCipher
        val secret = wrappingKey
        if (cipher == null || secret == null) {
            // 降级路径。'0' 前缀让读取时知道这段是明文——不要靠猜。
            return "0:" + android.util.Base64.encodeToString(psk, android.util.Base64.NO_WRAP)
        }

        cipher.init(Cipher.ENCRYPT_MODE, secret)
        // Keystore 的 GCM 要求 IV 由系统生成，不能自己指定。取它给的，存起来即可。
        val iv = cipher.iv
        val sealed = cipher.doFinal(psk)
        return "1:" + android.util.Base64.encodeToString(iv, android.util.Base64.NO_WRAP) +
            ":" + android.util.Base64.encodeToString(sealed, android.util.Base64.NO_WRAP)
    }

    private fun unwrap(stored: String): ByteArray {
        val parts = stored.split(':')

        if (parts[0] == "0") {
            return android.util.Base64.decode(parts[1], android.util.Base64.NO_WRAP)
        }

        val cipher = wrapCipher ?: throw GeneralSecurityException("Keystore 不可用")
        val secret = wrappingKey ?: throw GeneralSecurityException("Keystore 不可用")
        cipher.init(
            Cipher.DECRYPT_MODE,
            secret,
            GCMParameterSpec(TAG_BITS, android.util.Base64.decode(parts[1], android.util.Base64.NO_WRAP)),
        )
        return cipher.doFinal(android.util.Base64.decode(parts[2], android.util.Base64.NO_WRAP))
    }

    private fun loadOrCreateWrappingKey(): SecretKey {
        val store = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

        if (store.containsAlias(ALIAS)) {
            val entry = store.getEntry(ALIAS, null)
            if (entry is KeyStore.SecretKeyEntry) {
                return entry.secretKey
            }
            // 别名在但类型不对（极罕见），删掉重建。
            store.deleteEntry(ALIAS)
        }

        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEYSTORE).run {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(KEY_BITS)
                    // 刻意不设 setUserAuthenticationRequired：那会让每次发声音都要先解锁，
                    // 而本项目要防的是"同一 Wi-Fi 下的旁人"，不是"手机丢了之后的人"。
                    // 为了后者去折磨前者的日常使用，不划算。
                    .build(),
            )
            generateKey()
        }
    }

    companion object {
        private const val PREFS_NAME = "audiohub-pairings"
        private const val PREFIX = "hub:"
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val ALIAS = "audiohub_psk_wrapping_key"
        private const val TRANSFORMATION = "AES/GCM/NoPadding"
        private const val KEY_BITS = 256
        private const val TAG_BITS = 128
    }
}
