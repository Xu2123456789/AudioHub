package com.audiohub.sender

import com.audiohub.vban.VbanUdpSender

/**
 * 捕获服务与界面之间的进程内状态桥。
 *
 * 服务跑在同一进程的另一个线程上，界面需要每半秒读一次进度。这里刻意不用广播：
 * 广播要处理注册/注销和 Android 13 起的接收器导出标志，为了传几个数字不值得。
 * 这些字段全部是单值读写，用 @Volatile 就够，不需要锁。
 */
object CaptureStatus {

    /** 捕获线程是否正在运行。 */
    @Volatile
    var running: Boolean = false

    /** 界面当前显示的状态，比如"正在发送"、"出错停止"。 */
    @Volatile
    var stateMessage: String = "未开始"

    /** 形如 "192.168.1.5:6980"。 */
    @Volatile
    var targetDescription: String = ""

    @Volatile
    var packetsSent: Long = 0

    @Volatile
    var framesSent: Long = 0

    @Volatile
    var bytesSent: Long = 0

    @Volatile
    var nonSilentPackets: Long = 0

    @Volatile
    var peakAmplitude: Int = 0

    /** 已经运行了多少秒（由捕获线程更新）。 */
    @Volatile
    var elapsedSeconds: Double = 0.0

    /**
     * 这次发送有没有加密。
     *
     * 必须在界面上看得见。加密一旦"以为开了其实没开"，表现和正常发送完全一样
     * ——声音照常响，没有任何报错——只有这一行能说明实情。
     */
    @Volatile
    var encrypted: Boolean = false

    /** 出错时的原因；正常时为 null。 */
    @Volatile
    var lastError: String? = null

    fun reset() {
        running = false
        stateMessage = "正在启动"
        targetDescription = ""
        packetsSent = 0
        framesSent = 0
        bytesSent = 0
        nonSilentPackets = 0
        peakAmplitude = 0
        elapsedSeconds = 0.0
        encrypted = false
        lastError = null
    }

    /** 平均发送码率，单位 kbit/s。还不够一秒或还没发包时返回 0。 */
    fun averageKilobitsPerSecond(): Double {
        if (elapsedSeconds < 0.5 || bytesSent <= 0) {
            return 0.0
        }
        return bytesSent * 8.0 / elapsedSeconds / 1000.0
    }

    /** 峰值换算成 dBFS，满量程为 0。全静音时返回 null。 */
    fun peakDbfs(): Double? = VbanUdpSender.amplitudeToDbfs(peakAmplitude)

    /** 把状态整理成一段给人看的文字。 */
    fun describe(): String {
        val lines = StringBuilder()
        lines.append("状态：").append(stateMessage).append('\n')

        if (targetDescription.isNotEmpty()) {
            lines.append("目标：").append(targetDescription).append('\n')
        }

        // 明说"明文"而不是省掉这一行：看不出来和没加密，是同一件事的两种说法。
        lines.append("链路：").append(if (encrypted) "已加密（AES-256-GCM）" else "明文，未加密").append('\n')

        lines.append("已发送：").append(packetsSent).append(" 个包 / ")
            .append(framesSent).append(" 帧\n")

        val kbps = averageKilobitsPerSecond()
        if (kbps > 0) {
            lines.append("平均码率：").append(format1(kbps)).append(" kbit/s\n")
        }

        if (packetsSent > 0) {
            val silent = packetsSent - nonSilentPackets
            val dbfs = peakDbfs()
            val peakText = if (dbfs == null) "无信号" else "${format1(dbfs)} dBFS"
            lines.append("峰值：").append(peakText).append('\n')
            lines.append("含声音的包：").append(nonSilentPackets)
                .append(" / 静音包：").append(silent).append('\n')

            if (nonSilentPackets == 0L) {
                lines.append("提示：包在发但内容一直是静音。\n")
                lines.append("      多半是当前播放的 App 不允许被录制，\n")
                lines.append("      或这段声音不在可捕获的类别里。\n")
            }
        }

        lastError?.let {
            lines.append("错误：").append(it).append('\n')
        }

        return lines.toString()
    }

    private fun format1(value: Double): String =
        String.format(java.util.Locale.US, "%.1f", value)
}
