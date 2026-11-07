package com.audiohub.sender

import android.Manifest
import android.app.Activity
import android.app.AlertDialog
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Typeface
import android.media.projection.MediaProjectionConfig
import android.media.projection.MediaProjectionManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.MediaStore
import android.text.InputFilter
import android.text.InputType
import android.view.Gravity
import android.view.View
import android.view.ViewGroup
import android.view.WindowInsetsController
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.ImageView
import android.widget.LinearLayout
import android.widget.RadioButton
import android.widget.RadioGroup
import android.widget.ScrollView
import android.widget.TextView
import android.widget.Toast
import com.audiohub.protocol.ConnectionQr
import com.audiohub.protocol.DiscoveredHub
import com.audiohub.protocol.DiscoveryClient
import com.audiohub.protocol.QrPayload
import com.audiohub.protocol.SecureEnvelope
import com.audiohub.protocol.SecurityMode
import com.audiohub.protocol.SendModeSituation
import com.audiohub.protocol.SenderCredentials
import com.audiohub.protocol.ShortCode
import com.audiohub.protocol.resolveSendMode
import com.audiohub.protocol.senderCredentialsFor
import com.audiohub.vban.VbanPacketWriter
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.RGBLuminanceSource
import com.google.zxing.common.HybridBinarizer
import java.io.File
import java.util.UUID

