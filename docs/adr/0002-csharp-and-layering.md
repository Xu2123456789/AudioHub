# ADR 0002：选择 C# / .NET 8，并用分层隔离平台依赖

- 日期：2026-10-05
- 状态：已接受

## 背景

需要在 C#、Rust、Python 之间选择。当前 v1 范围只在 Windows 上运行，但要求"将来留出
移植到 Linux / 树莓派的余地"，并且要求模块化、接口清晰、方便后来者接手。

## 决定

用 C# / .NET 8 + NAudio。同时把 `AudioHub.Core` 与 `AudioHub.Engine` 限制为 `net8.0`
且不含任何 Windows 或 NAudio 引用，平台相关的东西全部隔离在 `Sources.*`、`Sinks.*`、
`Platform.Windows` 里。

## 理由

- **v1 只在 Windows 上跑**。WASAPI 环回捕获是 `WasapiLoopbackCapture` 一行的事，而 Rust
  的 cpal 对 loopback 支持不完整，Python 基本没有成熟方案。
- **跨平台靠分层解决，不靠语言**。将来移植是"换 `Sources`/`Sinks` 实现"，不是"换语言"。
  为尚未确定的需求提前付 1.5–2 倍工时（Rust）不值得。
- **工程化与可接手性**。DI、xUnit、NuGet、GitHub Actions 都是内置的一等公民；C# 的
  上手门槛明显低于 Rust，这对"方便后来者开发"是决定性因素。
- Python 与"正规工程、模块化"的要求冲突（弱类型、无成熟环回方案、打包分发笨重），否决。

## 代价

- 目标机器需要 .NET 8 运行时（Windows 常见，通常已具备）。
- 有 GC。已通过预分配缓冲和 `ArrayPool` 池化规避实时线程上的分配。
- 分发体积比 Rust 单文件大；如需要单文件可用 AOT 发布（尚未做）。

## 验收方式

`AudioHub.Core` 与 `AudioHub.Engine` 的 csproj 目标框架为 `net8.0`，且两者不引用
NAudio 或任何 `net8.0-windows` 项目。这一条应在 code review 中守住。
