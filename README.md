# AudioHub

把多台设备的声音混成一路，送进同一副蓝牙耳机。

> **关于开发方式**：本项目的代码、测试与文档是在 **AI 辅助下**完成的；需求、技术取舍与验收标准由作者把关。
> 凡无法自动验证的部分（听感、界面观感等）都在文档中明确标注「未验证」，不冒充已验证。

蓝牙耳机支持多点连接，但同一时刻只会播出一路。AudioHub 让一台 Windows 机器充当混音中枢：
本机播放的声音、安卓手机的声音（将来还包括 iPhone）各自进入混音器，混好之后从你选的
输出端点（通常是蓝牙耳机）播出。

## 当前进度

M0 / M1 已完整跑通：仓库骨架、接口层、混音引擎、WASAPI 环回捕获与输出、托盘应用、
网页控制台、单元测试。M2 安卓接入的基本通路（手填 IP + 明文 + VBAN）也已真机打通，
剩下的真机确认项写在表格备注里，不冒充已验证。

| 里程碑 | 内容 | 状态 |
| --- | --- | --- |
| M0 | 解决方案骨架、Core 接口层 | ✅ 完成 |
| M1 | 单路跑通：本机环回 → 混音 → 蓝牙耳机；托盘 + 网页控制台 | ✅ 完成 |
| M2 | 安卓手机接入（VBAN 接收端 + 自研发送端 App） | 🔶 真机已联通；零配置发现 + 可选加密已完成（电脑端已实测，**手机端待现场确认**）；音质待耳朵确认 |
| M3 | 来电自动闪避、每路音量、配置持久化、断线自愈 | ⬜ 未开始 |
| M4 | 端到端延迟实测与调优 | 🔶 软件侧完成（`--latency` 只到 WASAPI 端点），蓝牙段未测 |
| M5 | iPhone 接入 | ⬜ 未开始 |

（里程碑与 `CHANGELOG.md` 的记录对应。）

## 下载哪个版本

发布包有两个版本，**功能完全一样**，区别只在"要不要先在电脑上装 .NET 运行时"：

| 版本 | 大小 | 适合谁 | 电脑上要先装什么 |
| --- | --- | --- | --- |
| `AudioHub-PC-self-contained.zip` | 约 67 MB | 给别人用，或不想折腾 | **什么都不用装**（运行时已打包进去） |
| `AudioHub-PC-framework-dependent.zip` | 约 0.8 MB | 只在自己电脑用、在意体积 | 需要 .NET 8 桌面运行时（缺失时启动脚本会提示并给出下载链接） |

安卓端是 `app-debug.apk`，装到手机上即可。两者都解压后双击 `start-audiohub.bat` 启动。

## 快速开始（不需要命令行）

进入解压出来的文件夹（里面就有 `start-audiohub.bat`），**双击 `start-audiohub.bat`**。

应用会打开控制窗口，并在系统托盘留一个图标（在右下角，可能收在 `^` 箭头里）。
关掉窗口它仍在后台运行，双击托盘图标可再次打开。窗口里可以切换输出端点、调节每路音量。

想确认它真的在工作，双击 **`audiohub-verify.bat`**：它会走六步——显示本机 IP（手机要填这个）、
检查并修复防火墙、列出播放设备、显示配对密钥、给出手机上该填的参数、最后开 90 秒接收窗口。
日常排查只用这一个问题入口。

**自包含版**不需要在电脑上安装任何东西；**框架依赖版**要求 Windows 上已有 .NET 8 桌面运行时 —— 缺失时双击 `start-audiohub.bat` 会给出中文提示和官方下载链接，不会闪退。

## 手机连不上时，先双击这一个

**双击 `audiohub-verify.bat`**（唯一的问题入口）：它一次给出本机 IPv4（手机要填的地址）、
UDP 6980/6979 的防火墙规则并检查/修复、默认播放设备、配对密钥、手机上该填的参数，
最后开 90 秒接收窗口，让你当场验证「发现」和「加密链路」是否通过。换网络或声音不来时先跑它。

> 防火墙首次配置需要管理员权限：`audiohub-verify.bat` 检测到规则缺失时会自动弹出 UAC 提权，
> 你点"是"即可，之后永久生效，日常不用再管。

手机上填好 IP 还是没声音时，`audiohub-verify.bat` 的加密链路检查会在结束时列出三条最常见原因——不用猜。

## 从源码构建

