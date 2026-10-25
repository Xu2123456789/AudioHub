package com.audiohub.sender

import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.net.HttpURLConnection
import java.net.URL

/**
 * 应用内更新：查 GitHub Releases 有没有比当前更新的 APK，有就下载并唤起系统安装器。
 *
 * 不用 AndroidX、不引第三方网络库：HTTP 用框架自带的 HttpURLConnection，
 * JSON 用框架自带的 org.json。所有网络操作在后台线程，结果回投主线程再碰界面。
 *
 * 更新源约定：仓库 Releases 里最新的一个 release，其 assets 里必须有一个 .apk 文件；
 * 版本号取自 release 的 tag（如 v1.1.0）。版本按"主.次.修订"三段数字比较，
 * 只有"最新版本 > 当前版本"才算有更新。
 */
class Updater(private val context: Context) {

    interface Listener {
        /** 主线程。给人看的一句话状态。 */
        fun onMessage(msg: String)

        /** 主线程。下载进度 0..100。 */
        fun onProgress(percent: Int)

        /** 主线程。整个流程结束（无论成败），用于恢复按钮。 */
        fun onDone()
    }

    private val handler = Handler(Looper.getMainLooper())

    fun checkForUpdates(listener: Listener) {
        Thread {
            post(listener) { it.onMessage("正在检查更新…") }

            val latest = fetchLatestRelease()
            if (latest == null) {
                post(listener) { it.onMessage("检查失败：连不上 GitHub，或仓库还没有发布过版本") }
                post(listener) { it.onDone() }
                return@Thread
            }

            val current = currentVersion()
            if (current != null && !isNewer(latest.version, current)) {
                post(listener) { it.onMessage("已是最新版本（$current）") }
                post(listener) { it.onDone() }
                return@Thread
            }

            post(listener) { it.onMessage("发现新版本 ${latest.version}，开始下载…") }
            val file = File(ApkProvider.apkDir(context), UPDATE_FILE_NAME)
            val ok = download(latest.url, file) { percent ->
                post(listener) { it.onProgress(percent) }
            }

            if (!ok) {
                post(listener) { it.onMessage("下载失败，请稍后重试") }
                post(listener) { it.onDone() }
                return@Thread
            }

            post(listener) { it.onProgress(100) }
            post(listener) { it.onMessage("下载完成，准备安装") }
            post(listener) { triggerInstall() }
            post(listener) { it.onDone() }
        }.apply { isDaemon = true }.start()
    }

    // ------------------------------------------------------------ 内部

    private fun post(listener: Listener, action: (Listener) -> Unit) {
        handler.post { action(listener) }
    }

    private fun fetchLatestRelease(): Release? {
        val conn = URL(GITHUB_LATEST).openConnection() as HttpURLConnection
        return try {
            conn.requestMethod = "GET"
            conn.setRequestProperty("Accept", "application/vnd.github+json")
            // GitHub API 没带 User-Agent 会直接 403。
            conn.setRequestProperty("User-Agent", "AudioHub-Sender")
            conn.connectTimeout = 15_000
            conn.readTimeout = 15_000
            if (conn.responseCode != HttpURLConnection.HTTP_OK) {
                return null
            }
            val body = conn.inputStream.bufferedReader().use { it.readText() }
            parseRelease(body)
        } catch (e: Exception) {
            null
        } finally {
            conn.disconnect()
        }
    }

    private fun parseRelease(body: String): Release? {
        val json = JSONObject(body)
        val tag = json.optString("tag_name").orEmpty()
        val version = tag.removePrefix("v").removePrefix("V").trim()
        if (version.isBlank()) {
            return null
        }

        val assets = json.optJSONArray("assets") ?: return null
        var url: String? = null
        for (i in 0 until assets.length()) {
            val asset = assets.getJSONObject(i)
            val name = asset.optString("name")
            if (!name.endsWith(".apk", ignoreCase = true)) {
                continue
            }
            val downloadUrl = asset.optString("browser_download_url")
            if (downloadUrl.isBlank()) {
                continue
            }
            url = downloadUrl
            // 名字里带 sender 的是手机端 APK，优先它；没有就取第一个 .apk。
            if (name.contains("sender", ignoreCase = true)) {
                break
            }
        }
        url ?: return null
        return Release(version, url)
    }

    private fun download(url: String, dest: File, onProgress: (Int) -> Unit): Boolean {
        val conn = URL(url).openConnection() as HttpURLConnection
        return try {
            conn.requestMethod = "GET"
            conn.setRequestProperty("User-Agent", "AudioHub-Sender")
            conn.instanceFollowRedirects = true
            conn.connectTimeout = 15_000
            conn.readTimeout = 60_000
            if (conn.responseCode != HttpURLConnection.HTTP_OK) {
                return false
            }

            val total = conn.contentLength
            dest.parentFile?.mkdirs()
            conn.inputStream.use { input ->
                FileOutputStream(dest).use { output ->
                    val buf = ByteArray(64 * 1024)
                    var done = 0L
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) {
                            break
                        }
                        output.write(buf, 0, n)
                        done += n
                        if (total > 0) {
                            onProgress(((done * 100) / total).toInt())
                        }
                    }
                }
            }
            true
        } catch (e: Exception) {
            false
        } finally {
            conn.disconnect()
        }
    }

    private fun triggerInstall() {
        val file = File(ApkProvider.apkDir(context), UPDATE_FILE_NAME)
        if (!file.isFile) {
            return
        }

        // Android 8（API 26）起装"未知来源"要先授权；没授权就带用户去开。
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O &&
            !context.packageManager.canRequestPackageInstalls()
        ) {
            val intent = Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES)
                .setData(Uri.parse("package:${context.packageName}"))
            context.startActivity(intent)
            return
        }

        val uri = ApkProvider.contentUriFor(UPDATE_FILE_NAME)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, "application/vnd.android.package-archive")
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }
        context.startActivity(intent)
    }

    private fun currentVersion(): String? = runCatching {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName
    }.getOrNull()

    private fun isNewer(latest: String, current: String): Boolean {
        val l = parseVersion(latest) ?: return false
        val c = parseVersion(current) ?: return false
        if (l.first != c.first) {
            return l.first > c.first
        }
        if (l.second != c.second) {
            return l.second > c.second
        }
        return l.third > c.third
    }

    private fun parseVersion(v: String): Triple<Int, Int, Int>? {
        val cleaned = v.trim()
            .removePrefix("v")
            .removePrefix("V")
            .substringBefore('-')
            .substringBefore('+')
        val parts = cleaned.split('.')
        if (parts.size < 2) {
            return null
        }
        val major = parts[0].toIntOrNull() ?: return null
        val minor = parts[1].toIntOrNull() ?: return null
        val patch = parts.getOrNull(2)?.toIntOrNull() ?: 0
        return Triple(major, minor, patch)
    }

    private data class Release(val version: String, val url: String)

    companion object {
        private const val GITHUB_LATEST =
            "https://api.github.com/repos/Xu2123456789/AudioHub/releases/latest"
        private const val UPDATE_FILE_NAME = "update.apk"
    }
}
