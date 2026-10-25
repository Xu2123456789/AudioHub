package com.audiohub.sender

import android.Manifest
import android.app.Activity
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.text.InputType
import android.view.View
import android.view.ViewGroup
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.audiohub.protocol.DiscoveredHub
import com.audiohub.protocol.DiscoveryClient
import com.audiohub.protocol.SecureEnvelope
import com.audiohub.protocol.SecurityMode
import com.audiohub.vban.VbanPacketWriter
import java.util.UUID

/**
 * 界面。四件事：找到电脑、必要时配一次对、开始/停止、把实时状态显示出来。
 *
 * 状态区刻意把"发了多少包 / 峰值多大 / 有多少包是静音"都摆出来，
 * 而不是只写"运行中"。出问题时这几个数字能直接指认是哪一段断的：
 * 包数为 0 是没发出去，包数在涨但没有含声音的包是捕获到的是静音，
 * 包数正常但电脑没反应才是网络或接收端的事。
 *
 * "查找电脑"是**开箱即用**那一半的实现：用户不必知道 IP 是什么。
 * 找到的电脑按身份（UUID）记下来，密钥只在该要的时候才要一次（TOFU）：
 * 第一次信它、以后认它。电脑身份变了就当新电脑，会重新问一次。
 */
class MainActivity : Activity() {

    private lateinit var hostView: EditText
    private lateinit var portView: EditText
    private lateinit var streamView: EditText
    private lateinit var keyView: EditText
    private lateinit var keyHintView: TextView
    private lateinit var findButton: Button
    private lateinit var toggleButton: Button
    private lateinit var statusView: TextView
    private lateinit var hubListView: LinearLayout

    private lateinit var pairings: PairedHubStore

    private val handler = Handler(Looper.getMainLooper())

    /** 权限被拒后要不要接着去要录屏授权。 */
    private var pendingStart = false

    /** 选中的那台电脑。null 表示用户手填地址、没走发现。 */
    private var selectedHubId: UUID? = null

    /** 选中电脑对加密的要求，决定要不要出现密钥输入框。 */
    private var selectedSecurityMode = SecurityMode.NONE

    /** 最近一次查到的电脑。 */
    private var candidates: List<DiscoveredHub> = emptyList()

    private var searching = false

    /** 这次发送要用的密钥；null 表示明文发送。 */
    private var pendingPsk: ByteArray? = null

    private val refresher = object : Runnable {
        override fun run() {
            renderStatus()
            handler.postDelayed(this, REFRESH_INTERVAL_MILLIS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        pairings = PairedHubStore(this)
        setContentView(buildUi())
        restoreSelection()
        renderHubList()
    }

    override fun onStart() {
        super.onStart()
        handler.post(refresher)
    }

    override fun onStop() {
        handler.removeCallbacks(refresher)
        super.onStop()
    }

    override fun onDestroy() {
        handler.removeCallbacks(refresher)
        super.onDestroy()
    }

    private fun buildUi(): View {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val pad = dp(16)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(pad, pad, pad, pad)
        }

        column.addView(
            TextView(this).apply {
                text = "第一步：找到电脑"
                textSize = 14f
                setPadding(0, 0, 0, dp(4))
            },
        )

        findButton = Button(this).apply {
            text = getString(R.string.button_find_hub)
            setOnClickListener { startDiscovery() }
        }
        column.addView(findButton)

        // 候选列表。每次查完重建：电脑可能来了又走，留一份陈旧的列表比没有更糟。
        hubListView = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(0, dp(8), 0, dp(8))
        }
        column.addView(hubListView)

        column.addView(label("电脑 IP（点上面找到的电脑会自动填；也可以手填）"))
        hostView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            hint = "例如 192.168.1.5"
            setText(prefs.getString(KEY_HOST, ""))
        }
        column.addView(hostView)

