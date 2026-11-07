package com.audiohub.sender

import android.Manifest
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Color
import android.os.Bundle
import android.util.Size
import android.view.Gravity
import android.widget.Button
import android.widget.FrameLayout
import android.widget.TextView
import androidx.activity.ComponentActivity
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.ImageProxy
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import com.audiohub.sender.scan.LuminanceFrame
import com.audiohub.sender.scan.ScanOutcome
import com.audiohub.sender.scan.ZxingQrScanner
import com.audiohub.sender.scan.luminanceFrameFromPlane
import java.util.concurrent.ExecutorService
import java.util.concurrent.Executors

/**
 * 实时扫码页：开着相机，认出来就立刻关掉并回传。
 *
 * 这一层是**平台适配层**，故意写得很薄：只做"开相机、把帧变成 LuminanceFrame、
 * 把结果回传"。识别规则在 [ZxingQrScanner]，二维码内容格式在 :protocol 的 ConnectionQr，
 * 两者都与平台无关。换平台时只需要重写这个文件（拿别的相机 API 填同一个契约）。
 *
 * 为什么用 CameraX 而不是手写 Camera2：旋转、对焦、预览与各家 ROM 的差异都在
 * Camera2 的样板里，两百多行且和协议毫无关系。按"每个平台都要照着重写一遍"来算，
 * 让这一层代码最少才是对的选择——不是因为它在 Android 上最强。
 */
class ScanActivity : ComponentActivity() {

    companion object {
        /** 识别成功时回传的二维码原始文本，由调用方用 ConnectionQr.parse 解析。 */
        const val EXTRA_QR_TEXT = "com.audiohub.sender.extra.QR_TEXT"

        /**
         * 相机打不开。
         *
         * 必须有一个**能被区分出来的**结果码，而不是只顾着在原地显示一句话。
         * 早先那版打不开时只改提示文字、不 finish、不回传任何结果：用户看到一句
         * "相机打不开"，然后这扇窗口就停在那里，退出后才回到主界面，
         * 而 manifest 承诺的"没有相机也能用拍照扫码"永远不会发生。
         */
        const val RESULT_CAMERA_FAILED = 2

        fun intent(context: Context): Intent = Intent(context, ScanActivity::class.java)
    }

    private lateinit var previewView: PreviewView
    private lateinit var hintView: TextView
    private lateinit var cancelButton: Button

    private val scanner = ZxingQrScanner()
    private var analysisExecutor: ExecutorService? = null

