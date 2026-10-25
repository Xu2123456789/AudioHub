# ADR 0005: 安卓音源走 VBAN 协议

- 状态：已采纳
- 日期：2026-10-05
- 影响层面：`AudioHub.Sources.Vban`、`AudioHub.Hosting`（多一路 `TransportKind`）、`AudioHub.Core`（新增 `TransportAudioSource`）

## 背景

M2 要把安卓手机的音频接进 Windows 中枢。CW（2026-10-03 调研）已排除「多源 → 一副蓝牙耳机」的成品硬件，
所以这条路只能自己走。剩下的选择是：**用哪个现成轮子**、**要不要自己实现传输**。

候选沟通过网络核实，结论如下：

| 候选 | 许可 | Windows 服务端 | 结论 |
| --- | --- | --- | --- |
| scrcpy | Apache-2.0，活跃维护 | 官方提供 | 只能外部进程。**音频转发要求 Android 11+**，且须另装虚拟声卡（驱动级安装）才能进 AudioHub |
| sndcpy | MIT | 无（依赖 VLC） | 排除：作者自承为 PoC，抖动导致 VLC 缓冲涨到数百毫秒 |
| roc-toolkit | MPL-2.0 | **官方不支持 Windows** | 本轮不可选；将来 Core 落到 Linux 时是最优解 |
| AudioRelay | **闭源**，ToS 禁止逆向 | 是 | 只能外部进程依赖，免费版限制多，无 SDK |
| **VBAN 协议** | 规范公开可自行实现 | 自己写接收端 | 选中 |

## 决定

**自己实现 VBAN 接收端，作为一个独立的 net8.0 模块；手机端先用第三方 VBAN 发送 App，
将来若要自研 Kotlin 客户端，走同一协议，服务端代码不动。**

理由：

1. **真·零安装**。不用装虚拟声卡驱动、不用 root、不依赖任何第三方外部进程。
   （本机安装任何东西之前必须先问用户目录，这条对你的既有约定也适用 —— 选它就绕开了。）
2. **协议本身极简且合适**。UDP + PCM + 帧计数，无时钟同步、无会话、无握手。
   「每个发送者是主、接收者跟随」这个设定，正好配我们「包到达后由输出时钟统一拉取」的拉取式管线。
3. **手机端可以歇着**。已经存在的第三方 VBAN 发送 App 即可跑通，本轮不写安卓代码。
4. **模块化**：传输（`ITransport`）、包装（`TransportAudioSource`）、协议（`AudioHub.Sources.Vban`）三层分离，
   换协议只动一行配置和一个 switch 分支。

## 授权核实（这一步很重要，动手前必做）

官方规范 `VBANProtocol_Specifications.pdf`（revision 13）原文：

> "VBAN Protocol is public and free, it can be implemented by any developers or companies
> to make receiver or transmitter applications."
>
> "Licensing: VBAN Protocol base, described in this document, is free to use AS IS. Under
> this free license, the VBAN protocol base includes: The AUDIO protocol with PCM Audio formats."

我们只用到 PCM audio 这一块，**授权成立**。

注意区分：上面那句是 VBAN 协议本身；Voicemeeter Remote API SDK 商用需付费（另行 500 欧），
我们不使用它，也不依赖 Voicemeeter 软件本身。

## 已知取舍

- **12 bit / 10 bit 数据类型被拒绝**。规范列出了这两种，但从未给出它引用的
  `VBAN_SampleSize` 数组内容，世面上也无法从别处确认其字节布局。猜错了要么越界读、
  要么整个流错位，所以宁可拒绝并计数（详见 `VbanPacketHeader.TryGetPayloadSize`）。
- **采样率必须配对**。VBAN 无协商机制，混音器这一路是开跑前就用固定格式建好的。
  如果手机发的采样率与我们注册的不一致，不会被重采样 —— 会放错速度。
  所以 `VbanTransport` 支持输入期望格式，不匹配就拒绝，理由写进 `LastRejectionReason`。
- **UDP 无断连事件**。只能靠「超时没包」判断掉线，当前用 2 秒。

## 如果将来换成自研 Kotlin 客户端

不改动任何上层。新增一个传输实现（例如 `AudioHub.Sources.RtpOpus`），
在 `WindowsAudioHubHost.CreateTransport` 加一个分支，`TransportKind` 加一个枚举值即可。
`Concentus`（NuGet，纯 C# Opus 解码，支持 net8.0）是那条路上的首选依赖。
