package com.audiohub.simulator

import com.audiohub.protocol.DiscoveryClient
import com.audiohub.protocol.DiscoveryProtocol
import com.audiohub.protocol.SecureEnvelope
import com.audiohub.protocol.SecurityMode
import com.audiohub.protocol.SenderSecurity
import com.audiohub.protocol.interpretSenderSecurity
import com.audiohub.vban.PacketSealer
import com.audiohub.vban.VbanUdpSender
import java.util.Arrays
import java.util.Locale
import java.util.UUID
import kotlin.math.PI
import kotlin.math.sin
import kotlin.system.exitProcess

/**
 * 在电脑上冒充手机，按真实节奏往电脑自己（或另一台机器）发 VBAN 音频。
 *
 * 它用的是手机端那份 [VbanUdpSender] 和那份 [SecureEnvelope]，所以跑通了就说明
 * "手机将来发出去的字节，电脑这边认得"——这一点不需要手机在场也能确认。
 *
 * 带上 `--key` 和 `--hub` 时它发的是**加密**包，于是这一条最难自证的链路
 * （Kotlin 封 → 网络 → C# 开）变成了电脑上一条命令就能跑完的事。
 *
 * 用法：
 *   simulate-sender.bat
 *   simulate-sender.bat --host=192.168.1.5 --seconds=30
 *   simulate-sender.bat --silent
 *   simulate-sender.bat --key=<配对钥匙> --hub=<电脑身份 UUID>
 *
 * 想边发边看收包情况，另开一个窗口跑 `dist\probe-vban.bat`。
 */
fun main(args: Array<String>) {
    if (args.contains("--discover")) {
        // 冒充手机问一句"谁在"，把回答上来的电脑列出来。
        // 走的是 DiscoveryClient —— 和手机端界面上"查找电脑"按钮用的是同一个类。
        runDiscovery()
        return
    }

    val options = Options.parse(args)
    val sealer = options.buildSealer()

    println("AudioHub 模拟发送端")
    println("  目标     ${options.host}:${options.port}")
    println("  流名称   ${options.streamName}")
    println("  时长     ${options.seconds} 秒")
    println("  格式     ${SAMPLE_RATE} Hz / ${CHANNELS} 声道 / 16 位")
    println(
        if (options.silent) {
            "  内容     静音（只验证链路，不验证声音）"
        } else {
            "  内容     ${options.frequency} Hz 正弦，幅度 ${(options.level * 100).toInt()}% 满量程"
        },
    )
    println("  加密     ${if (sealer != null) "开（AES-256-GCM，和手机同一份实现）" else "关（明文）"}")
    println()

    val generator = if (options.silent) null else ToneGenerator(options.frequency, options.level)
    val pcm = ByteArray(FRAMES_PER_PACKET * BYTES_PER_FRAME)

    VbanUdpSender(options.host, options.port, options.streamName, SAMPLE_RATE, CHANNELS, sealer).use { sender ->
        println("正在发送到 ${sender.targetDescription}")

        val startedAtNanos = System.nanoTime()
        val durationNanos = options.seconds.toLong() * 1_000_000_000L
        var packets = 0L
        var nextReportNanos = startedAtNanos + 1_000_000_000L

        while (System.nanoTime() - startedAtNanos < durationNanos) {
            if (generator != null) {
                generator.fill(pcm)
            } else {
                Arrays.fill(pcm, 0)
            }

            sender.send(pcm, 0, pcm.size)
            packets++

            val nowNanos = System.nanoTime()
            if (nowNanos >= nextReportNanos) {
                report(sender, nowNanos - startedAtNanos)
                nextReportNanos += 1_000_000_000L
            }

            // 按实时节奏走。每个包代表 FRAMES_PER_PACKET / SAMPLE_RATE 秒，
            // 发快了会让接收端的静音看门狗以为流断了，发慢了会欠载。
            sleepUntil(startedAtNanos + packets * FRAMES_PER_PACKET * 1_000_000_000L / SAMPLE_RATE)
        }

        println()
        val seconds = (System.nanoTime() - startedAtNanos) / 1_000_000_000.0
        println(
            String.format(
                Locale.US,
                "发送结束：%d 个包 / %d 帧 / %d 字节，用时 %.1f 秒（平均 %.1f kbit/s）",
                sender.packetsSent,
                sender.framesSent,
                sender.bytesSent,
                seconds,
                if (seconds > 0) sender.bytesSent * 8.0 / seconds / 1000.0 else 0.0,
            ),
        )
        // 加密时"上线字节"必然多于"音频字节"，差值是每包 36 字节的信封开销。
        // 把两个数都摆出来，一眼就能看出加密到底有没有生效。
        if (sender.bytesOnWire != sender.bytesSent) {
            println("  其中上线字节 ${sender.bytesOnWire}（加密开销 ${sender.bytesOnWire - sender.bytesSent} 字节）")
        }
        sender.lastError?.let { println("发送过程中出现过错误：$it") }
    }
}