    /** 已经认出来了（或已经结束），后续帧不再处理，避免回传两次。 */
    @Volatile
    private var done = false

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        // 权限由调用方（MainActivity）先申请好；这里只做兜底，
        // 因为"没权限还硬开相机"会抛 SecurityException，表现是闪退。
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            setResult(RESULT_CANCELED)
            finish()
            return
        }

        buildUi()
        startCamera()
    }

    // ---------------------------------------------------------------- 界面

    private fun buildUi() {
        // 界面手搭而不写 XML：只有一个取景框、一句提示、一个取消按钮，
        // 单独为它加一份 layout 反而更难读。
        val root = FrameLayout(this)

        previewView = PreviewView(this).apply {
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.MATCH_PARENT,
            )
            // 二维码取景用 FILL_CENTER：画面被裁一点没关系，识别靠的是中间那块。
            scaleType = PreviewView.ScaleType.FILL_CENTER
        }
        root.addView(previewView)

        hintView = TextView(this).apply {
            text = getString(R.string.scan_live_hint)
            setTextColor(Color.WHITE)
            textSize = 15f
            setBackgroundColor(0xAA000000.toInt())
            gravity = Gravity.CENTER
            setPadding(dp(16), dp(10), dp(16), dp(10))
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.MATCH_PARENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
            ).apply { gravity = Gravity.TOP }
        }
        root.addView(hintView)

        cancelButton = Button(this).apply {
            text = getString(R.string.scan_live_cancel)
            setOnClickListener {
                setResult(RESULT_CANCELED)
                finish()
            }
            layoutParams = FrameLayout.LayoutParams(
                FrameLayout.LayoutParams.WRAP_CONTENT,
                FrameLayout.LayoutParams.WRAP_CONTENT,
            ).apply {
                gravity = Gravity.BOTTOM or Gravity.CENTER_HORIZONTAL
                bottomMargin = dp(32)
            }
        }
        root.addView(cancelButton)

        setContentView(root)
    }

    private fun dp(value: Int): Int =
        (value * resources.displayMetrics.density).toInt()

    // ---------------------------------------------------------------- 相机

    private fun startCamera() {
        val future = ProcessCameraProvider.getInstance(this)

        future.addListener({
            // 相机可能在监听器执行前就被别的应用抢走，或这台设备根本没有可用相机。
            val provider = runCatching { future.get() }.getOrElse {
                failCamera()
                return@addListener
            }

            val preview = Preview.Builder().build().apply {
                setSurfaceProvider(previewView.surfaceProvider)
            }

            val executor = Executors.newSingleThreadExecutor()
            analysisExecutor = executor

            val analysis = ImageAnalysis.Builder()
                // 只保留最新一帧：识别跟不上时宁可丢帧，也不要排队积压，
                // 否则认出来的会是几百毫秒前的画面（用户已经移开了）。
                .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                .apply {
                    @Suppress("DEPRECATION")
                    setTargetResolution(Size(1280, 720))
                }
                .build()
                .apply { setAnalyzer(executor, ::analyze) }

            if (!bindFirstAvailable(provider, preview, analysis)) {
                // 常见原因：相机被占用、或这台设备根本没有相机。
                // 不能只改提示文字就停在这里 —— 调用方要能接住，退回拍照扫码。
                failCamera()
            }
        }, mainExecutor)
    }

    /**
     * 绑定第一台能用的相机，后置优先。
     *
     * 后置优先是因为要对着电脑屏幕扫，画质与对焦都更合适；但不少平板只有前置，
     * 所以退一步也要试。manifest 里声明了「没有相机也能装」，那么有前置就该能用。
     */
    private fun bindFirstAvailable(
        provider: ProcessCameraProvider,
        preview: Preview,
        analysis: ImageAnalysis,
    ): Boolean {
        val selectors = listOf(
            CameraSelector.DEFAULT_BACK_CAMERA,
            CameraSelector.DEFAULT_FRONT_CAMERA,
        )

        for (selector in selectors) {
            val exists = runCatching { provider.hasCamera(selector) }.getOrDefault(false)
            if (!exists) {
                continue
            }

            val bound = runCatching {
                provider.unbindAll()
                provider.bindToLifecycle(this, selector, preview, analysis)
            }.isSuccess

            if (bound) {
                return true
            }
        }

        return false
    }

    private fun analyze(proxy: ImageProxy) {
        // close() 必须走到：不关的话相机会在若干帧之后彻底停止出图，
        // 表现是"预览还在动，但怎么扫都认不出来"。
        try {
            if (done) {
                return
            }

            val frame = proxy.toLuminanceFrame() ?: return

            when (val outcome = scanner.scan(frame)) {
                is ScanOutcome.Found -> deliver(outcome.text)
                ScanOutcome.NotAudioHub -> showHint(getString(R.string.scan_not_audiohub))
                ScanOutcome.NoCode -> Unit
            }
        } finally {
            proxy.close()
        }
    }

    /**
     * 把相机帧变成契约里的灰度帧。
     *
     * 只取 Y 平面（亮度），不取 UV：二维码识别不需要颜色，少复制一半数据。
     * 解析分辨率已由 [ImageAnalysis.setTargetResolution] 限定为 1280×720。
     *
     * 真正容易出错的"行跨度/像素跨度"处理在 [luminanceFrameFromPlane] 里，
     * 那是纯函数，可以在电脑上单测——预览画面看不出这类错误，表现只是"怎么扫都认不出来"。
     */
    private fun ImageProxy.toLuminanceFrame(): LuminanceFrame? {
        if (width <= 0 || height <= 0) {
            return null
        }

        val plane = planes.firstOrNull() ?: return null
        return luminanceFrameFromPlane(
            buffer = plane.buffer,
            width = width,
            height = height,
            rowStride = plane.rowStride,
            pixelStride = plane.pixelStride,
        )
    }

    // ---------------------------------------------------------------- 结果

    private fun deliver(qrText: String) {
        if (done) {
            return
        }
        done = true

        runOnUiThread {
            setResult(RESULT_OK, Intent().putExtra(EXTRA_QR_TEXT, qrText))
            finish()
        }
    }

    /**
     * 相机救不回来：把结果码交出去，让调用方退回拍照扫码。
     *
     * 这里不自己弹提示，因为提示文案和"接下来做什么"都属于调用方 ——
     * 这一层只负责如实报告"没能开相机"。
     */
    private fun failCamera() {
        if (done) {
            return
        }
        done = true

        runOnUiThread {
            setResult(RESULT_CAMERA_FAILED)
            finish()
        }
    }

    private fun showHint(text: String) {
        runOnUiThread {
            if (!done) {
                hintView.text = text
            }
        }
    }

    override fun onDestroy() {
        super.onDestroy()
        analysisExecutor?.shutdown()
        analysisExecutor = null
    }
}
