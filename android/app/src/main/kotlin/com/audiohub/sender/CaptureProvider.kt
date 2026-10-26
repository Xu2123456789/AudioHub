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
 * 拍二维码时的临时照片落点。
 *
 * 系统相机通过 `MediaStore.ACTION_IMAGE_CAPTURE` + `EXTRA_OUTPUT` 写一张全尺寸照片，
 * 写的地方必须是本应用能授权出去的 `content://` 地址。FileProvider 在 AndroidX 里，
 * 而本项目刻意不引 AndroidX，所以照 [ApkProvider] 的思路再写一个。
 *
 * 与 ApkProvider 的差别只有一处：这个要**支持写入**（相机写、本应用读），
 * 所以 openFile 按 mode 给不同的标志位，而不是固定只读。
 *
 * 安全边界：只认一个 authority、只服务缓存目录 `scan/` 下的文件名；
 * 文件名只允许字母数字和 `._-`，拦掉 `../` 之类的穿越；别的 Uri 一律不认。
 */
class CaptureProvider : ContentProvider() {

    private val matcher = UriMatcher(UriMatcher.NO_MATCH)

    override fun onCreate(): Boolean {
        matcher.addURI(AUTHORITY, "scan/*", CODE_SCAN)
        return true
    }

    override fun openFile(uri: Uri, mode: String): ParcelFileDescriptor? {
        if (matcher.match(uri) != CODE_SCAN) {
            return null
        }

        val name = uri.lastPathSegment ?: return null
        if (!SAFE_NAME.matches(name)) {
            return null
        }

        val ctx = context ?: return null
        val directory = scanDir(ctx)
        directory.mkdirs()
        val file = File(directory, name)

        val flags = when (mode) {
            "w", "wt" -> ParcelFileDescriptor.MODE_WRITE_ONLY or
                ParcelFileDescriptor.MODE_CREATE or
                ParcelFileDescriptor.MODE_TRUNCATE

            "rw", "rwt" -> ParcelFileDescriptor.MODE_READ_WRITE or
                ParcelFileDescriptor.MODE_CREATE or
                ParcelFileDescriptor.MODE_TRUNCATE

            else -> ParcelFileDescriptor.MODE_READ_ONLY
        }

        return ParcelFileDescriptor.open(file, flags)
    }

    override fun getType(uri: Uri): String? = "image/jpeg"

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
        const val AUTHORITY = "com.audiohub.sender.photo"

        private const val CODE_SCAN = 1
        private val SAFE_NAME = Regex("[A-Za-z0-9._-]+")

        /** 临时照片目录；每次扫完就删，不在缓存里累积。 */
        fun scanDir(context: Context): File = File(context.cacheDir, "scan")

        fun contentUriFor(name: String): Uri = Uri.parse("content://$AUTHORITY/scan/$name")
    }
}