/**
 * 冒充手机做一次发现。
 *
 * 这一条能自证的是**跨端**那一段：Kotlin 发出的询问，电脑端 C# 认不认；
 * C# 回的应答，Kotlin 又解不解得开。两边各自的单测都钉了同一串黄金字节，
 * 但"真的在网络上见面"是另一回事，而这件事在电脑上就能跑完，不必等真手机。
 */
private fun runDiscovery() {
    println("AudioHub 模拟发现（冒充手机找电脑）")
    println("  往 255.255.255.255:${DiscoveryProtocol.PORT} 发一次询问，等 2 秒")
    println()

    val found = DiscoveryClient().probe(2_000L)

    if (found.isEmpty()) {
        println("没有电脑回答。确认电脑上的 AudioHub 正在运行且没加 --no-discovery。")
        exitProcess(1)
    }

    println("回答上来的电脑：")
    for (hub in found) {
        println("  [匹配] ${hub.host}:${hub.vbanPort}  身份 ${hub.hubId}  ${describe(hub.securityMode)}  ${hub.name}")
    }
    println()
    println("PASS: ${found.size} 台电脑回答了询问，且报文被完整解析。")
    exitProcess(0)
}

private fun describe(mode: Int): String = when (mode) {
    SecurityMode.REQUIRED -> "要求加密"
    SecurityMode.OPTIONAL -> "加密可选"
    else -> "明文"
}

private fun report(sender: VbanUdpSender, elapsedNanos: Long) {
    val seconds = elapsedNanos / 1_000_000_000.0
    val kbps = if (seconds > 0) sender.bytesSent * 8.0 / seconds / 1000.0 else 0.0
    val dbfs = VbanUdpSender.amplitudeToDbfs(sender.peakAmplitude)
    val peakText = if (dbfs == null) "无信号" else String.format(Locale.US, "%.1f dBFS", dbfs)

    println(
        String.format(
            Locale.US,
            "  %5.1f s   包 %6d   平均 %7.1f kbit/s   峰值 %s",
            seconds,
            sender.packetsSent,
            kbps,
            peakText,
        ),
    )
}

private fun sleepUntil(targetNanos: Long) {
    val remaining = targetNanos - System.nanoTime()
    if (remaining <= 0) {
        return
    }
    Thread.sleep(remaining / 1_000_000, (remaining % 1_000_000).toInt())
}

/**
 * 生成连续相位的正弦波。
 * 相位跨包累积而不是每包重置，否则包边界上会出现台阶，听感上是咔哒声。
 */
private class ToneGenerator(frequency: Double, level: Double) {
    private var phase = 0.0
    private val phaseDelta = 2.0 * PI * frequency / SAMPLE_RATE
    private val amplitude = level * 32767.0

    fun fill(buffer: ByteArray) {
        var i = 0
        while (i < buffer.size) {
            val sample = (sin(phase) * amplitude).toInt().coerceIn(-32_768, 32_767)
            val low = (sample and 0xFF).toByte()
            val high = ((sample shr 8) and 0xFF).toByte()

            repeat(CHANNELS) {
                buffer[i++] = low
                buffer[i++] = high
            }

            phase += phaseDelta
            if (phase >= 2.0 * PI) {
                phase -= 2.0 * PI
            }
        }
    }
}

