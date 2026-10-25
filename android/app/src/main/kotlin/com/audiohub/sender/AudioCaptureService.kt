package com.audiohub.sender

import android.app.Activity
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.media.AudioAttributes
import android.media.AudioFormat
import android.media.AudioPlaybackCaptureConfiguration
import android.media.AudioRecord
import android.media.projection.MediaProjection
import android.media.projection.MediaProjectionManager
import android.os.Handler
import android.os.IBinder
import android.os.Looper
import android.os.PowerManager
import com.audiohub.protocol.SecureEnvelope
import com.audiohub.protocol.SenderSecurity
import com.audiohub.protocol.interpretSenderSecurity
import com.audiohub.vban.PacketSealer
import com.audiohub.vban.VbanUdpSender

/**
 * 抓手机正在播放的声音，转成 VBAN 发给电脑。
 *
 * 为什么必须走 MediaProjection 而不是 AudioRecord 的普通音源：
 * AudioRecord 的 MIC 音源拿到的只是麦克风；要抓"系统正在播放的声音"，
 * Android 10 起唯一的正规途径是 AudioPlaybackCapture，而它要求先拿到
 * MediaProjection 授权（也就是那个"开始录制或投射"的弹窗）。
 *
 * 必须跑在前台服务里：捕获一旦退到后台就会被系统掐断，而且 Android 14 起
 * 系统会强制要求"用 MediaProjection 的应用必须有一个 mediaProjection 类型的前台服务在跑"。
 * 所以顺序不能反：先 startForeground，再拿 MediaProjection。
 */
class AudioCaptureService : Service() {

    private var worker: Thread? = null

    @Volatile
    private var stopping = false

    private var projection: MediaProjection? = null

    @Volatile
    private var record: AudioRecord? = null

    @Volatile
    private var sender: VbanUdpSender? = null

    private var wakeLock: PowerManager.WakeLock? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP -> {
                stopCapture()
                stopForeground(STOP_FOREGROUND_REMOVE)
                stopSelf()
                return START_NOT_STICKY
            }

            ACTION_START -> {
                val resultCode = intent.getIntExtra(EXTRA_RESULT_CODE, Activity.RESULT_CANCELED)

                @Suppress("DEPRECATION")
                val resultData = intent.getParcelableExtra<Intent>(EXTRA_RESULT_DATA)

                val host = intent.getStringExtra(EXTRA_HOST).orEmpty().trim()
                val port = intent.getIntExtra(EXTRA_PORT, DEFAULT_PORT)
                val streamName = intent.getStringExtra(EXTRA_STREAM).orEmpty().ifBlank { DEFAULT_STREAM }

                // 密钥给了却用不了时**必须停**，不能退回明文 —— 在只收加密包的电脑上，
                // 退回去的表现是"声音永远不来，双方都不报错"，和断网一模一样。
                val sealer = try {
                    buildSealer(intent)
                } catch (ex: IllegalArgumentException) {
                    CaptureStatus.reset()
                    CaptureStatus.stateMessage = "出错停止"
                    CaptureStatus.lastError = ex.message
                    stopSelf()
                    return START_NOT_STICKY
                }

                if (resultData == null || resultCode != Activity.RESULT_OK) {
                    CaptureStatus.reset()
                    CaptureStatus.stateMessage = "出错停止"
                    CaptureStatus.lastError = "没有拿到录屏授权数据，无法开始捕获"
                    stopSelf()
                    return START_NOT_STICKY
                }

                if (host.isEmpty()) {
                    CaptureStatus.reset()
                    CaptureStatus.stateMessage = "出错停止"
                    CaptureStatus.lastError = "没有填写电脑 IP"
                    stopSelf()
                    return START_NOT_STICKY
                }

                // 必须在 5 秒内进入前台，否则系统会直接杀掉服务。
                startForeground(
                    NOTIFICATION_ID,
                    buildNotification("正在连接 $host:$port${if (sealer != null) "（加密）" else ""}"),
                )
                startWorker(resultCode, resultData, host, port, streamName, sealer)
                return START_NOT_STICKY
            }

