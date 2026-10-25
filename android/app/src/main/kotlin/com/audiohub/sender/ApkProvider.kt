package com.audiohub.sender

import android.content.ContentProvider
import android.content.ContentValues
import android.content.Context
import android.content.UriMatcher
import android.database.Cursor
import android.net.Uri
import android.os.ParcelFileDescriptor
import java.io.File

/**
 * 手写的极简 ContentProvider，只做一件事：把下载好的 APK 以 content:// 形式
 * 交给系统安装器读。
 *
 * 为什么不用 FileProvider：那是 AndroidX 里的类，而本项目刻意不引 AndroidX。
 * 这里照它的思路自己写一个等价物 —— 只读、只服务 APK、临时授权。
 *
 * 安全边界：只认 [AUTHORITY] 这一个 authority；路径固定是 apk/ 下的文件名；
 * 文件名只允许字母数字和 . _ -，拦掉 ../ 这类穿越；别的 Uri 一律不认。
 */
class ApkProvider : ContentProvider() {

    private val matcher = UriMatcher(UriMatcher.NO_MATCH)

    override fun onCreate(): Boolean {
        matcher.addURI(AUTHORITY, "apk/*", CODE_APK)
        return true
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? {
        if (matcher.match(uri) != CODE_APK) {
            return null
        }
        val name = uri.lastPathSegment ?: return null
        if (!SAFE_NAME.matches(name)) {
            return null
        }
        val ctx = context ?: return null
        val file = File(apkDir(ctx), name)
        if (!file.isFile) {
            return null
        }
        return ParcelFileDescriptor.open(file, ParcelFileDescriptor.MODE_READ_ONLY)
    }

    override fun getType(uri: Uri): String? = "application/vnd.android.package-archive"

    override fun query(
        uri: Uri,
        projection: Array<out String>?,
        selection: String?,
        selectionArgs: Array<out String>?,
        sortOrder: String?,
    ): Cursor? = null

    override fun insert(uri: Uri, values: ContentValues?): Uri? = null

    override fun delete(uri: Uri, selection: String?, selectionArgs: Array<out String>?): Int = 0

    override fun update(
        uri: Uri,
        values: ContentValues?,
        selection: String?,
        selectionArgs: Array<out String>?,
    ): Int = 0

    companion object {
        /** 必须和 AndroidManifest 里的 android:authorities 完全一致。 */
        const val AUTHORITY = "com.audiohub.sender.apk"

        private const val CODE_APK = 1
        private val SAFE_NAME = Regex("[A-Za-z0-9._-]+")

        /** 下载的 APK 统一放在这里；安装前不删，下次下载直接覆盖。 */
        fun apkDir(context: Context): File = File(context.cacheDir, "apk")

        fun contentUriFor(name: String): Uri = Uri.parse("content://$AUTHORITY/apk/$name")
    }
}
