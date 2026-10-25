# 新增一种音源

AudioHub 的扩展点是 `IAudioSource`。加一种新音源不需要改 Core、Engine 或任何界面。

下面以"iPhone 通过蓝牙接收器接入"为例走一遍完整流程。

## 1. 实现 IAudioSource

```csharp
using AudioHub.Core;

public sealed class BluetoothReceiverSource : IAudioSource
{
    public string Id => "iphone";
    public string DisplayName => "iPhone（蓝牙接收器）";
    public SourceKind Kind => SourceKind.Bluetooth;
    public AudioFormat Format { get; private set; } = AudioFormat.Default;
    public SourceState State { get; private set; } = SourceState.Idle;

    public event EventHandler<AudioFrame>? FrameReceived;
    public event EventHandler<SourceStateChangedEventArgs>? StateChanged;

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        // 打开设备或线路输入，开始回调
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        // 停止并释放
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
```

## 2. 在回调里交付帧

```csharp
private void OnSamples(byte[] buffer, int bytes, WaveFormat format)
{
    int frameCount = /* 由 bytes 和 format 算出 */;
    if (frameCount <= 0) return;

    using AudioFrame frame = AudioFrame.RentFrames(Format, frameCount, DateTime.UtcNow.Ticks);
    WaveSampleConverter.ToFloat(buffer.AsSpan(0, bytes), format, frame.WritableSamples);
    FrameReceived?.Invoke(this, frame);
}
```

三条硬性规则：

- **帧由音源拥有**，事件返回后立即 `Dispose`。订阅者只能在回调期间读它。
- **事件是同步的**。不要在回调里 await、不要阻塞、不要分配大对象。
- **每次状态变化都要发 `StateChanged`**，界面靠它保持同步。

## 3. 在组合根里注册

只改 `WindowsAudioHubHost` 的构造函数：

```csharp
_iphoneSource = new BluetoothReceiverSource();
_iphoneSource.FrameReceived += (_, frame) => _mixer.Submit(_iphoneSource.Id, frame.Samples);
_mixer.AddChannel(
    _iphoneSource.Id,
    _iphoneSource.Format,
    MixerChannelOptions.Bluetooth);   // 蓝牙抖动大，缓冲给到 120 ms
```

`StartAsync` / `StopAsync` / `DisposeAsync` 里跟着启停即可。到此为止，托盘、网页控制台、
命令行全部自动多出一路，不需要改一行界面代码。

## 4. 可选：加闪避规则

手机来电时自动压低电脑音乐：

```csharp
_mixer.AddDuckingRule(new DuckingRule(
    Id: "phone-over-pc",
    TriggerChannelId: "iphone",
    TargetChannelIds: ["local"],
    DuckDb: -12f,
    ThresholdRms: 0.02f,
    AttackMs: 60,
    ReleaseMs: 400,
    HoldMs: 500));
```

## 5. 可选：网络音源

走网络的音源实现 `ITransport` 而不是直接实现 `IAudioSource`，让传输与混音解耦：

```
ITransport（VBAN / 将来的 RTP-Opus / 任何私有协议）
    └── TransportAudioSource 负责把它包成 IAudioSource
```

`TransportAudioSource` 已经在 `AudioHub.Core` 里。它转发帧、镜像连接状态，
并且**不拥有**被包装的 transport（谁创建谁释放），也不 dispose 帧 —— 帧归 transport 自己。

写一个新传输只要三步：

1. 实现 `ITransport`（`StartAsync` / `StopAsync` / `PacketReceived` / `StateChanged`）。
2. 在 `AudioHub.Hosting.TransportKind` 加一个枚举值。
3. 在 `WindowsAudioHubHost.CreateTransport` 加一个 switch 分支。

上层一行都不用改。M2 用的是 VBAN（`AudioHub.Sources.Vban`），选型理由见
[adr/0005](adr/0005-android-transport-is-vban.md)。

### VBAN 实现时的三个叮咬点

- **拒绝未知位宽**。规范列出了 12 bit 和 10 bit 数据类型，却从未给出它引用的
  `VBAN_SampleSize` 数组，字节布局无从核实。猜错会导致整个流错位，所以要拒绝并计数。
- **采样率必须配对**。VBAN 没有协商机制，混音器那一通道是开跑前就按固定格式建好的。
  采样率不一致不会被重采样，会放错速度。`VbanTransport` 的 `expectedFormat` 就是为此存在的不匹配就拒绝。
- **`nbs` 和 `nbc` 都是存 n-1**：字节值为 0 意味着 1（1 个采样 / 1 个声道）。

## 检查清单

- [ ] 实现了 `IAudioSource` 全部成员
- [ ] 帧在事件返回后被释放，订阅者不保留引用
- [ ] 回调里不分配、不阻塞、不 await
- [ ] 每个状态迁移都发 `StateChanged`
- [ ] 可重复启停
- [ ] 在 `WindowsAudioHubHost` 注册并在 Dispose 时释放
- [ ] 补一个单元测试（参考 `tests/AudioHub.Tests`）

## 传输层额外检查项

- [ ] 不会猜测损坏的报文，也不猜测未文档化的比特布局 —— 宁可拒绝并计入 `LastRejectionReason`
- [ ] 采样率 / 声道数与注册给混音器的格式一致（不一致就拒绝，不要静默放错速度）
- [ ] `StartAsync` / `StopAsync` 可重复调用
- [ ] 停止后不再向已退订的订阅者投递帧
- [ ] 有一个不依赖真机的自证测试：本地 UDP 发包 → 接收 → 断言解码结果
