# AudioHub 手机端（安卓）

把手机正在播放的声音，通过 Wi-Fi 以 VBAN 协议发给电脑上的 AudioHub。
电脑端把它混进去，最终从电脑的蓝牙耳机出声——这样"手机 + 蓝牙耳机"那段
蓝牙跳数就被 Wi-Fi 取代了，是降延迟的全部意义所在。

```
手机播放的声音
   │  AudioPlaybackCapture（系统级回放捕获，Android 10+）
   ▼
AudioRecord（48000 Hz / 立体声 / 16 位）
   │
   ▼
VbanPacketWriter（打成 28 字节头 + PCM 的 VBAN 报文）
   │
   ▼
UDP → 电脑 6980 端口
   │
   ▼
AudioHub.VbanTransport → 混音器 → 电脑的蓝牙耳机
```

## 目录里有什么

| 路径 | 作用 |
|---|---|
| `vban/` | 纯 Kotlin/JVM 模块：VBAN 报文编码 + UDP 发送。**不依赖 Android**，所以能在电脑上用普通单元测试跑 |
| `app/` | 安卓应用：回放捕获、UDP 发送、界面 |
| `simulator/` | 在电脑上冒充手机发包的开发工具，用来在没有手机时验证电脑的接收路径 |
| `build-apk.bat` | 双击就能构建，产物路径会打印出来 |
| `simulate-sender.bat` | 双击就开始冒充手机发包 |
| `toolchain-env.bat` | 上面两个入口共用的工具链路径设置，不单独运行 |
| `local.properties` | 指向 Android SDK 的位置（本机专用） |

`vban/` 和 `app/` 分开不是洁癖，它带来两件实在的事：

1. 报文编码抽出来之后，"发出去的包对不对"变成了一条能在电脑上跑的单元测试，
   不需要手机、不需要模拟器。
2. 连 UDP 发送也放在 `vban/` 里（`VbanUdpSender` 一个 Android API 都没用），
   于是 `simulator/` 能复用**和手机完全相同**的那份代码。这让"模拟测试"真正有意义——
   它验证的是手机将来要走的代码路径，不是另写一份的近似品。

## 工具链位置

