package com.audiohub.sender.scan

import com.google.zxing.BarcodeFormat
import com.google.zxing.qrcode.QRCodeWriter
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.nio.ByteBuffer

/**
 * 钉住"识别出来的东西对不对"，不需要手机、不需要相机。
 *
 * 做法：用 ZXing 的**编码器**把一段文本画成二维码，再转成契约里的灰度帧，
 * 交给 [ZxingQrScanner] 认回来。编码器与解码器在同一个库里，所以这组测试证明的
 * 不是"ZXing 自己跟自己对答案"，而是：
 *
 * 1. 我们喂给它的字节布局是对的（行跨度、灰度极性、画面尺寸）；
 * 2. 认出来之后交给 [com.audiohub.protocol.ConnectionQr] 的那一步接得上；
 * 3. "不是 AudioHub 的码"能被区分出来，而不是被当成"还没对准"。
 *
 * 第 1 条是真正容易出错的地方：灰度极性反了、行跨度过早收紧、尺寸传错，
 * 相机预览里看起来一切正常，就是永远认不出来——那种故障只能靠这组测试提前发现。
 */
class ZxingQrScannerTest {

    private val scanner = ZxingQrScanner()

    @Test
    fun aRealAudioHubQrDecodesIntoTheSameFields() {
        val original =
            "audiohub://c/1?h=10.20.30.40&p=6980&k=ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2" +
                "&i=11111111-2222-3333-4444-555555555555&f=b321-0f13-afca-6cd2&n=DESKTOP-1"

        val outcome = scanner.scan(renderQr(original))

        assertTrue("应认出是 AudioHub 的码，实际 $outcome", outcome is ScanOutcome.Found)
        val found = outcome as ScanOutcome.Found

        assertEquals("10.20.30.40", found.payload.host)
        assertEquals(6980, found.payload.port)
        assertEquals("ABCDE-FGHIJ-KLMNP-QRSTV-WXYZ2", found.payload.pairingKey)
        assertEquals("11111111-2222-3333-4444-555555555555", found.payload.hubId)
        assertEquals("b321-0f13-afca-6cd2", found.payload.fingerprint)
        assertEquals("DESKTOP-1", found.payload.name)
        assertTrue("带密钥的码扫完就具备加密条件", found.payload.carriesKey)

        // 回传的是原始文本：跨界面传递不依赖 QrPayload 可序列化。
        assertEquals(original, found.text)
    }

    @Test
    fun anAddressOnlyQrStillDecodes() {
        // 电脑端"二维码不含密钥"那个开关的产物：能连，但不能加密。
        val found = scanner.scan(renderQr("audiohub://c/1?h=192.168.1.5&p=6980&n=PC"))

        assertTrue(found is ScanOutcome.Found)
        val payload = (found as ScanOutcome.Found).payload
        assertEquals("192.168.1.5", payload.host)
        assertEquals("PC", payload.name)
        assertEquals(null, payload.pairingKey)
        assertTrue("没有密钥就不该声称具备加密条件", !payload.carriesKey)
    }

    @Test
    fun someOtherQrIsNotSilentlyTreatedAsNotAlignedYet() {
        // 用户可能举着一张付款码或网址码。如果把它当成"还没对准"，用户会一直举着。
        val outcome = scanner.scan(renderQr("https://example.com/some-other-qr"))

        assertEquals(ScanOutcome.NotAudioHub, outcome)
    }

    @Test
    fun anEmptyFrameIsNoCodeNotAnError() {
        // 相机刚打开、或者对着白墙：这是最常见的状态，必须安静地返回"没认出来"，
        // 不能抛异常——异常会冒到相机线程上，把整个分析循环打断。
        val blank = LuminanceFrame(
            ByteArray(640 * 480) { 0xFF.toByte() },
            width = 640,
            height = 480,
        )

        assertEquals(ScanOutcome.NoCode, scanner.scan(blank))
    }

    @Test
    fun aFrameWithPaddingBetweenRowsStillDecodes() {
        // 相机给的 Y 平面通常每行尾部带对齐填充（rowStride > width）。
        // 按"宽度×高度"直接一次读，图像会斜着撕开——预览画面完全看不出来，
        // 表现只是"怎么扫都认不出来"。所以这条测试直接打在行跨度的处理上。
        val text = "audiohub://c/1?h=10.0.0.9&p=6980&n=PADDED"
        val tight = renderQr(text)

        for (extra in intArrayOf(1, 16, 64)) {
            val padded = padRows(tight, extra)

            val frame = luminanceFrameFromPlane(
                buffer = ByteBuffer.wrap(padded),
                width = tight.width,
                height = tight.height,
                rowStride = tight.width + extra,
                pixelStride = 1,
            )

            assertNotNull("extra=$extra 时应能切出帧", frame)
            assertEquals("extra=$extra 切出的尺寸不对", tight.width, frame!!.width)
            assertEquals("extra=$extra 切出的尺寸不对", tight.height, frame.height)

            val outcome = scanner.scan(frame)
            assertTrue("extra=$extra 扣掉填充后应能认出，实际 $outcome", outcome is ScanOutcome.Found)
            assertEquals(
                "10.0.0.9",
                (outcome as ScanOutcome.Found).payload.host,
            )
        }
    }

    @Test
    fun aTruncatedPlaneIsRejectedInsteadOfDecodingHalfAnImage() {
        // 数据没给全时宁可丢掉这一帧：拿半张图去识别只会得到随机结果。
        val frame = luminanceFrameFromPlane(
            buffer = ByteBuffer.wrap(ByteArray(100)),
            width = 100,
            height = 10,
            rowStride = 100,
            pixelStride = 1,
        )

        assertEquals(null, frame)
    }

    // ------------------------------------------------------------ 辅助

    /** 把文本画成二维码，再转成灰度帧（二维码的黑白极性：模块为 0x00，空白为 0xFF）。 */
    private fun renderQr(text: String, size: Int = 480): LuminanceFrame {
        val matrix = QRCodeWriter().encode(text, BarcodeFormat.QR_CODE, size, size)
        val width = matrix.width
        val height = matrix.height
        val luma = ByteArray(width * height)

        for (y in 0 until height) {
            for (x in 0 until width) {
                luma[y * width + x] = if (matrix.get(x, y)) 0x00 else 0xFF.toByte()
            }
        }

        return LuminanceFrame(luma, width, height)
    }

    private fun padRows(frame: LuminanceFrame, extraBytesPerRow: Int): ByteArray {
        val stride = frame.width + extraBytesPerRow
        // 填充字节故意用 0x7F（灰）：如果适配层把它当数据读进去，画面会出现竖条纹，
        // 识别必然失败——那样这条测试才有意义。
        val padded = ByteArray(stride * frame.height) { 0x7F }
        for (y in 0 until frame.height) {
            frame.luma.copyInto(
                padded,
                destinationOffset = y * stride,
                startIndex = y * frame.width,
                endIndex = (y + 1) * frame.width,
            )
        }
        return padded
    }
}