需要 .NET 8 SDK。当前装在 `<用户目录>\AppData\Local\Microsoft\dotnet`
（若需改到其他目录请自行重装，构建脚本不依赖固定位置）。构建时把它加进 PATH：

```bash
export PATH="$LOCALAPPDATA/Microsoft/dotnet:$PATH"   # Git Bash
dotnet build
dotnet test
# 框架依赖版（目标机需有 .NET 8 桌面运行时）
dotnet publish src/AudioHub.App/AudioHub.App.csproj -c Release -o dist
dotnet publish src/AudioHub.Cli/AudioHub.Cli.csproj  -c Release -o dist

# 自包含版（把运行时一起打包，解压约 160 MB，电脑零安装）
dotnet publish src/AudioHub.App/AudioHub.App.csproj -c Release -o dist-self-contained -r win-x64 --self-contained true -p:WindowsDesktopEulaAccept=true
dotnet publish src/AudioHub.Cli/AudioHub.Cli.csproj  -c Release -o dist-self-contained -r win-x64 --self-contained true -p:WindowsDesktopEulaAccept=true
```

在 PowerShell 里则是：

```powershell
$env:Path = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:Path"
dotnet build
```

### 命令行用法

```bash
AudioHub.Cli.exe --selftest          # 自检，输出 PASS/FAIL
AudioHub.Cli.exe --list-devices      # 列出播放端点及其 ID
AudioHub.Cli.exe --latency           # 测量输出端点的软件侧延迟，输出 JSON
AudioHub.Cli.exe --probe-vban        # 监听 VBAN 包；每秒打印收包计数 + 到达间隔统计
AudioHub.Cli.exe --vban              # 带 VBAN（安卓）音源运行；打印每通道缓冲健康度
AudioHub.Cli.exe --vban --jitter 250 # 网络通道缓冲 250 ms（默认 80），代价是 +170 ms 延迟
AudioHub.Cli.exe --vban --local     # 把本机声音也纳入混音器（默认不抓，见下）
AudioHub.Cli.exe --device <ID> --buffer 30 --seconds 20
AudioHub.Cli.exe --web --port 5055   # 附带启动网页控制台
```

`--probe-vban` 的"到达间隔"行是排查音质问题的第一站：

```
到达间隔：标称 5.33 ms  实测 p50=5.0 p95=7.0 max=10.1 ms  突发 0 批 · 最大 0 包/批  => 看起来是均匀到达
```

`p95` 只有几毫秒说明包是均匀到的；`p95` 是几十到几百毫秒、`突发 N 批`在涨，说明包被
网络攒成一批批下发——那种情况下抖动缓冲会在每批的头尾各丢一段音频，
听感就是噪音加忽大忽小。加 `--jitter` 能覆盖更宽的突发，代价是等量延迟。

`--jitter` 可在命令行用 `AudioHub.Cli.exe --vban --jitter 250` 调整（默认 80），
用来和默认配置做 A/B 对比，代价是等量延迟。

### 为什么默认不抓本机声音

`--vban` 默认**不**采集本机的播放。这不是省事，是修一个 bug：混音器通常渲染到蓝牙耳机，
而耳机同时又是 Windows 的默认播放设备，回环抓"默认播放设备"就等于抓混音器自己的输出 ——
声音绕一圈回来再叠一次，每圈延迟 60–100 ms，听感是金属味的噪音叠加音量忽大忽小。
本机的声音本来就由 Windows 直接混进耳机，不需要我们再抓一遍。

代价是本机那一路没有独立音量和闪避。想要的话用命令行 `AudioHub.Cli.exe --vban --local`
（= `--local`），它会**自动改抓一个不是输出设备的端点**（通常是扬声器），
启动时的"本机采集"那一行会写明实际抓的是哪个。

`--latency` 的数字**只到 WASAPI 端点为止**，不含蓝牙耳机那一段 100–250 ms；
这一点写在它输出的 `Notes` 里。它真正能回答的是「这些参数下排队的
音频有多少毫秒，以及再往下压会不会就开始欠载」。

`--probe-vban` 用来回答"手机到底有没有连上"：每隔一秒打印收包数 / 丢包数 / 峰值，
**有包则退出码 0，一个包都没收到则退出码 1**。它只能证明包到了，证明不了音质。

## 把安卓手机接进来（M2，已实现）

用的是 **VBAN 协议**（开放标准，任何平台都能自己实现）。AudioHub 自带发送端 App
（`android\build-apk.bat` 构建），也仍能被任何第三方 VBAN 发送端使用。
不需要数据线、不需要 root、不需要装驱动，也不需要 Voicemeeter。

