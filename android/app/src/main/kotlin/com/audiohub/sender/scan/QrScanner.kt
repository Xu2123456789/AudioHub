package com.audiohub.sender.scan

import com.audiohub.protocol.QrPayload

/**
 * 一帧画面的亮度数据，紧排存放（每行恰好 [width] 字节，行间没有填充）。
 *
 * 为什么契约里是"灰度 + 宽高"而不是 `ImageProxy` / `Bitmap`：
 * 二维码识别只需要亮度，而每个平台的相机 API 都能直接给出亮度平面
 * （Android 的 YUV Y 平面、iOS 的 pixel buffer、桌面的摄像头灰度帧）。
 * 用灰度做输入，这个契约就不绑定任何平台 —— 将来换平台只需重写"把相机帧变成
 * LuminanceFrame"那十几行，识别逻辑和二维码内容的解析都不用动。
 *
 * 代价是要把 Y 平面复制成紧排：相机的行跨度（rowStride）通常大于宽度。
 * 复制一次 640×480 约 0.3 MB，帧率十几帧，可以忽略。
 */
class LuminanceFrame(
    val luma: ByteArray,
    val width: Int,
    val height: Int,
) {
    init {
        require(width > 0 && height > 0) { "画面尺寸不合法：${width}x$height" }
        require(luma.size == width * height) {
            "亮度数据 $luma.size 字节，与 ${width}x$height 不符"
        }
    }
}

/** 一次识别的结果。三种情况必须分开，因为对用户说的话完全不同。 */
sealed interface ScanOutcome {
    /**
     * 认出来了，而且确实是 AudioHub 的二维码。
     *
     * 同时带上 [text] 和 [payload]：跨界面传递的是原始文本（[com.audiohub.protocol.QrPayload]
     * 不是 Serializable，也不该为了传递方便去实现它——那是 JVM 的细节，协议层不该知道），
     * 而 [payload] 证明这一帧确实能被解析，不必在调用方再判一次"是不是我们的码"。
     */
    class Found(val text: String, val payload: QrPayload) : ScanOutcome

    /** 画面里没有能识别的二维码。继续扫就行，不需要提示。 */
    object NoCode : ScanOutcome

    /**
     * 识别到了二维码，但内容不是 AudioHub 的格式。
     *
     * 单独一种情况是因为它**不能**当成"还没对准"：用户可能一直举着一个别的二维码，
     * 越扫越莫名其妙。要明确告诉他"这不是 AudioHub 的码"。
     */
    object NotAudioHub : ScanOutcome
}

/**
 * 扫描器契约：给一帧亮度数据，还一个结果。
 *
 * 实现必须是无状态的、可以在任意线程被调用；不许持有相机、界面或 Activity。
 * 这条约束是为了让识别逻辑能被普通 JVM 单测直接覆盖 —— 不需要手机、不需要相机。
 */
interface QrScanner {
    fun scan(frame: LuminanceFrame): ScanOutcome
}