全部装在 `D:\app\Android\` 下面，不污染系统 PATH，也不写 `C:\Users\<用户名>\.gradle`：

```
D:\app\Android\
├── JDK-17\          Temurin 17.0.20.1
├── Android-SDK\     cmdline-tools / platform-tools / platforms;android-34 / build-tools;34.0.0
├── Gradle-8.7\      Gradle 8.7
├── Gradle-Home\     Gradle 缓存与依赖仓库（GRADLE_USER_HOME）
└── _downloads\      三件套的压缩包，装完之后可以整个删掉
```

版本是配套的：**Gradle 8.7 + AGP 8.5.2 + Kotlin 1.9.24 + JDK 17**。
AGP 8.5 要求 Gradle 8.7 或更高，且必须用 JDK 17 运行。不要单独升其中一个。

想整体卸载，删掉 `D:\app\Android` 这一个目录就够了。

## 构建

双击 `build-apk.bat`。或者在任意命令行里（cmd / PowerShell / Git Bash 都行）：

```
build-apk.bat                    :: 构建 debug APK（默认动作）
build-apk.bat test               :: 跑 :vban 的协议与发送端单元测试
build-apk.bat clean              :: 清掉构建产物
build-apk.bat clean assembleDebug  :: 从零全量重建
```

产物：

```
android\app\build\outputs\apk\debug\app-debug.apk
```

第一次构建要联网拉 AGP 和 Kotlin 插件的依赖，会比较久（本机实测 2 分 40 秒，缓存 783 MB）；
之后走缓存，全量重建约 40 秒。

工具链根目录可以覆盖：设环境变量 `AUDIOHUB_TOOLCHAIN`，默认 `D:\app\Android`。

**这两个入口是纯 cmd 的 `.bat`，刻意不用 PowerShell。** 本机 PowerShell 的
`Get-ExecutionPolicy` 是 `Restricted`（Windows 出厂默认），`.ps1` 默认跑不了；
一个"要先改安全设置才能双击"的入口不算入口。共用设置放在 `toolchain-env.bat`。

## 装机与使用

1. **先放行防火墙**：右键 `dist\allow-firewall.bat` → 「以管理员身份运行」。

   本机防火墙里存在针对 `AudioHub.Cli` 的入站**阻止**规则（多半是早先防火墙弹窗被点了
   "取消"，Windows 用写一条 Block 规则的方式记住了"不同意"）。它的作用域是 `Public`，
   而**手机热点正好被 Windows 判为 Public**，于是手机的包会被静默丢弃——现场只会看到
   `accepted=0`，没有任何报错。

   这一步会删掉那几条阻止规则，并新增一条只放行「本地子网 → UDP 6980」的允许规则。
   （必须删而不能只加：Windows 防火墙里 Block 规则优先级高于 Allow 规则。）

   > 模拟发送端（`simulate-sender.bat`）**测不出这个问题**——它走 127.0.0.1，环回流量
   > 不经过防火墙。这正是"模拟通过"不等于"手机能用"的原因之一。

2. 电脑上跑 `dist\start-vban.bat`（**不是** `start-audiohub.bat`——托盘那个程序目前还不能启用 VBAN 输入）。
3. 想单独确认包能不能到，跑 `dist\probe-vban.bat`，它每秒打印一行收包统计。
4. 把 `app-debug.apk` 拷到手机上安装（需要允许"安装未知来源应用"）。
5. 手机和电脑连**同一个网络**，优先 5 GHz。用电脑连手机热点也可以（实测拓扑见下）。
6. 打开 App，填电脑的局域网 IP（电脑上 `ipconfig` 查 WLAN 那块的 IPv4 地址），
   端口保持 6980，点"开始发送"。
7. 系统会弹出录屏授权——**必须同意**。这是 Android 抓系统声音的唯一正规途径，
   App 不会录屏，只是借这个授权来拿音频捕获能力。
8. 回到电脑窗口，看 `vban` 那一行的包计数有没有在涨。

### 电脑连手机热点时的拓扑

这种接法完全可行，而且比走路由器更干净（没有客户端隔离）。实测：

```
手机（热点，同时是网关）  192.168.x.1
电脑（热点客户端）        192.168.x.x       ← App 里填这个
```

同一个 /24 子网，二层直连。手机往 `192.168.x.x:6980` 发包走的是自己的热点接口，
先到本机协议栈、再到电脑，路径最短。

需要注意的两点：

- **每次重开热点地址可能变**，填之前用 `ipconfig` 确认一下。
- 少数国产 ROM 的热点设置里有「AP 隔离 / 客户端隔离」之类的开关，开了就会挡住。
  放行了防火墙还是收不到包，去手机热点设置里找找这个开关。

## 参数必须两边一致

VBAN 没有协商机制，格式对不上接收端会直接丢包。当前固定为：

| 项 | 值 |
|---|---|
| 采样率 | 48000 Hz |
| 声道 | 2 |
| 数据格式 | 16 位有符号整数（data_type = 1），小端 |
| 端口 | 6980 |
| 单包帧数 | 256（负载 1024 字节，不会触发 IP 分片） |
| 流名称 | 默认 `AudioHub`，电脑端默认不校验 |

电脑端对应的选项是 `AudioHubOptions.NetworkSampleRate` 和 `AudioHubOptions.Channels`。

## 界面上那几个数字是干什么的

App 不显示"运行中"这种没法验证的状态，而是直接把数字摆出来，因为出问题时
这几个数字能直接指认是哪一段断的：

- **包数在涨、峰值有数值** → 发送正常，问题在电脑端或网络。
- **包数在涨、但"含声音的包"一直是 0** → 手机发出去的是静音。多半是当前
  播放的 App 不允许被录制（`allowAudioPlaybackCapture=false`），或者这段声音
  属于被系统保护的类别。
- **包数是 0** → 发送侧根本没跑起来，看屏幕上的"错误"那一行。

## 没有手机时怎么验证

手机接进来之前，至少有两件事是可以先确认的：协议对不对、电脑那一侧收不收得下。
`simulator/` 就是干这个的。

```
:: 窗口 A：电脑端开始听（或直接双击 dist\probe-vban.bat）
cd /d <项目根目录>\dist
AudioHub.Cli.exe --probe-vban --seconds 30