**现在的正常流程是三步，不用查 IP：**

1. 电脑上双击 `start-audiohub.bat`。托盘应用会在 UDP 6979 等待手机询问（自动发现）。
2. 手机上点 **「查找电脑」**，点出现的这台电脑，再点「开始发送」并同意录屏授权。
3. 想加密就再走一遍配对：双击 `audiohub-verify.bat` 抄下它显示的密钥，
   在手机弹出的密钥框里输一次——**之后它会记住**。

不想用发现也可以手填 IP（和以前一样），或者用 `--no-discovery` 关掉发现。

```bash
AudioHub.Cli.exe --probe-vban --seconds 10     # 先确认包有没有打过来
AudioHub.Cli.exe --vban --seconds 60           # 确认真有包了，再正式跑
AudioHub.Cli.exe --secure on --vban            # 只收加密包（需要手机先配对）
```

**采样率要对上**：手机发过来的是 48 kHz 立体声以外的组合会直接被拒绝（默认期望 48 kHz
立体声），因为 VBAN 没有协商机制，放错速度是我们宁可不要的结果。被拒绝的理由会打印出来。

选型理由（为什么是 VBAN 而不是 scrcpy / roc-toolkit / AudioRelay）见
[adr/0005](docs/adr/0005-android-transport-is-vban.md)。

`--buffer` 是 WASAPI 缓冲毫秒数，也是蓝牙延迟的主要调节旋钮：越小延迟越低，但在
2.4 GHz 拥挤时更容易爆音。

## 项目结构

```
src/AudioHub.Core                     接口 + 领域模型，零平台依赖
src/AudioHub.Engine                   混音引擎：增益、闪避、重采样、抖动缓冲
src/AudioHub.Platform.Windows         WASAPI 端点枚举与 PCM 转换
src/AudioHub.Sources.WindowsLoopback  本机声音捕获（WASAPI 环回）
src/AudioHub.Sources.Vban             安卓音频接收（VBAN over UDP，net8.0，无平台依赖）
src/AudioHub.Sinks.Wasapi             输出端点渲染
src/AudioHub.Hosting                  组合根，界面共用
src/AudioHub.Web                      控制 API 与控制台页面
src/AudioHub.App                      托盘应用（WPF）
src/AudioHub.Cli                      命令行
tests/AudioHub.Tests                  单元测试
android/vban                          手机端 VBAN 编码与发送（纯 Kotlin/JVM，不依赖 Android）
android/protocol                      发现协议 + AES-256-GCM 加密信封（纯 Kotlin/JVM，同上）
android/app                           手机端 App：回放捕获 + UDP 发送 + 查找电脑 + 配对界面
android/simulator                     在电脑上冒充手机的开发工具（能发明文，也能发加密包）
```

`AudioHub.Core` 与 `AudioHub.Engine` 面向 `net8.0`，不含任何 Windows 或 NAudio 引用——
这是"将来移植到 Linux 只需换实现"的保证。详见 [docs/architecture.md](docs/architecture.md)。

新增一种音源见 [docs/plugin-guide.md](docs/plugin-guide.md)。
设计取舍记录在 [docs/adr/](docs/adr/)。

## 已知限制

- **本机那一路默认不采集**（这是修一个自激 bug，不是省事，见上）。要本机独立音量或闪避，
  需要你在 Windows 里把默认播放设备改成扬声器，再用命令行 `AudioHub.Cli.exe --vban --local`。
- **发现失败通常是 AP 隔离**：有些路由器（或"访客网络"）不允许同一 Wi-Fi 下的设备互相广播。
  表现是手机上"查找电脑"什么都找不到，但电脑那边的收包窗口也没反应。
  绕法：关掉 AP 隔离 / 换一个 SSID，或退回手填 IP。
- 换网络后，手填过 IP 的话要重填（双击 `audiohub-verify.bat` 第一步会显示当前 IP）。
  **用"查找电脑"选中的那台不用改**，它记的是身份而不是地址。
- **Windows 开入站端口必须管理员**：`dist\allow-firewall.bat` 双击一次（会自己弹 UAC），之后永久生效。
  它现在同时放行 UDP 6980（音频）和 6979（发现）。
- 电平表与闪避的平滑按渲染块更新，块长由输出设备决定。
- **输出上限默认 −6 dBFS**（软拐点限幅，`--ceiling` 可调）。日常内容远低于拐点，
  所以正常听感不变；`--ceiling 0` 才是关闭保护。