// 配色只在这里定一次。界面全部用代码搭，"深一点的灰"散落在十几个 setTextColor 里
// 就再也调不动了。品牌蓝和电脑端、图标底色是同一个 #2563EB。
private val INK = Color.parseColor("#111827")
private val MUTED = Color.parseColor("#6B7280")
private val BG = Color.parseColor("#F5F6F8")
private val ACCENT = Color.parseColor("#2563EB")
private val SUCCESS = Color.parseColor("#1D9E75")
private val DANGER = Color.parseColor("#A32D2D")

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
    private lateinit var linkPlanView: TextView
    private lateinit var findButton: Button
    private lateinit var scanButton: Button
    private lateinit var codeButton: Button
    private lateinit var advancedContainer: LinearLayout
    private lateinit var toggleButton: Button
    private lateinit var statusView: TextView
    private lateinit var hubListView: LinearLayout
    private lateinit var updateButton: Button
    private lateinit var shareModeGroup: RadioGroup

    /** 整页的滚动容器。留着是为了把提示条滚进视野。 */
    private lateinit var rootScroll: ScrollView

    /** 页面内的提示条（替代会被截断的长 Toast）。 */
    private lateinit var noticeView: TextView

    /** "当前连接的是谁"那一行。 */
    private lateinit var targetView: TextView

    /** 密钥那块灰底卡片：里面两件东西都藏起来时，整块也要一起消失。 */
    private lateinit var keyBlock: LinearLayout

    private var shareWholeRadioId = 0

    private lateinit var pairings: PairedHubStore

    private val handler = Handler(Looper.getMainLooper())

    private val updater: Updater by lazy { Updater(this) }

    /** 权限被拒后要不要接着去要录屏授权。 */
    private var pendingStart = false

    /** 这次拍照扫码的临时照片；系统相机写完后由我们读取并删除。 */
    private var pendingScanFile: File? = null

    /** 选中的那台电脑。null 表示用户手填地址、没走发现。 */
    private var selectedHubId: UUID? = null

    /** 选中电脑对加密的要求，决定要不要出现密钥输入框。 */
    private var selectedSecurityMode = SecurityMode.NONE

    /** 最近一次查到的电脑。 */
    private var candidates: List<DiscoveredHub> = emptyList()

    /**
     * 见过名字的电脑（身份 → 名字）。
     *
     * 单播探测回来时会带着电脑的名字，但那台电脑不一定在 [candidates] 里
     * （用户是手填地址/输短码连的）。不记下来，界面上就只剩一个 IP，
     * 而他刚才明明在电脑屏幕上看到过那台电脑的名字。
     */
    private val hubNames = HashMap<UUID, String>()

    private var searching = false

    /** 探测慢时才弹的那条"正在确认…"提示；探测结束要能撤销它。 */
    private var progressHint: Runnable? = null

    /** 提示条里现在是不是那条进度提示（是的话，探测一结束就该收起来）。 */
    private var noticeIsProgress = false

    /** 运行中上一次"电脑还要求加密吗"的检查时间。 */
    private var lastModeCheckAt = 0L

    /** 已经就"电脑改了设置"提示过一次；每次重新开始发送时复位。 */
    private var modeDriftNotified = false

    /**
     * 这次发送真正要交给服务的两条 extra（电脑身份 + 密钥）。
     *
     * 这里刻意不存"密钥"再让发送处自己写 `if (psk != null && hubId != null)`：
     * 那样"要不要加密"就多出第二处实现，而上一版正是丢在这里 ——
     * 界面把结论说对了，发包动作却没读它，于是"界面说明文、线上跑密文"。
     * 现在结论由 [senderCredentialsFor] 一处产出，发送处只负责原样传给服务。
     */
    private var pendingCredentials: SenderCredentials? = null

    private val refresher = object : Runnable {
        override fun run() {
            renderStatus()
            handler.postDelayed(this, REFRESH_INTERVAL_MILLIS)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 状态栏跟着页面底色走，图标转深色。不然去掉 ActionBar 之后，
        // 顶上会剩一条和页面不同色的横带，看着像少画了一块。
        window.statusBarColor = BG
        window.insetsController?.setSystemBarsAppearance(
            WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS,
            WindowInsetsController.APPEARANCE_LIGHT_STATUS_BARS,
        )

        pairings = PairedHubStore(this)
        // 兜底恢复：若上次静音后进程被杀（记录残留），打开 App 时就把音量恢复回去。
        // 之前只放在服务 onCreate 里，但"杀后台后重开"走的是主界面、服务根本不启动，
        // 所以必须在这里也做一次（逻辑抽在 MuteRecovery.kt，两端共用一份键）。
        recoverVolumeIfNeeded(this)
        setContentView(buildUi())
        restoreSelection()
        renderHubList()

        // 恢复出来的模式是"上次启动时的说法"，而电脑随时能改（现在是热切换）。
        // 所以在后台问一次，问到了就把显示纠正过来 —— 否则用户会在没点开始之前
        // 看到一句已经过时的"将加密发送/明文发送"。
        val restoredHost = hostView.text.toString().trim()
        if (selectedHubId != null && restoredHost.isNotEmpty()) {
            identifyAddress(restoredHost) { }
        }
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
        // 探测的进度提示也要撤：Activity 关掉之后它再跑一次只是往一个已经废掉的
        // 视图上写字，虽然不崩，但会让人以为是新提示。
        progressHint?.let { handler.removeCallbacks(it) }
        progressHint = null
        super.onDestroy()
    }

    private fun buildUi(): View {
        val prefs = getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

        val column = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(18), dp(20), dp(18), dp(28))
        }

        // ---- 抬头 ------------------------------------------------------------
        // 用一个真正的抬头（图标 + 标题 + 副标题）替掉系统 ActionBar 的那条灰带。
        // 图标就是启动器图标，和电脑端托盘、安卓桌面看到的是同一个标志。
        val header = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            gravity = Gravity.CENTER_VERTICAL
            setPadding(0, 0, 0, dp(16))
        }
        header.addView(
            ImageView(this).apply {
                setImageResource(R.mipmap.ic_launcher)
                layoutParams = LinearLayout.LayoutParams(dp(42), dp(42)).apply { rightMargin = dp(12) }
            },
        )

        val titles = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        titles.addView(
            TextView(this).apply {
                text = getString(R.string.app_title)
                textSize = 22f
                typeface = Typeface.DEFAULT_BOLD
                setTextColor(INK)
            },
        )
        titles.addView(
            TextView(this).apply {
                text = getString(R.string.app_subtitle)
                textSize = 13f
                setTextColor(MUTED)
                setPadding(0, dp(3), 0, 0)
            },
        )
        header.addView(titles)
        column.addView(header)

        // ---- 当前连接 + 开始 / 停止 -------------------------------------------
        // 连哪台、加不加密、能不能开始 —— 这三件事决定用户下一步按什么，
        // 所以放一屏的最上面。以前它们散在长页面里，配上候选列表一撑就滚不动了。
        val statusCard = addCard(column, getString(R.string.section_target), topMargin = 0)

        targetView = TextView(this).apply {
            text = getString(R.string.target_none)
            textSize = 16f
            setTextColor(INK)
        }
        statusCard.addView(targetView)

        // 点开始之前就说清这次会加密还是明文：等连上再看已经晚了。
        linkPlanView = TextView(this).apply {
            textSize = 13f
            setTextColor(MUTED)
            setPadding(0, dp(5), 0, dp(14))
        }
        statusCard.addView(linkPlanView)

        toggleButton = primaryButton(getString(R.string.button_start)).apply {
            setOnClickListener { onToggleClicked() }
        }
        statusCard.addView(toggleButton, fullWidth())

        // ---- 提示条 ----------------------------------------------------------
        // 替代长 Toast。用户报过"提示信息太长显示不全"：Toast 会被单行截断，
        // 而这里的话恰恰不能截断 —— "这台电脑要求加密，请先填密钥"截掉一半就没有意义了。
        // 放在页面里能换行、能一直停着，看完点一下收掉。
        noticeView = TextView(this).apply {
            textSize = 13f
            setTextColor(INK)
            setBackgroundResource(R.drawable.bg_notice)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            visibility = View.GONE
            isClickable = true
            setOnClickListener { visibility = View.GONE }
        }
        column.addView(noticeView, fullWidth().apply { topMargin = dp(10) })

        // ---- 连接电脑 --------------------------------------------------------
        val connectCard = addCard(column, getString(R.string.section_connect))

        findButton = primaryButton(getString(R.string.button_find_hub)).apply {
            setOnClickListener { startDiscovery() }
        }
        connectCard.addView(findButton, fullWidth())

        // 自动发现不通时的两条快速通道。它们比手打地址省事，
        // 而且在"路由器不转发广播"的网络里往往还能走通，所以摆在显眼处。
        val manualRow = LinearLayout(this).apply { orientation = LinearLayout.HORIZONTAL }

        scanButton = secondaryButton(getString(R.string.button_scan_qr)).apply {
            setOnClickListener { startScan() }
        }
        codeButton = secondaryButton(getString(R.string.button_short_code)).apply {
            setOnClickListener { askForShortCode() }
        }

        manualRow.addView(
            scanButton,
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f),
        )
        manualRow.addView(
            codeButton,
            LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f).apply { leftMargin = dp(8) },
        )
        connectCard.addView(manualRow, fullWidth().apply { topMargin = dp(8) })

        // 候选列表。每次查完重建：电脑可能来了又走，留一份陈旧的列表比没有更糟。
        hubListView = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL }
        connectCard.addView(hubListView, fullWidth())

        // ---- 密钥（只在需要时出现）-------------------------------------------
        // 必须放在"高级设置"外面：电脑要求加密时，用户要一眼看到"该填密钥了"，
        // 而不是先去展开一个写着"高级"的折叠块再找。
        val keyCard = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.bg_card_sunken)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            visibility = View.GONE
        }

        keyHintView = TextView(this).apply {
            textSize = 13f
            setTextColor(INK)
            visibility = View.GONE
            setPadding(0, 0, 0, dp(6))
        }
        keyCard.addView(keyHintView)

        keyView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            hint = "例如 ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2"
            textSize = 14f
            visibility = View.GONE
        }
        keyCard.addView(keyView, fullWidth())

        // 边输密钥边更新"链路"提示，不用等点开始才知道会不会加密。
        keyView.addTextChangedListener(object : android.text.TextWatcher {
            override fun beforeTextChanged(s: CharSequence?, start: Int, count: Int, after: Int) = Unit
            override fun onTextChanged(s: CharSequence?, start: Int, before: Int, count: Int) {
                if (::linkPlanView.isInitialized) {
                    updateLinkPlan()
                }
            }
            override fun afterTextChanged(s: android.text.Editable?) = Unit
        })

        keyBlock = keyCard
        column.addView(keyBlock, fullWidth().apply { topMargin = dp(10) })

        // ---- 高级设置 --------------------------------------------------------
        val advancedCard = addCard(column, null)

        advancedContainer = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            visibility = View.GONE
        }

        val advancedToggle = TextView(this).apply {
            text = getString(R.string.advanced_title_closed)
            textSize = 14f
            setTextColor(MUTED)
            isClickable = true
            setPadding(0, dp(2), 0, dp(2))
            setOnClickListener {
                val show = advancedContainer.visibility != View.VISIBLE
                advancedContainer.visibility = if (show) View.VISIBLE else View.GONE
                text = getString(if (show) R.string.advanced_title_open else R.string.advanced_title_closed)
            }
        }
        advancedCard.addView(advancedToggle)
        advancedCard.addView(advancedContainer, fullWidth())

        advancedContainer.addView(fieldLabel("电脑 IP（点上面找到的电脑会自动填）"))
        hostView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_URI
            hint = "例如 192.168.1.5"
            textSize = 14f
            setText(prefs.getString(KEY_HOST, ""))
        }
        advancedContainer.addView(hostView, fullWidth())

        advancedContainer.addView(fieldLabel("端口（保持 6980，除非电脑端改过）"))
        portView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_NUMBER
            textSize = 14f
            setText(prefs.getString(KEY_PORT, AudioCaptureService.DEFAULT_PORT.toString()))
        }
        advancedContainer.addView(portView, fullWidth())

        advancedContainer.addView(fieldLabel("流名称（电脑端默认不校验，留空即用 AudioHub）"))
        streamView = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT
            textSize = 14f
            setText(prefs.getString(KEY_STREAM, AudioCaptureService.DEFAULT_STREAM))
        }
        advancedContainer.addView(streamView, fullWidth())

        // 共享来源：决定系统授权弹窗里默认共享"整个屏幕"还是"单个应用"。
        // 这只是 Android 14 起"预填默认项"的提示，真正的授权确认仍需用户点。
        advancedContainer.addView(fieldLabel(getString(R.string.share_mode_label)))
        shareModeGroup = RadioGroup(this).apply { orientation = RadioGroup.HORIZONTAL }

        val wholeRadio = RadioButton(this).apply {
            id = View.generateViewId()
            text = getString(R.string.share_mode_whole)
            textSize = 14f
        }
        val appRadio = RadioButton(this).apply {
            id = View.generateViewId()
            text = getString(R.string.share_mode_app)
            textSize = 14f
        }
        shareWholeRadioId = wholeRadio.id
        shareModeGroup.addView(wholeRadio)
        shareModeGroup.addView(appRadio)
        val savedShare = prefs.getString(KEY_SHARE_MODE, SHARE_WHOLE)
        if (savedShare == SHARE_APP) appRadio.isChecked = true else wholeRadio.isChecked = true
        advancedContainer.addView(shareModeGroup)

        // ---- 运行状态 --------------------------------------------------------
        // 这一块是出问题时唯一能拿出来的证据（发了多少包、峰值多大），
        // 所以留在页面上、可选中复制，而不是藏在日志里。
        val stateCard = addCard(column, getString(R.string.section_status))
        statusView = TextView(this).apply {
            typeface = Typeface.MONOSPACE
            textSize = 12f
            setTextColor(MUTED)
            setTextIsSelectable(true)
        }
        stateCard.addView(statusView, fullWidth())

        // ---- 页脚 ------------------------------------------------------------
        column.addView(
            TextView(this).apply {
                textSize = 12f
                setTextColor(MUTED)
                text = getString(R.string.usage_note)
                setPadding(dp(2), dp(16), dp(2), dp(10))
            },
        )

        updateButton = secondaryButton(getString(R.string.button_check_update)).apply {
            setOnClickListener { onCheckUpdateClicked() }
        }
        column.addView(updateButton, fullWidth().apply { topMargin = dp(4) })

        // 密钥没能被硬件保护时要说出来。静默降级等于假装安全。
        pairings.storageNote?.let { note ->
            column.addView(
                TextView(this).apply {
                    textSize = 12f
                    setTextColor(DANGER)
                    text = getString(R.string.storage_note_prefix, note)
                    setPadding(dp(2), dp(10), dp(2), 0)
                },
            )
        }

        // 整页可滚 —— 这是上次"自动发现一出来，下面的按钮就再也看不见"的正面修法。
        // 根视图是 ScrollView，内容多长都能滚到底，而不是让中间某个控件去抢高度。
        return ScrollView(this).apply {
            setBackgroundColor(BG)
            isFillViewport = true
            addView(
                column,
                FrameLayout.LayoutParams(
                    FrameLayout.LayoutParams.MATCH_PARENT,
                    FrameLayout.LayoutParams.WRAP_CONTENT,
                ),
            )
        }.also { rootScroll = it }
    }

    // ------------------------------------------------------------ 样式零件
    //
    // 界面全部用代码搭（这个工程刻意没有任何 AndroidX 依赖），
    // 所以"卡片长什么样、主按钮长什么样"必须各只有一处定义。
    // 以前每个控件各自设边距和字号，改一次颜色要找十几处。

    /** 一张白卡片。分组靠卡片，不靠一堆分隔线。 */
    private fun addCard(parent: LinearLayout, title: String?, topMargin: Int = 12): LinearLayout {
        val card = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.bg_card)
            setPadding(dp(16), dp(14), dp(16), dp(16))
        }

        if (!title.isNullOrEmpty()) {
            card.addView(
                TextView(this).apply {
                    text = title
                    textSize = 12f
                    setTextColor(MUTED)
                    setPadding(0, 0, 0, dp(10))
                },
            )
        }

        parent.addView(
            card,
            LinearLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.WRAP_CONTENT,
            ).apply { this.topMargin = dp(topMargin) },
        )

        return card
    }

    /** 一屏只有一个主按钮：它永远是"下一步该按的那个"。 */
    private fun primaryButton(text: String): Button = Button(this).apply {
        this.text = text
        textSize = 16f
        isAllCaps = false
        setTextColor(Color.WHITE)
        setBackgroundResource(R.drawable.bg_btn_primary)
        stateListAnimator = null
        minHeight = dp(50)
        setPadding(dp(16), dp(13), dp(16), dp(13))
    }

    private fun secondaryButton(text: String): Button = Button(this).apply {
        this.text = text
        textSize = 14f
        isAllCaps = false
        setTextColor(ACCENT)
        setBackgroundResource(R.drawable.bg_btn_secondary)
        stateListAnimator = null
        minHeight = dp(44)
        setPadding(dp(12), dp(10), dp(12), dp(10))
    }

    private fun fullWidth(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
        )

    private fun fieldLabel(text: String): TextView =
        TextView(this).apply {
            this.text = text
            textSize = 12f
            setTextColor(MUTED)
            setPadding(0, dp(10), 0, dp(4))
        }

    /**
     * 把一条提示显示在页面上，而不是弹一个几秒就消失、还会被截断的 Toast。
     *
     * 界面还没搭好时（极端情况）退回 Toast：提示宁可丑，也不能丢。
     */
    private fun showNotice(message: String) {
        if (!::noticeView.isInitialized) {
            Toast.makeText(this, message, Toast.LENGTH_LONG).show()
            return
        }

        noticeView.text = message
        noticeView.visibility = View.VISIBLE
        // 任何一条真实提示都覆盖掉"正在确认"的标记，免得探测结束时把它误当进度提示收掉。
        noticeIsProgress = false

        // 滚到提示那儿：提示条在状态卡片下面，手机上可能正好在屏幕外，
        // 那样"报了错却没人看见"和没报是一样的。
        if (::rootScroll.isInitialized) {
            rootScroll.post { rootScroll.smoothScrollTo(0, (noticeView.top - dp(8)).coerceAtLeast(0)) }
        }
    }

    /** 列表里的小标题（"已配对的电脑" / "查到的电脑"）。 */
    private fun label(text: String): TextView =
        TextView(this).apply {
            this.text = text
            textSize = 12f
            setTextColor(MUTED)
            setPadding(dp(2), dp(10), 0, dp(2))
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
                    showNotice(msg)
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
                hubListView.addView(hubRow(text, "点一下选中") { selectPaired(pairing) }, rowParams())
            }
        }

        if (candidates.isNotEmpty()) {
            hubListView.addView(label("查到的电脑"))
            for (hub in candidates) {
                hubListView.addView(
                    hubRow(hub.label, describeSecurity(hub.securityMode)) { selectHub(hub) },
                    rowParams(),
                )
            }
        }
    }

    private fun rowParams(): LinearLayout.LayoutParams =
        LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT,
            ViewGroup.LayoutParams.WRAP_CONTENT,
        ).apply { topMargin = dp(6) }

    /**
     * 列表里可点的一行：上面一行名字、下面一行说明。
     *
     * 以前是"名字\n说明"塞进一个 TextView，靠换行符排版 —— macOS 式的大小字号
     * 根本没法用，两行永远一样重。现在上下两行分开，名字醒目、说明退到灰色。
     */
    private fun hubRow(title: String, subtitle: String, onClick: () -> Unit): View {
        val row = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundResource(R.drawable.bg_row)
            setPadding(dp(14), dp(12), dp(14), dp(12))
            isClickable = true
            setOnClickListener { onClick() }
        }

        row.addView(
            TextView(this).apply {
                text = title
                textSize = 15f
                setTextColor(INK)
            },
        )
        row.addView(
            TextView(this).apply {
                text = subtitle
                textSize = 12f
                setTextColor(MUTED)
                setPadding(0, dp(3), 0, 0)
            },
        )

        return row
    }

    private fun describeSecurity(mode: Int): String = when (mode) {
        SecurityMode.REQUIRED -> "要求加密发送，需要输一次密钥"
        SecurityMode.OPTIONAL -> "支持加密发送，密钥可选"
        else -> "明文发送，不需要密钥"
    }

    private fun selectHub(hub: DiscoveredHub) {
        clearTypedKeyIfTargetChanged(hub.hubId)
        if (hub.name.isNotBlank()) {
            hubNames[hub.hubId] = hub.name
        }
        selectedHubId = hub.hubId
        selectedSecurityMode = hub.securityMode
        hostView.setText(hub.host)
        portView.setText(hub.vbanPort.toString())
        updateKeyVisibility()
        renderHubList()
    }

    private fun selectPaired(pairing: PairedHubStore.Pairing) {
        clearTypedKeyIfTargetChanged(pairing.hubId)
        if (pairing.name.isNotBlank()) {
            hubNames[pairing.hubId] = pairing.name
        }
        selectedHubId = pairing.hubId
        selectedSecurityMode = advertisedModeFor(pairing.hubId)
        hostView.setText(pairing.host)
        portView.setText(pairing.port.toString())
        updateKeyVisibility()
        renderHubList()
    }

    /**
     * 单播问一次"这个地址上是哪台电脑、要不要加密"，问到了就填进界面，问完再回调。
     *
     * 为什么不能省：短码只编进一个地址，手填地址更是只有地址。而加密必须用**电脑身份**
     * 派生密钥（两端派生的密钥要一模一样），还得知道电脑到底要不要加密。
     * 少了这两样就会变成用户报的那个现象 —— 填了配对密钥仍然连不上，
     * 而提示指向错误的方向（"好像是无法找到电脑"）。
     *
     * 电脑端对任何来源的 Probe 都应答，所以单播走得通；这也顺手绕开
     * "路由器不转发广播"，而短码本来正是为那种环境准备的。
     *
     * @param onSettled 探测结束（无论成没成）后在主线程回调，参数就是探测结果
     *   （null = 没有应答）。**一定会被调用一次**，否则"点开始"在探测失败时
     *   就永远没有下文了。把结果交回去而不是让调用方回头看
     *   [selectedHubId]：那个字段在探测失败时仍是上一次的旧值，
     *   拿它判断成败会得出"成功"的结论 —— 正是"同一个判定写两处"的老毛病。
     */
    private fun identifyAddress(host: String, onSettled: (DiscoveredHub?) -> Unit) {
        toggleButton.isEnabled = false

        // 上一次探测留下的进度提示还没撤，先撤掉：连点两次时不能让旧的那条盖住新的结果。
        progressHint?.let { handler.removeCallbacks(it) }

        // 局域网往返是毫秒级，所以正常情况下不该看到任何"正在确认"。
        // 300ms 后才提示：既不给每次点开始都闪一句噪音，也不至于让网络不对的人
        // 面对一个按下去没反应的按钮。
        val hint = Runnable {
            showNotice(getString(R.string.identifying_hub, host))
            noticeIsProgress = true
        }
        progressHint = hint
        handler.postDelayed(hint, 300L)

        Thread {
            val hub = runCatching { DiscoveryClient().probeAddress(host, timeoutMs = 1_200L) }.getOrNull()

            handler.post {
                progressHint?.let { handler.removeCallbacks(it) }
                progressHint = null
                toggleButton.isEnabled = true
                if (isFinishing || isDestroyed) {
                    return@post
                }

                // 进度提示只是"还在问"，问完了就该消失 —— 否则它会一直挂在页面上，
                // 看起来像卡住了。真正的结果由 onSettled 自己决定要不要说。
                if (noticeIsProgress) {
                    noticeView.visibility = View.GONE
                    noticeIsProgress = false
                }

                // 探测期间用户可能已经改了地址。只接受"问的就是当前这个地址"的结果，
                // 否则会把甲的电脑身份填到乙的地址上 —— 那是最坏的一种错配：
                // 用甲的密钥去连乙，界面、日志、音量全都正常，只是永远没有声音。
                val accepted = hub?.takeIf { hostView.text.toString().trim() == host }
                if (accepted != null) {
                    applyIdentifiedHub(accepted)
                }
                onSettled(accepted)
            }
        }.apply { isDaemon = true }.start()
    }

    /**
     * 运行中每隔几秒问一次电脑"你现在还要加密吗"，发现和正在发的对不上就提示重新开始。
     *
     * 为什么必须查：电脑端现在是热切换的。如果把加密从「关闭」改成「仅加密」，
     * 正在发的明文包会被拒（电脑侧有计数，托盘会说"有设备还没配密钥"）；
     * 反过来从「仅加密」改成「关闭」，正在发的密文会被当成读不懂的包**整包丢掉**，
     * 而电脑那边连计数都没有 —— 手机这头只表现为"突然没声音了"，两边都零报错。
     *
     * 为什么只能"提示"不能自己重启：安卓的录屏授权令牌是一次性的，
     * 想重新抓声音必须重新弹一次系统确认框。所以能做的是把原因说清楚，
     * 让用户点一下停止、再点一下开始。
     */
    private fun maybeRecheckMode() {
        if (!CaptureStatus.running || modeDriftNotified) {
            return
        }

        val now = System.currentTimeMillis()
        if (now - lastModeCheckAt < MODE_RECHECK_MILLIS) {
            return
        }
        lastModeCheckAt = now

        val host = hostView.text.toString().trim()
        val hubId = selectedHubId ?: return
        if (host.isEmpty()) {
            return
        }

        Thread {
            val hub = runCatching { DiscoveryClient().probeAddress(host, timeoutMs = 1_000L) }.getOrNull()
                ?: return@Thread

            handler.post {
                if (isFinishing || isDestroyed || !CaptureStatus.running) {
                    return@post
                }

                // 地址上换了另一台电脑，不是"改了设置"，这次不提示。
                if (hub.hubId != hubId) {
                    return@post
                }

                val sendingEncrypted = CaptureStatus.encrypted
                val drift = when (hub.securityMode) {
                    SecurityMode.REQUIRED -> !sendingEncrypted
                    SecurityMode.NONE -> sendingEncrypted
                    else -> false
                }

                if (drift) {
                    modeDriftNotified = true
                    // 顺手把已知模式更新掉：这样即使用户直接点停止再开始，也是新的值。
                    selectedSecurityMode = hub.securityMode
                    showNotice(getString(R.string.notice_mode_changed))
                }
            }
        }.apply { isDaemon = true }.start()
    }

    /**
     * 把探测到的电脑信息填进界面。
     *
     * 只写 [selectedSecurityMode]（电脑自己广播的原话），不在这里改写它 ——
     * "要不要加密"仍然只有 [resolveSendMode] 一处判定。
     */
    private fun applyIdentifiedHub(hub: DiscoveredHub) {
        // 先清掉"上一个目标留下的"密钥，再改身份。顺序反了就没用了。
        //
        // 不清的后果是本项目最贵的那种失败：给甲电脑敲过密钥，接着手填/短码连乙，
        // 乙的身份 + 甲的密钥 → 两边派生出的 AES 密钥不同 → 电脑整包丢弃、
        // 手机照样显示"加密发送中"，**双方零报错，只是没有声音**。
        // 点列表里的电脑时走的是 selectHub/selectPaired，它们都有这一句；
        // 单播探测这条路是后加的，之前漏了。
        clearTypedKeyIfTargetChanged(hub.hubId)
        if (hub.name.isNotBlank()) {
            hubNames[hub.hubId] = hub.name
        }

        selectedHubId = hub.hubId
        selectedSecurityMode = hub.securityMode

        // 端口用电脑自己报的那个：用户在高级里改过、或短码默认值不对时，
        // 电脑的答案比我们的猜测可靠。
        if (hub.vbanPort in 1..65_535) {
            portView.setText(hub.vbanPort.toString())
        }

        updateKeyVisibility()
        renderHubList()
        savePrefs()
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
        if (::linkPlanView.isInitialized) {
            updateLinkPlan()
        }

        val hubId = selectedHubId
        val remembered = hubId != null && pairings.keyFor(hubId) != null

        when {
            remembered -> {
                keyHintView.visibility = View.VISIBLE
                keyHintView.text = "已记住这台电脑的密钥，不用再填。"
                keyView.visibility = View.GONE
            }

            selectedSecurityMode == SecurityMode.REQUIRED -> {
                keyHintView.visibility = View.VISIBLE
                keyHintView.text = getString(R.string.hub_requires_key)
                keyView.visibility = View.VISIBLE
            }

            selectedSecurityMode == SecurityMode.OPTIONAL -> {
                keyHintView.visibility = View.VISIBLE
                keyHintView.text = getString(R.string.hub_accepts_key)
                keyView.visibility = View.VISIBLE
            }

            else -> {
                keyHintView.visibility = View.GONE
                keyView.visibility = View.GONE
            }
        }

        // 里面两件东西都藏起来时，这张灰底卡片也要一起消失 ——
        // 否则屏幕上会留一块空的灰条，看着像加载失败。
        if (::keyBlock.isInitialized) {
            val needed = keyHintView.visibility == View.VISIBLE || keyView.visibility == View.VISIBLE
            keyBlock.visibility = if (needed) View.VISIBLE else View.GONE
        }
    }

    /**
     * 在点"开始"之前就说清这次会加密还是明文。
     *
     * 只按"对方要求 + 有没有密钥"给出说明；真正的判定仍只在 [preparePsk] 里，
     * 这里刻意不复制那条判定 —— 同一个规则写在两处会各自演化（这个项目栽过四次）。
     */
    private fun updateLinkPlan() {
        if (!::linkPlanView.isInitialized || !::hostView.isInitialized) {
            return
        }

        updateTargetLine()

        val hubId = selectedHubId
        val remembered = hubId != null && pairings.keyFor(hubId) != null
        val typed = keyView.text.toString().trim().isNotEmpty()

        if (hubId == null && hostView.text.toString().trim().isEmpty()) {
            linkPlanView.text = "链路：请先选择电脑，或填地址 / 短码 / 扫码"
            linkPlanView.setTextColor(DANGER)
            return
        }

        // 判定只有一处：[resolveSendMode]。这里只负责把结论翻成人话。
        // 早先这里是四条 if，用的是"本机被重置后的模式"，于是电脑说"仅加密"、
        // 而扫到的二维码里没密钥时，界面会显示"明文发送" —— 用户以为许可，实际发不响。
        val plan = resolveSendMode(
            advertised = advertisedModeOrNull(),
            hasKey = remembered || typed,
            hasIdentity = hubId != null,
        )

        val text: String = when (plan.situation) {
            SendModeSituation.Encrypted ->
                "链路：将加密发送（AES-256-GCM）"
            SendModeSituation.MissingKey ->
                "链路：此电脑要求加密，需先填密钥，否则发出去会被拒"
            SendModeSituation.MissingIdentity ->
                "链路：此电脑要求加密，但还不知道它是哪台电脑 —— 请从列表里选或扫码，才能加密"
            SendModeSituation.PlaintextOnly ->
                "链路：明文发送（这台电脑关闭了加密）"
            SendModeSituation.PlaintextOptional ->
                "链路：明文发送（此电脑支持加密，填密钥后改为加密）"
            SendModeSituation.UnknownHub ->
                "链路：明文发送（手填地址，未与电脑配对，无法加密）"
        }

        linkPlanView.text = text
        linkPlanView.setTextColor(if (plan.encrypt) SUCCESS else DANGER)
    }

    /**
     * 更新"当前连接"那一行：有名字就显示名字 + 地址，只有地址就显示地址。
     *
     * 从 [updateLinkPlan] 里调，是因为两者关心的都是"目标变没变"——
     * 每次目标一变，链路和这一行都得跟着变，分两个入口迟早有一处漏掉。
     */
    private fun updateTargetLine() {
        if (!::targetView.isInitialized || !::hostView.isInitialized) {
            return
        }

        val host = hostView.text.toString().trim()
        if (host.isEmpty()) {
            targetView.text = getString(R.string.target_none)
            return
        }

        val id = selectedHubId
        val name = id?.let { wanted ->
            hubNames[wanted]
                ?: candidates.firstOrNull { it.hubId == wanted }?.name
                ?: pairings.all().firstOrNull { it.hubId == wanted }?.name
        }

        targetView.text = if (name.isNullOrBlank()) {
            host
        } else {
            getString(R.string.target_ready, name, host)
        }
    }

    /**
     * 决定这次发送用不用加密、用哪把密钥，把结论放进 [pendingCredentials]。
     *
     * 返回 false 表示**不能开始**，调用方必须直接返回。
     *
     * 这里刻意用"返回布尔"而不是"返回可空密钥"：可空有歧义 ——
     * "不需要加密"和"要加密但拿不到"会变成同一个 null。
     * 早先那版就是这样漏掉一种情况的：电脑要求加密、以前配过对，
     * 但用户这次手输了一个看不懂的密钥 —— 提示了一句"密钥看不懂"，
     * 然后**照样以明文开始发送**。在只收加密包的电脑上，那就是
     * 声音永远不来而双方都不报错。**报错和继续，只能选一个。**
     *
     * 所有出口都经过 [resolveSendMode]：这里不再有"提前 return 把判定绕过去"的分支。
     * 上一版的三条 return 里只有最后一条问了判定，前两条拿着密钥就直接放行，
     * 于是"电脑关掉加密"这条结论到了界面、却没到发送动作。
     */
    private fun preparePsk(): Boolean {
        pendingCredentials = null

        val hubId = selectedHubId
        val typed = keyView.text.toString().trim()

        // 先看手输的密钥看不看得懂。看不懂就停在这里 —— 报错和继续只能选一个。
        val typedKey = if (typed.isEmpty()) {
            null
        } else {
            SecureEnvelope.parse(typed) ?: run {
                showNotice(getString(R.string.error_bad_key))
                return false
            }
        }

        // 手输的优先于记住的：用户这次填了，就是这次要用。
        val usableKey = typedKey ?: if (hubId != null) pairings.keyFor(hubId) else null

        // 判定只有一处。界面文案、这里、以及最后塞进 Intent 的 extra，全都问它。
        val plan = resolveSendMode(
            advertised = advertisedModeOrNull(),
            hasKey = usableKey != null,
            hasIdentity = hubId != null,
        )

        if (!plan.canStart) {
            showNotice(getString(R.string.error_key_needed))
            return false
        }

        if (typedKey != null) {
            if (hubId == null) {
                // 手填地址 + 手输密钥：没有电脑身份就派生不出密钥，这次只能明文。
                // 必须停下来明说，否则用户会以为"我填了密钥，所以是加密的" ——
                // 那正是这套加密最该避免的错觉。
                showNotice(getString(R.string.error_key_without_identity))
                return false
            }

            // 记住它，下次换到要求加密的电脑上不用再抄一遍。
            // 注意：记住 ≠ 这次要用 —— 用不用由上面的 plan 决定。
            val name = candidates.firstOrNull { it.hubId == hubId }?.name.orEmpty()
            pairings.remember(hubId, typedKey, name, hostView.text.toString().trim(), currentPort())
            Toast.makeText(this, getString(R.string.paired_ok), Toast.LENGTH_SHORT).show()

            // 记住之后把输入框收起来，并把里面的明文清掉：
            // 留着它会让界面和"已记住，不用再填"那句自相矛盾，
            // 也让一串密钥在屏幕上多挂一会儿。
            keyView.setText("")
            updateKeyVisibility()
        }

        // 唯一的出口：明文就是两样都没有，加密就是两样都有。
        pendingCredentials = senderCredentialsFor(plan, hubId?.toString(), usableKey)
        return true
    }

    /**
     * 电脑自己广播的安全模式；没听到过广播就按"加密可选"。
     *
     * 单独抽出来是为了守住一条纪律：[selectedSecurityMode] **只存电脑的原话**，
     * 不允许被"扫到了什么 / 有没有密钥 / 短码查没查到"改写成别的值。
     * 一旦改写，界面就会把"这台电脑要求加密"说成"明文发送" —— 用户看到的是许可，
     * 实际结果是发出去全被拒。要不要加密由 [resolveSendMode] 一处判定。
     */
    private fun advertisedModeFor(hubId: UUID?): Int {
        if (hubId == null) {
            return SecurityMode.NONE
        }

        return candidates.firstOrNull { it.hubId == hubId }?.securityMode
            ?: SecurityMode.OPTIONAL
    }

    /**
     * "这台电脑要求什么"——**只有在知道是哪台电脑时才有答案**。
     *
     * [selectedSecurityMode] 可能残留上一台电脑的值（短码/手填地址拿不到身份，
     * 换目标时又没清）。所以凡是要拿它做判定，都必须先过这一道：
     * 没有身份就返回 null，也就是"不知道"，由 [resolveSendMode] 按"未听到广播"处理。
     *
     * 抽出来的理由和 [resolveSendMode] 一样：这个判断以前散在界面和发送准备两处，
     * 只改一处就会让两边给出不同答案。
     */
    private fun advertisedModeOrNull(): Int? =
        if (selectedHubId == null) null else selectedSecurityMode

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

        // 每次开始前都单播问一次"你是谁、要不要加密"。
        //
        // 两个理由，缺一不可：
        //  · 手填地址 / 短码时，这是**唯一**能拿到身份和模式的途径（没身份就派不出密钥）；
        //  · 已经选过电脑时，模式也必须重新读 —— 电脑那边随时能改，而且现在是免重启生效的，
        //    本机记住的可能是上一次的值，甚至是上次启动时恢复出来的值。用户报的
        //    "电脑关掉加密后手机还在加密"，根子就在这里。
        //
        // 问不到就沿用已知的：探测失败不清 [selectedHubId]，所以原本能用的加密配置不会因为
        // "这次它没应答"而被抹掉。
        identifyAddress(hostView.text.toString().trim()) { continueStart() }
    }

    /**
     * 真正去"定加密方式 + 要权限 + 要录屏授权"。
     *
     * 从 [onToggleClicked] 里拆出来，是因为"开始"现在要等一次单播确认才继续。
     * **只有这一个入口**：探测成功、探测失败都走它，否则"探测失败之后怎么办"
     * 就会变成第二条判定规则 —— 而这个项目已经为"同一个判定写两处"付过五次代价。
     */
    private fun continueStart() {
        // 先定下这次用不用加密。放在要录屏授权之前，是为了让"密钥不对"
        // 这种立刻能修的问题在用户还没被系统弹窗打扰时就报出来。
        if (!preparePsk()) {
            return
        }

        // 新的一次发送：把"已经提示过模式变了"的标记清掉。
        modeDriftNotified = false
        lastModeCheckAt = 0L

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

        if (requestCode == REQUEST_CAMERA_PERMISSION) {
            // 相机权限被拒不是死路：拍照识别不需要这个权限，那条路仍然可用。
            // 所以这里不报错拦人，只说明一句然后退到退路上。
            if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                launchLiveScan()
            } else {
                showNotice(getString(R.string.scan_need_camera))
                startPhotoScan()
            }
            return
        }

        if (requestCode != REQUEST_PERMISSIONS) {
            return
        }

        // 录音权限是硬性的；通知权限被拒只会让通知不显示，不拦开始。
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            pendingStart = false
            showNotice(getString(R.string.error_no_record_permission))
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
        startActivityForResult(buildCaptureIntent(manager), REQUEST_PROJECTION)
    }

    /**
     * 按"共享来源"设置构造授权 Intent。
     *
     * 关键边界（务必理解）：MediaProjection 授权是 Android 的强制安全机制，
     * 弹窗由系统控制、每次都要用户点"允许"，第三方 App 无法记住或跳过。
     * 这里能做的只是 Android 14（API 34）起的"预填默认显示区域"：
     * 选"整个屏幕"时弹窗直接预填整个屏幕，省掉"选来源"这一步；
     * Android 11–13 没有这个 API，只能退回默认（系统自己决定弹窗形式）。
     */
    private fun buildCaptureIntent(manager: MediaProjectionManager): Intent {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            val config = if (shareWholeScreen()) {
                MediaProjectionConfig.createConfigForDefaultDisplay()
            } else {
                MediaProjectionConfig.createConfigForUserChoice()
            }
            return manager.createScreenCaptureIntent(config)
        }
        return manager.createScreenCaptureIntent()
    }

    /** 当前选的是否"整个屏幕"（否则是"单个应用"）。 */
    private fun shareWholeScreen(): Boolean =
        shareModeGroup.checkedRadioButtonId == shareWholeRadioId

    @Deprecated("没有 AndroidX 就得用这个老接口；Activity Result API 属于 AndroidX。")
    override fun onActivityResult(requestCode: Int, resultCode: Int, data: Intent?) {
        super.onActivityResult(requestCode, resultCode, data)

        if (requestCode == REQUEST_SCAN_LIVE) {
            onLiveScanResult(resultCode, data)
            return
        }

        if (requestCode == REQUEST_SCAN_PHOTO) {
            onPhotoScanResult(resultCode)
            return
        }

        if (requestCode != REQUEST_PROJECTION) {
            return
        }

        if (resultCode != RESULT_OK || data == null) {
            showNotice(getString(R.string.error_projection_denied))
            return
        }

        val port = portView.text.toString().trim().toIntOrNull()
        if (port == null || port !in 1..65_535) {
            showNotice(getString(R.string.error_bad_port))
            return
        }

        // 流名称在协议里是 16 字节 ASCII，中文会在编码时抛异常。
        // 在这里拦下来，比让它变成捕获线程里的一条错误更早也更好懂。
        val streamName = streamView.text.toString().trim().ifBlank { AudioCaptureService.DEFAULT_STREAM }
        validateStreamName(streamName)?.let { problem ->
            showNotice(problem)
            return
        }

        // 取成局部量：要放进下面的 lambda，而成员属性在 lambda 里拿不到智能转换。
        // 这两条 extra 的**内容**已经在 preparePsk 里定好了，这里只负责原样传递。
        val credentials = pendingCredentials
        if (credentials == null) {
            // 走到这里说明"要不要加密"这次根本没定过（典型是：系统权限弹窗期间
            // Activity 被重建，字段丢了）。**绝不能**在这里默认成明文 ——
            // 对一台"仅加密"的电脑，明文发出去就是整包被丢、双方零报错。
            // 宁可让用户再点一次。
            showNotice(getString(R.string.error_restart_send))
            return
        }
        pendingCredentials = null

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
            //
            // 读的是 [credentials]（[senderCredentialsFor] 的产物），不是
            // pendingPsk / selectedHubId 这些原始状态 —— 读原始状态等于把
            // "要不要加密"又判了第二遍，上一版正是这样与界面说的分了叉。
            val hubIdText = credentials.hubIdText
            val psk = credentials.psk
            if (hubIdText != null && psk != null) {
                putExtra(AudioCaptureService.EXTRA_HUB_ID, hubIdText)
                putExtra(AudioCaptureService.EXTRA_PSK, psk)
            }
        }

        // minSdk 是 30，直接用前台服务启动，避免服务退到后台被掐。
        startForegroundService(intent)
        toggleButton.text = getString(R.string.button_stop)
    }

    // ------------------------------------------------------------ 扫码 / 短码

    /**
     * 实时扫码：开着相机，认出来立刻填入。
     *
     * 权限只在这里问一次（进入扫码页时），不在启动应用时问 —— 一个只想填地址的用户
     * 不该被要求交出相机。
     */
    private fun startScan() {
        if (CaptureStatus.running) {
            Toast.makeText(this, getString(R.string.stop_before_connecting), Toast.LENGTH_SHORT).show()
            return
        }

        if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
            launchLiveScan()
            return
        }

        requestPermissions(arrayOf(Manifest.permission.CAMERA), REQUEST_CAMERA_PERMISSION)
    }

    private fun launchLiveScan() {
        startActivityForResult(ScanActivity.intent(this), REQUEST_SCAN_LIVE)
    }

    /**
     * 用系统相机拍一张电脑屏幕，再在本地解码。
     *
     * 现在是**退路**：只有在用户拒绝相机权限、或设备没有可用相机时才走这里。
     * 它不需要 CAMERA 权限（拍照由系统相机应用完成），所以这条路在任何情况下都还在。
     */
    private fun startPhotoScan() {
        if (CaptureStatus.running) {
            Toast.makeText(this, getString(R.string.stop_before_connecting), Toast.LENGTH_SHORT).show()
            return
        }

        val directory = CaptureProvider.scanDir(this)
        directory.mkdirs()
        val file = File(directory, "scan-${System.currentTimeMillis()}.jpg")
        val output = CaptureProvider.contentUriFor(file.name)

        val intent = Intent(MediaStore.ACTION_IMAGE_CAPTURE).apply {
            putExtra(MediaStore.EXTRA_OUTPUT, output)
            addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION or Intent.FLAG_GRANT_READ_URI_PERMISSION)
        }

        if (intent.resolveActivity(packageManager) == null) {
            showNotice(getString(R.string.camera_missing))
            return
        }

        pendingScanFile = file
        startActivityForResult(intent, REQUEST_SCAN_PHOTO)
    }

    private fun onPhotoScanResult(resultCode: Int) {
        val file = pendingScanFile
        pendingScanFile = null

        if (resultCode != RESULT_OK || file == null || !file.isFile) {
            file?.delete()
            return
        }

        // 解码几百毫秒到一秒，放后台线程，避免主线程卡住。
        Thread {
            val payload = runCatching { decodeQr(file) }.getOrNull()
            file.delete()

            handler.post {
                if (isFinishing || isDestroyed) {
                    return@post
                }

                if (payload == null) {
                    showNotice(getString(R.string.scan_failed))
                } else {
                    applyQrPayload(payload)
                }
            }
        }.apply { isDaemon = true }.start()
    }

    /**
     * 实时扫码页交回来的结果：只回传原始文本，解析仍只走 [ConnectionQr] 一处。
     *
     * 扫码页已经解析过一遍（用来判断"这不是 AudioHub 的码"）。这里再解析一次不是重复规则，
     * 而是同一条规则的第二次调用；如果这里解析不出来，说明两处对同一串的理解不一致，
     * 那种情况必须报错，不能静默当成功。
     */
    private fun onLiveScanResult(resultCode: Int, data: Intent?) {
        if (resultCode == ScanActivity.RESULT_CAMERA_FAILED) {
            // 相机打不开（被占用 / 没有可用相机）。这里必须真的接住：
            // 否则用户点了扫码、只看到一句提示，然后什么都不会发生 ——
            // 而 manifest 里写明"没有相机也能装"，那就必须留一条能走通的退路。
            showNotice(getString(R.string.scan_camera_failed))
            startPhotoScan()
            return
        }

        if (resultCode != RESULT_OK || data == null) {
            // 用户主动取消，或扫码页因没权限自行退出。都不需要再说什么。
            return
        }

        val text = data.getStringExtra(ScanActivity.EXTRA_QR_TEXT) ?: return
        val payload = ConnectionQr.parse(text)
        if (payload == null) {
            showNotice(getString(R.string.scan_not_audiohub))
            return
        }

        applyQrPayload(payload)
    }

    /** 解码一张照片。缩放上限 1600 px：再大只是浪费内存，对识别没有帮助。 */
    private fun decodeQr(file: File): QrPayload? {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(file.absolutePath, bounds)

        var sample = 1
        val longest = maxOf(bounds.outWidth, bounds.outHeight)
        while (longest / sample > 1600) {
            sample *= 2
        }

        val bitmap = BitmapFactory.decodeFile(
            file.absolutePath,
            BitmapFactory.Options().apply { inSampleSize = sample },
        ) ?: return null

        val width = bitmap.width
        val height = bitmap.height
        val pixels = IntArray(width * height)
        bitmap.getPixels(pixels, 0, width, 0, 0, width, height)
        bitmap.recycle()

        val binary = BinaryBitmap(HybridBinarizer(RGBLuminanceSource(width, height, pixels)))
        val reader = MultiFormatReader().apply {
            setHints(
                mapOf(
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
                    DecodeHintType.TRY_HARDER to true,
                ),
            )
        }

        return ConnectionQr.parse(reader.decode(binary).text)
    }

    /** 把扫到的内容填进界面。密钥按电脑身份存进配对表，和手输密钥走同一套 TOFU。 */
    private fun applyQrPayload(payload: QrPayload) {
        val hubId = payload.hubId?.let { runCatching { UUID.fromString(it) }.getOrNull() }

        if (payload.carriesKey && hubId == null) {
            // 有密钥却没有身份：存不进"按电脑身份记住"的表，硬发只会退成明文。
            showNotice(getString(R.string.qr_missing_identity))
            return
        }

        val psk = payload.pairingKey?.let { SecureEnvelope.parse(it) }
        if (payload.pairingKey != null && psk == null) {
            showNotice(getString(R.string.error_bad_key))
            return
        }

        hostView.setText(payload.host)
        portView.setText(payload.port.toString())
        // 名字先取成局部量：它来自另一个模块的属性，Kotlin 不给智能转换。
        val payloadName = payload.name
        if (hubId != null && !payloadName.isNullOrBlank()) {
            hubNames[hubId] = payloadName
        }
        selectedHubId = hubId
        // 这里以前是 `if (psk != null) REQUIRED else NONE`，等于**用"有没有密钥"覆盖电脑的原话**。
        // 后果：电脑设「仅加密」、而扫到的码不带密钥时，本机模式被写成 NONE，
        // 界面显示"明文发送"，用户点开始后每个包都被电脑拒掉，双方都不报错。
        //
        // 现在优先用二维码里带的 s= 字段 —— 那是电脑生成二维码时**自己说的**原话，
        // 比"本机记不记得这台电脑"可靠得多，也正是"电脑关掉加密后手机照旧加密"
        // 那个故障的正面修法。旧版二维码没这个字段，才退回按身份去查。
        selectedSecurityMode = payload.securityMode ?: advertisedModeFor(hubId)

        if (hubId != null && psk != null) {
            pairings.remember(hubId, psk, payload.name.orEmpty(), payload.host, payload.port)
            keyView.setText("")
            Toast.makeText(
                this,
                getString(R.string.qr_applied, payload.name ?: payload.host),
                Toast.LENGTH_SHORT,
            ).show()
        } else {
            showNotice(getString(R.string.qr_address_only))
        }

        updateKeyVisibility()
        renderHubList()
        savePrefs()
    }

    /** 六位短码：纯本地解出地址，不需要网络。 */
    private fun askForShortCode() {
        if (CaptureStatus.running) {
            Toast.makeText(this, getString(R.string.stop_before_connecting), Toast.LENGTH_SHORT).show()
            return
        }

        val input = EditText(this).apply {
            inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_FLAG_CAP_CHARACTERS
            hint = getString(R.string.short_code_hint)
            filters = arrayOf(InputFilter.LengthFilter(ShortCode.LENGTH + 4))
            setPadding(dp(12), dp(8), dp(12), dp(8))
        }

        AlertDialog.Builder(this)
            .setTitle(R.string.short_code_title)
            .setView(input)
            .setPositiveButton(R.string.short_code_ok) { _, _ -> applyShortCode(input.text.toString()) }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun applyShortCode(raw: String) {
        val address = ShortCode.decode(raw)
        if (address == null) {
            showNotice(getString(R.string.short_code_failed))
            return
        }

        hostView.setText(address)
        portView.setText(ShortCode.DEFAULT_PORT.toString())

        // 短码里只有地址。先去问一次"你是哪台电脑、要不要加密"——
        // 这正是第 9 条那个 bug 的正面修法：电脑设了加密时，手机必须拿到它的身份
        // 才能派生密钥；拿不到就只能明文发，而电脑把明文全拒掉，双方都不报错。
        //
        // 以前这里只靠"按地址反查已配对记录"猜身份，猜不到就退成明文；
        // 现在先问电脑本人，问不到才退回那条老路（手机上一点记录都没有时它仍有用）。
        identifyAddress(address) { hub ->
            if (hub != null) {
                val name = candidates.firstOrNull { it.hubId == hub.hubId }?.name
                    ?.takeIf { it.isNotBlank() } ?: hub.name.ifBlank { address }
                showNotice(
                    getString(R.string.short_code_identified, name, describeSecurity(hub.securityMode)),
                )
                return@identifyAddress
            }

            // 电脑没应答：可能没开、也可能不在同一个 Wi-Fi。此时身份确实拿不到，
            // 只能退回"按地址反查已配对的电脑"，至少让配过对的人还能加密。
            val pairing = pairings.all().firstOrNull { it.host == address }
            if (pairing != null) {
                // 同样是"先清再改"：手填/短码换到另一台电脑时，框里可能还留着上一台的密钥。
                clearTypedKeyIfTargetChanged(pairing.hubId)
                selectedHubId = pairing.hubId
                selectedSecurityMode = advertisedModeFor(pairing.hubId)
                Toast.makeText(this, getString(R.string.short_code_paired, address), Toast.LENGTH_SHORT).show()
            } else {
                selectedHubId = null
                // 这里**不要**再写 selectedSecurityMode = NONE。那个字段的语义是
                // "电脑自己说的话"，而短码只给了个地址，关于加密它一个字都没说。
                // 硬塞一个 NONE 进去，等于替电脑宣布"我关了加密"，将来任何一处
                // 直接读它都会得出错误结论。没有身份时判定统一走
                // advertisedModeOrNull() 的"不知道"，由 resolveSendMode 按未听到广播处理。
                showNotice(getString(R.string.short_code_unreachable, address))
            }

            updateKeyVisibility()
            savePrefs()
        }

        updateKeyVisibility()
        savePrefs()
    }

    // ------------------------------------------------------------ 更新

    private fun onCheckUpdateClicked() {
        updateButton.isEnabled = false
        updater.checkForUpdates(object : Updater.Listener {
            override fun onMessage(msg: String) {
                Toast.makeText(this@MainActivity, msg, Toast.LENGTH_SHORT).show()
            }

            override fun onProgress(percent: Int) {
                if (percent in 0..100) {
                    updateButton.text = getString(R.string.update_progress, percent)
                }
            }

            override fun onDone() {
                updateButton.isEnabled = true
                updateButton.text = getString(R.string.button_check_update)
            }
        })
    }

    private fun renderStatus() {
        statusView.text = CaptureStatus.describe()

        val running = CaptureStatus.running

        // 运行中用真实结果覆盖"将要如何"，不用让用户去状态大段里找。
        if (::linkPlanView.isInitialized) {
            if (running) {
                val encrypted = CaptureStatus.encrypted
                linkPlanView.text = if (encrypted) "链路：加密发送中（AES-256-GCM）" else "链路：明文发送中（未加密）"
                linkPlanView.setTextColor(if (encrypted) SUCCESS else DANGER)
            } else {
                updateLinkPlan()
            }
        }
        toggleButton.text = getString(if (running) R.string.button_stop else R.string.button_start)

        // 运行中锁住目标，避免界面显示的地址和实际在用的一致不了。
        hostView.isEnabled = !running
        portView.isEnabled = !running
        streamView.isEnabled = !running
        keyView.isEnabled = !running
        findButton.isEnabled = !running && !searching
        scanButton.isEnabled = !running
        codeButton.isEnabled = !running

        // 运行中顺手复查一次电脑的加密设置（每 5 秒最多一次）。
        if (running) {
            maybeRecheckMode()
        }
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
            .putString(KEY_SHARE_MODE, if (shareModeGroup.checkedRadioButtonId == shareWholeRadioId) SHARE_WHOLE else SHARE_APP)
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
        private const val KEY_SHARE_MODE = "shareMode"
        private const val SHARE_WHOLE = "whole"
        private const val SHARE_APP = "app"

        private const val REQUEST_PERMISSIONS = 1001
        private const val REQUEST_PROJECTION = 1002
        private const val REQUEST_SCAN_PHOTO = 1003
        private const val REQUEST_SCAN_LIVE = 1004
        private const val REQUEST_CAMERA_PERMISSION = 1005
        private const val REFRESH_INTERVAL_MILLIS = 500L

        /** 运行中复查电脑加密设置的间隔。5 秒足够快，又不至于变成网络噪音。 */
        private const val MODE_RECHECK_MILLIS = 5_000L
    }
}