:: 窗口 B：冒充手机发包（或直接双击 android\simulate-sender.bat）
cd /d <项目根目录>\android
simulate-sender.bat --seconds=15
```

窗口 A 应该看到 `accepted` 持续上涨、`rejected=0`、`lost=0`，最后打印 `PASS`。
它发的是 440 Hz 正弦，幅度 25% 满量程，所以 `peak` 应该稳定在 `0.2500`。

这个测试的价值在于它跨语言：发送端是 Kotlin，接收端是 C#，两边只靠 VBAN 规范对接。
它跑通了，就说明**手机将来发出来的字节，电脑认得**——协议这一轴上不再有未知项。

## 已知限制

- **抓不到的声音**：受 DRM 保护的音频（部分流媒体）、把
  `allowAudioPlaybackCapture` 设成 `false` 的 App、通话音频。
  这不是本项目的缺陷，是 Android 的既定规则。
- **必须有 Wi-Fi**：手机和电脑要能互通 UDP。公共 Wi-Fi 常开了客户端隔离，
  这种情况下包到不了，换手机热点或家里的路由器。
- **熄屏**：捕获跑在前台服务里并持有唤醒锁，熄屏能继续；但系统如果强制
  停止本应用（"省电模式"里被限制），捕获会断。

## 验证边界（重要）

### 已在本机跑通（可复现）

- **工具链可用**：`java -version` = Temurin 17.0.20.1，`gradle --version` = 8.7，
  `sdkmanager --version` = 12.0，SDK 组件 build-tools 34.0.0 / platform-tools 37.0.1 /
  platforms;android-34 均已安装。
- **构建**：`clean + assembleDebug` 全绿，**0 警告 0 错误**，产出 `app-debug.apk`
  （2,377,336 字节，含 `AndroidManifest.xml`、`classes.dex`、`resources.arsc`）。
- **单元测试 39/39 通过**（`gradle :vban:test`）：
  - `VbanPacketWriterTest` 20 项——报文头逐字节对齐 VBAN 规范 revision 13，
    含采样率索引表、`n-1` 编码、数据类型与保留位、帧计数小端与回绕、非法输入拒绝。
  - `VbanUdpSenderTest` 19 项——**UDP 本地环回**，把真正发出去的字节收回来核对：
    头部各字段、载荷逐字节相同、超过 256 帧时正确分片、不足一帧的零头被丢弃、
    统计口径（包/帧/字节/峰值）、静音判定、负采样取绝对值、不可解析的主机立刻抛错。
- **跨语言互操作（关键的一条）**：用 `simulator` 发、电脑端真实接收，
  30 秒内 **accepted=2481 / rejected=0 / lost=0 / reorder=0，peak=0.2500，connected=True，PASS**。
  `peak` 恰好等于发送端设定的 0.25，说明 Kotlin 侧 `×32767` 写入与 C# 侧 `÷32768`
  还原在两端完全一致；0 拒绝说明报文头被对方解析器完整接受。

### 没有验证过（不要把下面这些当成已完成）

- **任何真机上的实际运行。从未在真实 Android 设备上装过、跑过。**
  上面所有结论都来自电脑上的 JVM 与 Windows 程序，没有一行是在手机 CPU 上执行的。
- 手机用 `MediaProjection` + `AudioPlaybackCapture` 抓到的到底是不是系统声音、音质如何。
- 手机 → 电脑这条 Wi-Fi 链路的实际丢包与延迟，以及最终"手机出声到耳机出声"的
  端到端延迟。这些属于物理链路，只能现场用耳朵和计时确认。
- 不同厂商 ROM 对 `MediaProjection` + `AudioPlaybackCapture` 的兼容性。
  国产 ROM 在这块差异较大，如果捕获到的是静音，先查系统的隐私/权限设置。
- APK 的安装与首次授权流程（录屏授权弹窗、通知权限、前台服务是否被系统限制）。