- **电脑端密钥被重新生成后，手机不会知道。** 如果你删掉
  `%LOCALAPPDATA%\AudioHub\key.json`（或者换了台电脑但手机还记着旧电脑的身份），
  手机手里那把旧密钥就再也解不开了，表现是**安静地连不上**。
  好在电脑端会明确说出来：`audiohub-verify.bat` 的加密链路检查结束时打印
  `Last rejection: 认证失败：密钥不对，或包在传输中被改动、重放过`。
  看到这一行就去重配一次（目前只能在手机的应用信息里"清除数据"，再重新配对）。
- 手机端抓不到受 DRM 保护的音频，也抓不到把 `allowAudioPlaybackCapture` 设为 `false` 的应用——
  这是 Android 的既定规则，不是本项目的缺陷。
- 真机上的到达是**批量**的（`max` 间隔 191.8 ms > 默认 80 ms 缓冲），所以默认配置下
  约每 2 秒仍有一次轻微断续。可用 `--jitter 250` 换，代价 +170 ms 延迟。
- APK 是 debug 签名，安卓会提示"未验证应用"。

## 安全

**先说最容易被误会的一点。** 手机端那个"开始录制或投射"的弹窗看起来像投屏，
但它不是投屏：Android 10 起抓手机内部声音只有 `AudioPlaybackCapture` 一条路，
而这必须先拿 `MediaProjection` 授权——同一把钥匙也开得了抓屏，**但本项目没有开**。
全项目搜不到 `VirtualDisplay` / `ImageReader` / `MediaCodec` / `Bitmap`，
拿到授权后只建了一个 `AudioRecord` + `AudioPlaybackCaptureConfiguration`。
**操控屏幕更不可能**：那需要 `INJECT_EVENTS`，是系统级签名权限，第三方 App 申请不到。

**最严重的后果不是被偷东西，是耳朵受伤。** 往耳机里塞一声巨响的门槛极低，
而且发送方并没有做违规的事——它只是在放很响的音频。所以护耳的措施排在加密前面。

**实际存在的风险：**

| 风险 | 后果 | 现状 |
| --- | --- | --- |
| 输出没有音量上限 | 一声巨响直接伤耳 | ✅ **已修**：默认 **−6 dBFS 天花板** + 软拐点限幅，任何输入都超不过。`--ceiling` 可调，`0` 才是关闭 |
| VBAN 本身不加密、不鉴权 | 同一局域网内任何人可往你耳机里塞声音 | 🔶 **可选加密已实现**，但**默认没开**——见下 |
| 手机与电脑之间的人可能窃听 | 听到你在放什么 | 🔶 同上 |
| 网页控制台开了局域网 | 同网的人可改音量/静音 | 默认只监听 localhost；**不要执行** `netsh http add urlacl ... user=everyone` |
| 防火墙放行范围是"整个局域网" | 比"只允许手机一个 IP"宽 | `remoteip=LocalSubnet`，已限定程序与端口 |
| APK 未正式签名 | 安卓提示"未验证应用" | debug 签名 |

**关于加密，必须说清楚三件事，否则会误以为已经安全了：**

1. **默认是 `auto`**：加密的收、明文的也收。这是为了手机还没配对时也能先用起来。
   **真正要挡住同网的人，电脑端要用 `--secure on`（只收加密包），手机端要完成一次配对。**
2. **加密的难点不是算法，是密钥从哪来。** 密钥不经过网络：双击 `audiohub-verify.bat`
   显示一次，人眼抄下来、手上输进手机。这正是它不需要密钥交换协议的原因——
   **电脑屏幕 → 人的眼睛 → 手机键盘**是一条攻击者碰不到的信道。
   手机上输一次就记住了（存在 Android KeyStore 里）。
3. **加密挡不住"你自己手机放很响的声音"。** 那完全合法。护耳靠的是上面那条输出天花板，
   和加密是互补关系，不是替代关系。

**已经做对的：** 安卓端只申请 6 个权限（网络、网络状态、录音、前台服务、通知、唤醒锁），
**新增发现功能没有加任何权限**；没有通讯录/位置/存储/电话；不采集麦克风；
`allowBackup="false"`；捕获服务 `exported=false`；授权每次都要重新点同意，不同意就什么都抓不到。

**如果你在意**，性价比最高的三条：把电脑端改成 `--secure on` 并完成一次配对、
用 `--stream <名字>` 只收指定流、把防火墙规则从"局域网"收紧到手机的固定 IP。

## 许可

MIT，见 [LICENSE](LICENSE)。