        column.addView(label("端口（保持 6980，除非电脑端改过）"))
        portView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            setText(prefs.getString(KEY_PORT, AudioCaptureService.DEFAULT_PORT.toString()))
        }
        column.addView(portView)

        column.addView(label("流名称（电脑端默认不校验，留空即用 AudioHub）"))
        streamView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT
            setText(prefs.getString(KEY_STREAM, AudioCaptureService.DEFAULT_STREAM))
        }
        column.addView(streamView)

        // 密钥输入框默认藏着：绝大多数人用不上（电脑默认是明文模式）。
        // 只有选中的电脑要求加密、或支持加密时，才把它显示出来。
        keyHintView = TextView(this).apply {
            textSize = 12f
            visibility = View.GONE
            setPadding(0, dp(12), 0, dp(4))
        }
        column.addView(keyHintView)

        keyView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            hint = "例如 ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2"
            visibility = View.GONE
        }
        column.addView(keyView)

        toggleButton = Button(this).apply {
            text = getString(R.string.button_start)
            setOnClickListener { onToggleClicked() }
        }
        column.addView(toggleButton)

        statusView = TextView(this).apply {
            typeface = Typeface.MONOSPACE
            textSize = 13f
            setTextIsSelectable(true)
        }
        column.addView(
            ScrollView(this).apply { addView(statusView) },
            LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1f),
        )

        column.addView(
            TextView(this).apply {
                textSize = 12f
                text = getString(R.string.usage_note)
            },
        )

        // 密钥没能被硬件保护时要说出来。静默降级等于假装安全。
        pairings.storageNote?.let { note ->
            column.addView(
                TextView(this).apply {
                    textSize = 12f
                    text = "注意：$note"
                    setPadding(0, dp(8), 0, 0)
                },
            )
        }

        return column
    }

    private fun label(text: String): TextView =
        TextView(this).apply {
            this.text = text
            textSize = 13f
            setPadding(0, dp(12), 0, dp(4))
        }

    // ------------------------------------------------------------ 发现与配对

    /**
     * 发一次广播问"谁在"，把回答上来的电脑列出来。
     *
     * 必须另开线程：主线程上做网络操作会直接抛 NetworkOnMainThreadException。
     * 结果回投到主线程再改界面。
     */
    private fun startDiscovery() {
        if (searching) {
            return
        }

        searching = true
        findButton.isEnabled = false
        findButton.text = getString(R.string.searching_hub)

        Thread {
            // 两秒足够：同一个 Wi-Fi 下往返是毫秒级，给余量是为了电脑那边的服务
            // 可能刚好还没起来，而不是为了网络慢。
            val found = runCatching { DiscoveryClient().probe(2_000L) }
                .getOrDefault(emptyList())

            // 两秒内界面可能已经被关掉或转屏重建。往一个已经销毁的 Activity 上
            // 弹 Toast / 改控件会抛 BadTokenException，所以先确认还活着。
            handler.post {
                if (isFinishing || isDestroyed) {
                    return@post
                }

                searching = false
                findButton.isEnabled = true
                findButton.text = getString(R.string.button_find_hub)
                candidates = found
                renderHubList()

                if (found.isEmpty()) {
                    // 没找到时把本机地址摆出来：一眼能看出手机连的是不是预期网络，
                    // 也能看出手机此刻拿到的 IP 属不属于电脑那个网段。
                    val nets = DiscoveryClient.localAddresses().joinToString("  ") { it }
                    val msg = if (nets.isBlank()) {
                        getString(R.string.no_hub_found)
                    } else {
                        "没找到电脑。本机地址：$nets\n请确认电脑上的 AudioHub 正在运行，且手机与它连的是同一个 Wi-Fi。"
                    }
                    Toast.makeText(this, msg, Toast.LENGTH_LONG).show()
                } else {
                    Toast.makeText(this, getString(R.string.hubs_found, found.size), Toast.LENGTH_SHORT).show()
                }
            }
        }.apply { isDaemon = true }.start()
    }

    private fun renderHubList() {
        hubListView.removeAllViews()

        val paired = pairings.all()
        if (paired.isNotEmpty()) {
            hubListView.addView(label("已配对的电脑"))
            for (pairing in paired) {
                val text = if (pairing.name.isBlank()) pairing.host else "${pairing.name}（${pairing.host}）"
                hubListView.addView(hubRow(text, "点一下选中") { selectPaired(pairing) })
            }
        }

        if (candidates.isNotEmpty()) {
            hubListView.addView(label("查到的电脑"))
            for (hub in candidates) {
                hubListView.addView(hubRow(hub.label, describeSecurity(hub.securityMode)) { selectHub(hub) })
            }
        }
    }

    private fun hubRow(title: String, subtitle: String, onClick: () -> Unit): TextView {
        val marker = android.util.TypedValue()
        theme.resolveAttribute(android.R.attr.selectableItemBackground, marker, true)

        return TextView(this).apply {
            text = "$title\n$subtitle"
            textSize = 14f
            setPadding(dp(10), dp(8), dp(10), dp(8))
            isClickable = true
            setBackgroundResource(marker.resourceId)
            setOnClickListener { onClick() }
        }
    }

    private fun describeSecurity(mode: Int): String = when (mode) {
        SecurityMode.REQUIRED -> "要求加密发送，需要输一次密钥"
        SecurityMode.OPTIONAL -> "支持加密发送，密钥可选"
        else -> "明文发送，不需要密钥"
    }

    private fun selectHub(hub: DiscoveredHub) {
        clearTypedKeyIfTargetChanged(hub.hubId)
        selectedHubId = hub.hubId
        selectedSecurityMode = hub.securityMode
        hostView.setText(hub.host)
        portView.setText(hub.vbanPort.toString())
        updateKeyVisibility()
        renderHubList()
    }

    private fun selectPaired(pairing: PairedHubStore.Pairing) {
        clearTypedKeyIfTargetChanged(pairing.hubId)
        selectedHubId = pairing.hubId
        val candidate = candidates.firstOrNull { it.hubId == pairing.hubId }
        selectedSecurityMode = candidate?.securityMode ?: SecurityMode.OPTIONAL
        hostView.setText(pairing.host)
        portView.setText(pairing.port.toString())
        updateKeyVisibility()
        renderHubList()
    }

    /**
     * 换了一台电脑就把输入框里上一个密钥清掉。
     *
     * 不清的话会出现这种情况：给甲电脑输过一次密钥，再去选乙电脑，
     * 输入框里还留着甲的那一串。用户以为"框里有东西就是对的"，
     * 一点开始，**用甲的密钥去连乙** —— 音量、界面、日志全都正常，
     * 只是乙永远收不到。这类"看起来配好了其实没有"的错配，
     * 手机上没有任何办法自证，所以只能在源头上不让它发生。
     *
     * 只在不同目标之间切换时清：点同一台电脑会重走一遍这个方法，
     * 那种情况不能把用户刚敲的东西弄丢。
     */
    private fun clearTypedKeyIfTargetChanged(next: UUID) {
        if (selectedHubId != next) {
            keyView.setText("")
        }
    }

    /**
     * 密钥输入框只在该出现的时候出现。
     *
     * 已经记住的就不用再要——"配对一次"是这个设计能不能用的关键，
     * 每次开 App 都要人去电脑上翻密钥，等于没做。
     */
    private fun updateKeyVisibility() {
        val hubId = selectedHubId
        val remembered = hubId != null && pairings.keyFor(hubId) != null

        if (remembered) {
            keyHintView.visibility = View.VISIBLE
            keyHintView.text = "已记住这台电脑的密钥，不用再填。"
            keyView.visibility = View.GONE
            return
        }

        when (selectedSecurityMode) {
            SecurityMode.REQUIRED -> {
                keyHintView.visibility = View.VISIBLE
                keyHintView.text = getString(R.string.hub_requires_key)
                keyView.visibility = View.VISIBLE
            }

            SecurityMode.OPTIONAL -> {
                keyHintView.visibility = View.VISIBLE
                keyHintView.text = getString(R.string.hub_accepts_key)
                keyView.visibility = View.VISIBLE
            }

            else -> {
                keyHintView.visibility = View.GONE
                keyView.visibility = View.GONE
            }
        }
    }

    /**
     * 决定这次发送用不用加密、用哪把密钥，把结果放进 [pendingPsk]。
     *
     * 返回 false 表示**不能开始**，调用方必须直接返回。
     *
     * 这里刻意用"返回布尔"而不是"返回可空密钥"：可空有歧义 ——
     * "不需要加密"和"要加密但拿不到"会变成同一个 null。
     * 早先那版就是这样漏掉一种情况的：电脑要求加密、以前配过对，
     * 但用户这次手输了一个看不懂的密钥 —— 提示了一句"密钥看不懂"，
     * 然后**照样以明文开始发送**。在只收加密包的电脑上，那就是
     * 声音永远不来而双方都不报错。**报错和继续，只能选一个。**
     */
    private fun preparePsk(): Boolean {
        pendingPsk = null

        // 手填地址、没走发现 → 不知道对方是谁，也没法按身份派生，只能明文。
        val hubId = selectedHubId ?: return true

        val typed = keyView.text.toString().trim()
        if (typed.isNotEmpty()) {
            val parsed = SecureEnvelope.parse(typed)
            if (parsed == null) {
                Toast.makeText(this, getString(R.string.error_bad_key), Toast.LENGTH_LONG).show()
                return false
            }

            val name = candidates.firstOrNull { it.hubId == hubId }?.name.orEmpty()
            pairings.remember(hubId, parsed, name, hostView.text.toString().trim(), currentPort())
            Toast.makeText(this, getString(R.string.paired_ok), Toast.LENGTH_SHORT).show()
            pendingPsk = parsed
            // 记住之后把输入框收起来，并把里面的明文清掉：
            // 留着它会让界面和第 328 行那句"已记住，不用再填"自相矛盾，
            // 也让一串密钥在屏幕上多挂一会儿。
            keyView.setText("")
            updateKeyVisibility()
            return true
        }

        pairings.keyFor(hubId)?.let {
            pendingPsk = it
            return true
        }

        // 没填也没存过：只有对方明确要求加密时才算问题。
        // 明文模式（None）和"加密可选"（Optional）下不填是正常用法。
        if (selectedSecurityMode == SecurityMode.REQUIRED) {
            Toast.makeText(this, getString(R.string.error_key_needed), Toast.LENGTH_LONG).show()
            return false
        }
        return true
    }

    private fun currentPort(): Int =
        portView.text.toString().trim().toIntOrNull() ?: AudioCaptureService.DEFAULT_PORT

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).toInt()

    private fun onToggleClicked() {
        if (CaptureStatus.running) {
            startService(
                Intent(this, AudioCaptureService::class.java).apply {
                    action = AudioCaptureService.ACTION_STOP
                },
            )
            toggleButton.text = getString(R.string.button_start)
            return
        }

        if (hostView.text.toString().trim().isEmpty()) {
            Toast.makeText(this, getString(R.string.error_no_host), Toast.LENGTH_SHORT).show()
            return
        }

        // 先定下这次用不用加密。放在要录屏授权之前，是为了让"密钥不对"
        // 这种立刻能修的问题在用户还没被系统弹窗打扰时就报出来。
        if (!preparePsk()) {
            return
        }

        if (!ensurePermissions()) {
            pendingStart = true
            return
        }

        requestProjection()
    }

    /**
     * 回放捕获要录音权限，前台服务的通知在 Android 13 起要通知权限。
     * 两个一起要，用户只被问一轮。
     */
    private fun ensurePermissions(): Boolean {
        val needed = mutableListOf<String>()

        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            needed.add(Manifest.permission.RECORD_AUDIO)
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU &&
            checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            needed.add(Manifest.permission.POST_NOTIFICATIONS)
        }

        if (needed.isEmpty()) {
            return true
        }

        requestPermissions(needed.toTypedArray(), REQUEST_PERMISSIONS)
        return false
    }

    override fun onRequestPermissionsResult(
        requestCode: Int,
        permissions: Array<out String>,
        grantResults: IntArray,
    ) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults)
        if (requestCode != REQUEST_PERMISSIONS) {
            return
        }

        // 录音权限是硬性的；通知权限被拒只会让通知不显示，不拦开始。
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            pendingStart = false
            statusView.text = getString(R.string.error_no_record_permission)
            return
        }

        if (pendingStart) {
            pendingStart = false
            requestProjection()
        }
    }

    private fun requestProjection() {
        if (isFinishing || isDestroyed) {
            return
        }
        savePrefs()

        val manager = getSystemService(Context.MEDIA_PROJECTION_SERVICE) as MediaProjectionManager
        startActivityForResult(manager.createScreenCaptureIntent(), REQUEST_PROJECTION)
    }

    @Deprecated("没有 AndroidX 就得用这个老接口；Activity Result API 属于 AndroidX。")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)
        if (requestCode != REQUEST_PROJECTION) {
            return
        }

        if (resultCode != RESULT_OK || data == null) {
            statusView.text = getString(R.string.error_projection_denied)
            return
        }

        val port = portView.text.toString().trim().toIntOrNull()
        if (port == null || port !in 1..65_535) {
            statusView.text = getString(R.string.error_bad_port)
            return
        }

        // 流名称在协议里是 16 字节 ASCII，中文会在编码时抛异常。
        // 在这里拦下来，比让它变成捕获线程里的一条错误更早也更好懂。
        val streamName = streamView.text.toString().trim().ifBlank { AudioCaptureService.DEFAULT_STREAM }
        validateStreamName(streamName)?.let { problem ->
            statusView.text = problem
            Toast.makeText(this, problem, Toast.LENGTH_LONG).show()
            return
        }

        // 取成局部量：两个都要用，而它们是可变的成员属性，
        // 后面在 apply 块（一个 lambda）里读会拿不到智能转换。
        val psk = pendingPsk
        val hubId = selectedHubId
        pendingPsk = null

        val intent = Intent(this, AudioCaptureService::class.java).apply {
            action = AudioCaptureService.ACTION_START
            putExtra(AudioCaptureService.EXTRA_RESULT_CODE, resultCode)
            putExtra(AudioCaptureService.EXTRA_RESULT_DATA, data)
            putExtra(AudioCaptureService.EXTRA_HOST, hostView.text.toString().trim())
            putExtra(AudioCaptureService.EXTRA_PORT, port)
            putExtra(AudioCaptureService.EXTRA_STREAM, streamName)

            // 密钥按"电脑身份 + 密钥"**一起**传：捕获线程要拿身份去派生，
            // 没有身份就派不出和电脑一样的密钥。
            //
            // 这里必须两个都传或都不传，不能只传身份。只传身份会被捕获线程判成
            // "参数不成立"并停止 —— 而"从列表里选了电脑、没输密钥"在
            // "加密可选"的电脑上是最普通的明文用法，本来就该能正常发。
            // 早先这版正是这么写的（身份无条件传、密钥有才传），那条通路直接起不来。
            if (psk != null && hubId != null) {
                putExtra(AudioCaptureService.EXTRA_HUB_ID, hubId.toString())
                putExtra(AudioCaptureService.EXTRA_PSK, psk)
            }
        }

        // minSdk 是 30，直接用前台服务启动，避免服务退到后台被掐。
        startForegroundService(intent)
        toggleButton.text = getString(R.string.button_stop)
    }

    private fun renderStatus() {
        statusView.text = CaptureStatus.describe()

        val running = CaptureStatus.running
        toggleButton.text = getString(if (running) R.string.button_stop else R.string.button_start)

        // 运行中锁住目标，避免界面显示的地址和实际在用的一致不了。
        hostView.isEnabled = !running
        portView.isEnabled = !running
        streamView.isEnabled = !running
        keyView.isEnabled = !running
        findButton.isEnabled = !running && !searching
    }

    /**
     * 返回问题描述，没问题时返回 null。
     * 规则和 [VbanPacketWriter] 里的校验保持一致，改一处就要改另一处。
     */
    private fun validateStreamName(name: String): String? {
        if (name.length > VbanPacketWriter.STREAM_NAME_LENGTH) {
            return "流名称最多 ${VbanPacketWriter.STREAM_NAME_LENGTH} 个字符，当前 ${name.length} 个"
        }
        if (name.any { it.code !in 32..126 }) {
            return "流名称只能用英文字母、数字和常见符号（协议规定这个字段是 ASCII）"
        }
        return null
    }

    private fun savePrefs() {
        getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_HOST, hostView.text.toString().trim())
            .putString(KEY_PORT, portView.text.toString().trim())
            .putString(KEY_STREAM, streamView.text.toString().trim())
            .putString(KEY_HUB_ID, selectedHubId?.toString() ?: "")
            .apply()
    }

    /** 恢复上次选中的电脑，这样配对状态不会因为关掉 App 就丢。 */
    private fun restoreSelection() {
        val saved = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getString(KEY_HUB_ID, "").orEmpty()
        if (saved.isBlank()) {
            return
        }

        selectedHubId = runCatching { UUID.fromString(saved) }.getOrNull() ?: return
        // 没重新查过之前不知道它现在要求什么，按"可选"处理：
        // 存过密钥就照旧加密，没存过就明文，两边都能连上。
        selectedSecurityMode = SecurityMode.OPTIONAL
        updateKeyVisibility()
    }

    companion object {
        private const val PREFS_NAME = "audiohub-sender"
        private const val KEY_HOST = "host"
        private const val KEY_PORT = "port"
        private const val KEY_STREAM = "stream"
        private const val KEY_HUB_ID = "hubId"

        private const val REQUEST_PERMISSIONS = 1001
        private const val REQUEST_PROJECTION = 1002
        private const val REFRESH_INTERVAL_MILLIS = 500L
    }
}
