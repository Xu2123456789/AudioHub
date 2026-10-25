# 架构

## 一句话

AudioHub 是一条**拉取式**音频管线：输出设备的时钟决定一切。

```
音源 ──推送──> 混音器通道（抖动缓冲）──拉取──> 混音求和 ──拉取──> 输出设备
                                          ↑
                                    输出设备的渲染线程
```

输出设备每要一个块，就向混音器要一个块；混音器向每个通道要一个块；通道从自己的抖动缓冲里取。
中间没有任何定时器，也没有额外队列。

## 为什么是拉取式

推送式设计需要在混音器和设备之间插一个队列，队列得有自己的时序，于是多了一级缓冲和第二个时钟。
蓝牙链路本来就慢，再叠一级缓冲只会更慢。拉取式让设备时钟一路传导到音源，延迟只由设备缓冲决定。

代价是：混音器的 `Read` 运行在设备的实时渲染线程上，里面不能分配内存、不能阻塞、不能拿竞争激烈的锁。
`MixerEngine` 的所有工作缓冲都是预先分配好的，只在块长变大时才扩容。

## 模块分层

| 项目 | 目标框架 | 职责 | 平台依赖 |
| --- | --- | --- | --- |
| `AudioHub.Core` | net8.0 | 接口与领域模型 | 无 |
| `AudioHub.Engine` | net8.0 | 混音算法 | 无 |
| `AudioHub.Platform.Windows` | net8.0-windows | 端点枚举、PCM 转换 | NAudio |
| `AudioHub.Sources.WindowsLoopback` | net8.0-windows | 本机声音捕获 | NAudio |
| `AudioHub.Sinks.Wasapi` | net8.0-windows | 输出渲染 | NAudio |
| `AudioHub.Hosting` | net8.0-windows | 组合根 | 上述全部 |
| `AudioHub.Web` | net8.0-windows | 控制 API | 无（HttpListener） |
| `AudioHub.App` | net8.0-windows | 托盘 UI | WPF + WinForms |
| `AudioHub.Cli` | net8.0-windows | 命令行 | — |

`AudioHub.Core` 和 `AudioHub.Engine` 编译到 `net8.0` 且不含 Windows API 引用，这是刻意的约束：
将来要把中枢搬到 Linux 或树莓派，只需要写 `Sources.*` 和 `Sinks.*` 的新实现（bluez-alsa 捕获 +
BlueZ 发射），Core、Engine、Web、以及所有界面都不用改。

## 核心接口

```
IPcmSource      拉取源：Format / Read(Span<float>)
IAudioSource    音源：Id / Kind / Format / State / FrameReceived / Start / Stop
IAudioSink      输出：Id / Format / IsRunning / Start / Stop
IMixerEngine    混音器：继承 IPcmSource，另有通道管理、增益、静音、闪避
ITransport      网络传输（M2 用，安卓与 PC 之间的接缝）
```

`IAudioSource` 的 `FrameReceived` 是同步事件，不是 `Task`。音源回调跑在有硬 deadline 的
线程上，在那里 await 会引入调度抖动。

## 混音引擎内部

每次 `Read` 分三遍：

1. **取数**：每个通道从抖动缓冲取一个块，必要时重采样到输出时钟，同时算出 RMS 电平。
2. **闪避**：用刚才算出的电平判断哪些闪避规则生效，平滑更新每个通道的闪避系数。
3. **求和**：按通道增益与闪避系数累加，最后削波。

必须分三遍：电平要先测出来才能判断闪避，闪避要先定下来才能求和。

### 采样率

通道可以是任意采样率和单/双声道。引擎用线性插值重采样，单声道上混为双声道。
线性插值对混合几个百分点的速率差异足够，降采样时会损失一点高频——换更好的重采样器
只需替换 `LinearResampler`。

### 时钟域

每条输入流有自己的采样时钟，彼此没有共享时钟。抖动缓冲吸收这个差异，并在欠载时补零、
重新预填充，避免一条流一卡就一直卡。

### 平滑

任何增益变化都是渐变而不是跳变，否则会爆音。

这里踩过一个坑：平滑系数有"每采样点"和"每块"两种形式。电平和闪避是每块更新一次，
如果套用每采样点的系数，时间常数会被放大一个块长那么多倍（实测几百倍）。
`MixerEngine.BlockCoefficient` 就是为此存在的，`Decibels.SmoothingCoefficient` 只用于
每采样点更新的增益渐变。

## 内存

`AudioFrame` 从 `ArrayPool<float>` 租借。音源回调和渲染线程都不应该在稳态分配内存，
否则 GC 会在最不合适的时候介入。

## 延迟账

| 环节 | 典型 |
| --- | --- |
| WASAPI 环回捕获 | 0（抓的就是已经在播的混音） |
| 通道抖动缓冲 | 20 ms（本机） |
| 混音与重采样 | < 1 ms |
| WASAPI 输出缓冲 | 50 ms（可调，见 `--buffer`） |
| 蓝牙耳机自身 | 100–250 ms |

前四项合计约 70 ms，最后一项是耳机决定的，软件改不了。所以"打游戏"的体验等同于你
现在戴蓝牙耳机打游戏，不会更差。

蓝牙只用在最后一跳。为什么不全程走蓝牙，见 [adr/0003](adr/0003-bluetooth-only-hub-is-not-viable.md)。