            else -> {
                stopSelf()
                return START_NOT_STICKY
            }
        }
    }

    /**
     * 按意图里的密钥和电脑身份造一个加密接缝。
     *
     * 返回 null **只有一个含义：用户要明文**（两条信息都没传）。
     * 参数不成立时抛 [IllegalArgumentException]，由调用方停下来并报错。
     *
     * 这个区分是刻意做的，而且是有代价换来的教训：早先这版用
     * `?... ?: return null` 串下来，于是"密钥缺了""编码坏了""UUID 不对""派生失败"
     * 四种情况**全都会静默退回明文**。在 `--secure on` 的电脑上，
     * 那就是声音永远不来、而手机界面还一切正常。**报错和继续，只能选一个。**
     *
     * 判定本身不在这里，在 [SecureEnvelope.interpretSenderSecurity]：
     * 界面和这里以前各写了一遍"要不要加密"的规则，对"选了电脑但没输密钥"
     * 给出了相反答案，结果最普通的明文通路直接起不来。规则现在只写一处。
     */
    private fun buildSealer(intent: Intent): PacketSealer? =
        when (
            val decision = interpretSenderSecurity(
                psk = intent.getByteArrayExtra(EXTRA_PSK),
                hubIdText = intent.getStringExtra(EXTRA_HUB_ID),
            )
        ) {
            is SenderSecurity.Plaintext -> null

            is SenderSecurity.Rejected ->
                throw IllegalArgumentException(decision.reason)

            is SenderSecurity.Encrypted -> {
                val key = SecureEnvelope.deriveKey(decision.psk, decision.hubId)
                val envelope = SecureEnvelope.forSending(key)
                PacketSealer { source, size, destination -> envelope.sealInto(source, size, destination) }
            }
        }

    private fun startWorker(
        resultCode: Int,
        resultData: Intent,
        host: String,
        port: Int,
        streamName: String,
        sealer: PacketSealer?,
    ) {
        stopCapture()
        stopping = false
        val thread = Thread(
            { runCapture(resultCode, resultData, host, port, streamName, sealer) },
            "audiohub-vban-capture",
        )
        // 不用守护线程：进程一旦被回收，捕获也就没意义了，让它把清理逻辑跑完。
        thread.isDaemon = false
        worker = thread
        thread.start()
    }

    private fun runCapture(
        resultCode: Int,
        resultData: Intent,
        host: String,
        port: Int,
        streamName: String,
        sealer: PacketSealer?,
    ) {
        var localRecord: AudioRecord? = null
        var localSender: VbanUdpSender? = null
        var localProjection: MediaProjection? = null

        try {
            CaptureStatus.reset()
            CaptureStatus.stateMessage = "正在建立捕获…"
            acquireWakeLock()

            val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
            val obtainedProjection = manager.getMediaProjection(resultCode, resultData)
                ?: throw IllegalStateException("系统没有返回 MediaProjection，录屏授权可能已被撤销")
            localProjection = obtainedProjection

            // Android 14 起要求注册回调，否则创建捕获时会抛异常。
            // 用户从系统状态栏撤掉录屏时，这里负责把服务一起收掉。
            obtainedProjection.registerCallback(
                object : MediaProjection.Callback() {
                    override fun onStop() {
                        CaptureStatus.lastError = "系统撤掉了录屏授权，捕获已停止"
                        stopCapture()
                        stopForeground(STOP_FOREGROUND_REMOVE)
                        stopSelf()
                    }
                },
                Handler(Looper.getMainLooper()),
            )
            projection = obtainedProjection

            // 只抓"会发出声音"的那几类用途。通话、闹钟、通知音刻意不抓：
            // 它们大多本来就不允许被录制，加进来只会让失败更难定位。
            val captureConfig = AudioPlaybackCaptureConfiguration.Builder(obtainedProjection)
                .addMatchingUsage(AudioAttributes.USAGE_MEDIA)
                .addMatchingUsage(AudioAttributes.USAGE_GAME)
                .addMatchingUsage(AudioAttributes.USAGE_UNKNOWN)
                .build()

            val format = AudioFormat.Builder()
                .setEncoding(AudioFormat.ENCODING_PCM_16BIT)
                .setSampleRate(SAMPLE_RATE)
                .setChannelMask(AudioFormat.CHANNEL_IN_STEREO)
                .build()

            val minBufferBytes = AudioRecord.getMinBufferSize(
                SAMPLE_RATE,
                AudioFormat.CHANNEL_IN_STEREO,
                AudioFormat.ENCODING_PCM_16BIT,
            )
            if (minBufferBytes <= 0) {
                throw IllegalStateException("本机不支持 48000 Hz 立体声 16 位录音（getMinBufferSize=$minBufferBytes）")
            }

            // 缓冲只开到最小值的两倍，并保证至少能放下 4 个包。
            // 刻意不往大开：捕获缓冲越大，攒在里面的数据就越多，端到端延迟越高，
            // 而"低延迟"正是这个项目存在的理由。两倍最小值足够吸收偶发抖动，
            // 又不会因为读取线程跟不上而长期堆积。
            val bufferBytes = maxOf(minBufferBytes * 2, BYTES_PER_FRAME * FRAMES_PER_READ * 4)

            val createdRecord = AudioRecord.Builder()
                .setAudioFormat(format)
                .setAudioPlaybackCaptureConfig(captureConfig)
                .setBufferSizeInBytes(bufferBytes)
                .build()
            localRecord = createdRecord

            if (createdRecord.state != AudioRecord.STATE_INITIALIZED) {
                throw IllegalStateException("AudioRecord 初始化失败，state=${createdRecord.state}")
            }
            record = createdRecord

            val createdSender = VbanUdpSender(host, port, streamName, SAMPLE_RATE, CHANNELS, sealer)
            localSender = createdSender
            sender = createdSender

            createdRecord.startRecording()
            if (createdRecord.recordingState != AudioRecord.RECORDSTATE_RECORDING) {
                throw IllegalStateException("AudioRecord 没能进入录制状态（recordingState=${createdRecord.recordingState}）")
            }

            CaptureStatus.running = true
            CaptureStatus.targetDescription = createdSender.targetDescription
            CaptureStatus.encrypted = sealer != null
            CaptureStatus.stateMessage = "正在发送"

            val chunk = ByteArray(BYTES_PER_FRAME * FRAMES_PER_READ)
            val startedAtNanos = System.nanoTime()
            var lastNotifyNanos = startedAtNanos

            while (!stopping) {
                val read = createdRecord.read(chunk, 0, chunk.size)
                if (read < 0) {
                    throw IllegalStateException("读取音频失败，错误码 $read")
                }
                if (read == 0) {
                    continue
                }

                createdSender.send(chunk, 0, read)

                val nowNanos = System.nanoTime()
                CaptureStatus.packetsSent = createdSender.packetsSent
                CaptureStatus.framesSent = createdSender.framesSent
                CaptureStatus.bytesSent = createdSender.bytesSent
                CaptureStatus.nonSilentPackets = createdSender.nonSilentPackets
                CaptureStatus.peakAmplitude = createdSender.peakAmplitude
                CaptureStatus.elapsedSeconds = (nowNanos - startedAtNanos) / 1_000_000_000.0
                createdSender.lastError?.let { CaptureStatus.lastError = it }

                if (nowNanos - lastNotifyNanos >= NOTIFICATION_REFRESH_NANOS) {
                    lastNotifyNanos = nowNanos
                    refreshNotification(createdSender)
                }
            }
        } catch (ex: Throwable) {
            CaptureStatus.lastError = "${ex.javaClass.simpleName}: ${ex.message}"
        } finally {
            CaptureStatus.running = false
            CaptureStatus.stateMessage = if (CaptureStatus.lastError == null) "已停止" else "出错停止"

            record = null
            sender = null
            projection = null

            runCatching { localRecord?.stop() }
            runCatching { localRecord?.release() }
            runCatching { localSender?.close() }
            runCatching { localProjection?.stop() }

            releaseWakeLock()
        }
    }

    /**
     * 请求停止并把捕获线程收干净。
     * 由主线程调用（界面按钮或系统回调）时最多阻塞 1.5 秒，远低于 ANR 阈值。
     */
    private fun stopCapture() {
        stopping = true
        // read() 是阻塞的，stop() 会让它带着错误码返回，循环随即退出。
        runCatching { record?.stop() }

        val thread = worker
        worker = null
        if (thread != null && Thread.currentThread() !== thread) {
            runCatching { thread.join(1_500) }
        }
    }

    private fun refreshNotification(localSender: VbanUdpSender) {
        val dbfs = CaptureStatus.peakDbfs()
        val peakText = if (dbfs == null) {
            "无信号"
        } else {
            String.format(java.util.Locale.US, "%.1f dBFS", dbfs)
        }
        val text = "${localSender.packetsSent} 包 · 峰值 $peakText"
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        runCatching { manager.notify(NOTIFICATION_ID, buildNotification(text)) }
    }

    private fun createNotificationChannel() {
        val manager = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (manager.getNotificationChannel(CHANNEL_ID) == null) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notification_channel_name),
                NotificationManager.IMPORTANCE_LOW,
            )
            channel.setShowBadge(false)
            manager.createNotificationChannel(channel)
        }
    }

    private fun buildNotification(text: String): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        return Notification.Builder(this, CHANNEL_ID)
            .setContentTitle(getString(R.string.notification_title))
            .setContentText(text)
            .setSmallIcon(android.R.drawable.ic_btn_speak_now)
            .setOngoing(true)
            .setContentIntent(open)
            .build()
    }

    private fun acquireWakeLock() {
        if (wakeLock != null) {
            return
        }
        val power = getSystemService(Context.POWER_SERVICE) as PowerManager
        wakeLock = power
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "AudioHub::capture")
            .also { it.acquire(WAKE_LOCK_TIMEOUT_MILLIS) }
    }

    private fun releaseWakeLock() {
        val lock = wakeLock ?: return
        wakeLock = null
        runCatching { if (lock.isHeld) lock.release() }
    }

    override fun onDestroy() {
        stopCapture()
        releaseWakeLock()
        super.onDestroy()
    }

    companion object {
        const val ACTION_START = "com.audiohub.sender.action.START"
        const val ACTION_STOP = "com.audiohub.sender.action.STOP"

        const val EXTRA_RESULT_CODE = "resultCode"
        const val EXTRA_RESULT_DATA = "resultData"
        const val EXTRA_HOST = "host"
        const val EXTRA_PORT = "port"
        const val EXTRA_STREAM = "stream"

        /**
         * 共享密钥的**原始字节**（16 字节）。
         *
         * 这里传字节而不是 Base64 文本：这一路上本来就只有字节，
         * 中间多一次编码/解码只多一处会出错的地方，不多一点安全性。
         * 早先那版传 Base64，于是"不是合法 Base64"成了一个只有服务端才会发现的失败模式。
         */
        const val EXTRA_PSK = "psk"

        /** 电脑身份（UUID 字符串），参与密钥派生。 */
        const val EXTRA_HUB_ID = "hubId"

        // [EXTRA_PSK] 和 [EXTRA_HUB_ID] 是**一个整体**：派生密钥两个都要用，
        // 所以要么两个都传，要么都不传。只传一个不是"半个加密"，
        // 而是一个参数错误 —— 而"选了电脑但没输密钥"本来是最普通的明文用法，
        // 界面那边必须避免发出这种组合（见 MainActivity.onActivityResult）。

        /** 必须和 PC 接收端 AudioHubOptions.NetworkSampleRate 一致。 */
        const val SAMPLE_RATE = 48_000

        /** 必须和 PC 接收端 AudioHubOptions.Channels 一致。 */
        const val CHANNELS = 2

        /** 默认端口，VBAN 协议默认值。 */
        const val DEFAULT_PORT = 6_980

        /** 默认流名称。PC 端默认不校验流名称，所以这个名字只是方便辨认。 */
        const val DEFAULT_STREAM = "AudioHub"

        /** 每个包装多少帧。256 是 VBAN 规范的单包上限，此时负载 1024 字节，不会触发 IP 分片。 */
        const val FRAMES_PER_READ = 256

        /** 16 位 × 立体声 = 每帧 4 字节。 */
        const val BYTES_PER_FRAME = CHANNELS * 2

        private const val CHANNEL_ID = "audiohub-capture"
        private const val NOTIFICATION_ID = 1
        private const val NOTIFICATION_REFRESH_NANOS = 2_000_000_000L
        private const val WAKE_LOCK_TIMEOUT_MILLIS = 4L * 60L * 60L * 1000L
    }
}
