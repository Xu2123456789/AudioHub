package com.audiohub.sender.scan

import java.nio.ByteBuffer

/**
 * 从相机的亮度平面切出紧排的 [LuminanceFrame]。
 *
 * 单独抽成纯函数（只依赖 java.nio，不碰任何 Android 类型）的原因是这里最容易错：
 *
 * - **行跨度**（rowStride）通常大于宽度——每行尾部有对齐填充。按"宽度×高度"直接一次读，
 *   得到的图像会斜着撕开。
 * - **像素跨度**（pixelStride）在 YUV_420_888 的 Y 平面上恒为 1，但有些设备/格式下不是。
 * - 平面缓冲区最后一行可能不满一行跨度，越界读会抛异常。
 *
 * 这三种错法在预览画面上**完全看不出来**（预览走的是另一条通道），
 * 表现只是"怎么扫都认不出来"。所以这段必须能被单测，而不是等手机上来试。
 *
 * @return 切好的帧；尺寸不合法或数据不够时返回 null（当作这一帧没拿到，继续下一帧）。
 */
fun luminanceFrameFromPlane(
    buffer: ByteBuffer,
    width: Int,
    height: Int,
    rowStride: Int,
    pixelStride: Int,
): LuminanceFrame? {
    if (width <= 0 || height <= 0 || rowStride < width || pixelStride < 1) {
        return null
    }

    val data = ByteArray(width * height)
    val limit = buffer.limit()

    try {
        if (pixelStride == 1 && rowStride == width) {
            // 紧排，最常见的情况：一次拷完。
            if (limit < data.size) {
                return null
            }
            buffer.position(0)
            buffer.get(data, 0, data.size)
            return LuminanceFrame(data, width, height)
        }

        for (y in 0 until height) {
            val rowStart = y * rowStride
            val target = y * width
            for (x in 0 until width) {
                val index = rowStart + x * pixelStride
                if (index >= limit) {
                    // 数据没给全：宁可丢掉这一帧，也不要拿半张图去识别。
                    return null
                }
                data[target + x] = buffer.get(index)
            }
        }
    } catch (e: Exception) {
        // 缓冲区状态异常（越界、只读等）一律按"这帧没拿到"处理，不能冒到相机线程上。
        return null
    }

    return LuminanceFrame(data, width, height)
}
