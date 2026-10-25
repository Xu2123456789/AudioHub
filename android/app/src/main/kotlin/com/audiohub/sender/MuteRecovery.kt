package com.audiohub.sender

import android.content.Context
import android.media.AudioManager

/**
 * 手机静音状态的持久化与兜底恢复。
 *
 * 背景：AudioHub 连接后会把手机媒体音量调到 0（让声音只从电脑出）。但如果进程被
 * 系统/用户彻底杀死，onDestroy/onTaskRemoved 都没机会跑，音量就永远停在 0。
 * 这里把"原音量"持久化下来，在**任何 App 入口**（主界面、服务）被再次创建时，
 * 若检测到"上次静音没恢复 + 当前音量仍是 0"，就把音量恢复到原值。
 *
 * 放在顶层而不是某个类里，是因为主界面（MainActivity）和服务（AudioCaptureService）
 * 都要用，且必须用**同一份键**——否则两处各写各的 key，等于两条会各自演化的规则。
 */

internal const val PREFS_MUTE = "audiohub-mute"
internal const val KEY_WAS_MUTING = "was_muting"
internal const val KEY_ORIGINAL_VOLUME = "original_volume"

/** 静音时记录原音量，供进程被杀后兜底恢复。 */
internal fun markMuting(context: Context, originalVolume: Int) {
    context.getSharedPreferences(PREFS_MUTE, Context.MODE_PRIVATE)
        .edit()
        .putInt(KEY_ORIGINAL_VOLUME, originalVolume)
        .putBoolean(KEY_WAS_MUTING, true)
        .apply()
}

/** 正常恢复后清掉记录，下次启动不该再兜底。 */
internal fun clearMuting(context: Context) {
    context.getSharedPreferences(PREFS_MUTE, Context.MODE_PRIVATE)
        .edit()
        .remove(KEY_ORIGINAL_VOLUME)
        .putBoolean(KEY_WAS_MUTING, false)
        .apply()
}

/**
 * 兜底恢复：上次静音后进程被杀（记录残留）、且当前音量仍是 0，则恢复原音量。
 * 若当前音量已 >0（用户手动调过），不覆盖，只清掉残留记录。
 */
internal fun recoverVolumeIfNeeded(context: Context) {
    val prefs = context.getSharedPreferences(PREFS_MUTE, Context.MODE_PRIVATE)
    if (!prefs.getBoolean(KEY_WAS_MUTING, false)) {
        return
    }
    val original = prefs.getInt(KEY_ORIGINAL_VOLUME, -1)
    clearMuting(context)
    if (original <= 0) {
        return
    }
    val am = context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    if (am.getStreamVolume(AudioManager.STREAM_MUSIC) == 0) {
        runCatching { am.setStreamVolume(AudioManager.STREAM_MUSIC, original, 0) }
    }
}