private class Options(
    val host: String,
    val port: Int,
    val seconds: Int,
    val streamName: String,
    val frequency: Double,
    val level: Double,
    val silent: Boolean,
    val keyText: String?,
    val hubId: UUID?,
) {
    /**
     * 造一个和手机完全同款的加密接缝。
     *
     * 密钥和电脑身份缺一不可：派生用的是 HKDF(密钥, 电脑身份)。
     * 只给其中一个就发不出电脑能解开的包，所以宁可报错也不静默退回明文——
     * 静默退回会让"加密到底开没开"这个问题失去答案。
     *
     * "要不要加密"这一步走 [SecureEnvelope.interpretSenderSecurity]，
     * 和手机 App 是同一份规则。这条规则以前在模拟器、界面、服务里各写了一遍，
     * 其中两份对同一个组合给出了相反答案 —— 所以现在只留一处。
     * 这里只多做一件命令行特有的事：把用户敲的密钥文本解析成字节。
     */
    fun buildSealer(): PacketSealer? {
        if (keyText == null && hubId == null) {
            return null
        }

        val psk = keyText?.let {
            SecureEnvelope.parse(it)
                ?: reject("--key 看不懂：只认 0-9 和字母（不含 I L O U），短横线可带可不带")
        }

        return when (
            val decision = interpretSenderSecurity(psk, hubId?.toString())
        ) {
            is SenderSecurity.Plaintext -> null

            is SenderSecurity.Rejected -> reject(decision.reason)

            is SenderSecurity.Encrypted -> {
                val envelope = SecureEnvelope.forSending(
                    SecureEnvelope.deriveKey(decision.psk, decision.hubId),
                )
                PacketSealer { source, size, destination ->
                    envelope.sealInto(source, size, destination)
                }
            }
        }
    }

    companion object {
        fun parse(args: Array<String>): Options {
            var host = "127.0.0.1"
            var port = 6_980
            var seconds = 20
            var streamName = "AudioHub"
            var frequency = 440.0
            var level = 0.25
            var silent = false
            var keyText: String? = null
            var hubId: UUID? = null

            for (arg in args) {
                when {
                    arg == "--help" || arg == "-h" -> {
                        printUsage()
                        exitProcess(0)
                    }

                    arg == "--silent" -> silent = true

                    arg.startsWith("--key=") -> keyText = arg.removePrefix("--key=").trim()

                    arg.startsWith("--hub=") -> {
                        val text = arg.removePrefix("--hub=").trim()
                        hubId = runCatching { UUID.fromString(text) }.getOrNull()
                            ?: reject("--hub 需要电脑身份的 UUID，收到 '$text'")
                    }

                    arg.startsWith("--host=") -> host = arg.removePrefix("--host=").trim()

                    arg.startsWith("--port=") -> port = intOf(arg, "--port=", 1..65_535)

                    arg.startsWith("--seconds=") -> seconds = intOf(arg, "--seconds=", 1..3_600)

                    arg.startsWith("--stream=") -> streamName = arg.removePrefix("--stream=").trim()

                    arg.startsWith("--frequency=") -> {
                        frequency = doubleOf(arg, "--frequency=")
                        if (frequency !in 20.0..20_000.0) {
                            reject("频率必须在 20 到 20000 Hz 之间，收到 $frequency")
                        }
                    }

                    arg.startsWith("--level=") -> {
                        level = doubleOf(arg, "--level=")
                        if (level !in 0.0..1.0) {
                            reject("幅度必须在 0 到 1 之间，收到 $level")
                        }
                    }

                    else -> reject("未知参数：$arg")
                }
            }

            if (host.isEmpty()) {
                reject("--host 不能为空")
            }
            if (streamName.isEmpty() || streamName.length > 16 || streamName.any { it.code !in 32..126 }) {
                reject("--stream 必须是 1 到 16 个可打印 ASCII 字符，收到 '$streamName'")
            }

            return Options(host, port, seconds, streamName, frequency, level, silent, keyText, hubId)
        }

        private fun intOf(arg: String, prefix: String, range: IntRange): Int {
            val text = arg.removePrefix(prefix).trim()
            val value = text.toIntOrNull() ?: reject("$prefix 需要一个整数，收到 '$text'")
            if (value !in range) {
                reject("$prefix 必须在 ${range.first} 到 ${range.last} 之间，收到 $value")
            }
            return value
        }

        private fun doubleOf(arg: String, prefix: String): Double {
            val text = arg.removePrefix(prefix).trim()
            return text.toDoubleOrNull() ?: reject("$prefix 需要一个数字，收到 '$text'")
        }

        private fun reject(message: String): Nothing {
            println("参数错误：$message")
            println()
            printUsage()
            exitProcess(2)
        }

        private fun printUsage() {
            println(
                """
                用法：simulate-sender.bat [选项]

                  --host=ADDR       目标地址，默认 127.0.0.1
                  --port=N          UDP 端口，默认 6980
                  --seconds=N       发送时长，默认 20 秒
                  --stream=NAME     流名称，默认 AudioHub
                  --frequency=HZ    正弦频率，默认 440
                  --level=0..1      幅度占满量程的比例，默认 0.25
                  --silent          发静音而不是正弦
                  --discover        冒充手机做一次发现，列出回答的电脑后退出
                  --key=XXXXX       配对密钥，给了就发加密包
                  --hub=UUID        电脑身份，和 --key 一起用；电脑上用
                                    dist\\show-key.bat 能看到身份
                  -h, --help        显示这段说明

                配 probe-vban.bat 一起用：另开一个窗口先跑它，再跑这个。

                加密要跑通，电脑上必须是 --secure=on 或 --secure=auto 启动。
                """.trimIndent(),
            )
        }
    }
}

private const val SAMPLE_RATE = 48_000
private const val CHANNELS = 2
private const val FRAMES_PER_PACKET = 256
private const val BYTES_PER_FRAME = CHANNELS * 2
