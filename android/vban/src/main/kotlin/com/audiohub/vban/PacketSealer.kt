package com.audiohub.vban

/**
 * 把**已经写好的整个 VBAN 报文**转换成实际要发出去的字节。
 *
 * 为什么要这么设计，而不是让 [VbanUdpSender] 直接依赖加密模块：
 * - `:vban` 是一个纯协议模块，不该知道密钥从哪来。加密属于"另一个 concerns"，
 *   把它硬塞进来会让 `:vban` 依赖 `:protocol`，而后者还带着发现协议，
 *   于是"只想测报文编码"的人被迫把整套安全层也拖进来。
 * - 接缝开在"整个报文写完之后"而不是"载荷写完之后"，是因为要保护的不只是音频内容。
 *   VBAN 头里有采样率、声道数、流名称，泄露它们等于告诉别人你在听什么、什么格式；
 *   连头一起封装，外面的人只能看出"这里有个 AudioHub 的加密包"。
 *
 * 实现必须能就地写入 [destination] 并返回长度：发送路径每秒走两百次，
 * 每次都新建数组会持续制造垃圾。
 */
fun interface PacketSealer {
    /**
     * @param source 已经写好的完整 VBAN 报文，有效内容是前 [size] 字节
     * @param size 报文长度
     * @param destination 输出缓冲，容量至少 `size + size 增长量`
     * @return 实际写入的字节数
     */
    fun seal(source: ByteArray, size: Int, destination: ByteArray): Int
}
