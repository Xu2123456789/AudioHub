package com.audiohub.sender.scan

import com.audiohub.protocol.ConnectionQr
import com.google.zxing.BarcodeFormat
import com.google.zxing.BinaryBitmap
import com.google.zxing.DecodeHintType
import com.google.zxing.MultiFormatReader
import com.google.zxing.PlanarYUVLuminanceSource
import com.google.zxing.common.HybridBinarizer
import com.google.zxing.NotFoundException

/**
 * 用 ZXing 的纯 Java 核心识别二维码。
 *
 * 为什么不用 zxing-android-embedded：它自带取景界面，样式改不动，而且把"取景"和"识别"
 * 绑在一起。我们只要"给一帧亮度、还一段文本"，自己接反而更薄，也才能让识别逻辑
 * 脱离相机被单测覆盖。
 *
 * 为什么不用 ML Kit：它的模型默认由 Google Play 服务提供，国内行货手机和华为设备上
 * 常常直接不可用；打包版又会把体积和闭源依赖带进来。而我们要扫的是**自己电脑屏幕上
 * 生成的二维码**——高对比、正对、近距离，属于识别难度的下限，用不上它为商品条码
 * 优化的那套能力。
 */
class ZxingQrScanner : QrScanner {
    override fun scan(frame: LuminanceFrame): ScanOutcome {
        val source = PlanarYUVLuminanceSource(
            frame.luma,
            frame.width,
            frame.height,
            0,
            0,
            frame.width,
            frame.height,
            false,
        )

        // 每帧新建 reader：MultiFormatReader 的 decodeWithState 会保留状态，跨线程共享不安全，
        // 而识别一帧的开销里构造对象的占比可以忽略。
        val reader = MultiFormatReader().apply {
            setHints(
                mapOf(
                    DecodeHintType.POSSIBLE_FORMATS to listOf(BarcodeFormat.QR_CODE),
                    DecodeHintType.TRY_HARDER to true,
                ),
            )
        }

        val text = try {
            reader.decode(BinaryBitmap(HybridBinarizer(source))).text
        } catch (e: NotFoundException) {
            // 最常见的情况：画面里还没有完整的二维码。不是错误，继续下一帧。
            return ScanOutcome.NoCode
        } catch (e: Exception) {
            // 畸形图像等各种解码异常一律当作"这一帧没认出来"。
            // 不能让它冒到相机线程上——那里抛出去会让整个分析循环停掉。
            return ScanOutcome.NoCode
        }

        val payload = ConnectionQr.parse(text) ?: return ScanOutcome.NotAudioHub
        return ScanOutcome.Found(text, payload)
    }
}
