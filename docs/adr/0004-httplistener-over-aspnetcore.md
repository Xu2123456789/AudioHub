# ADR 0004：网页控制台用 HttpListener，不用 ASP.NET Core

- 日期：2026-10-05
- 状态：已接受

## 背景

网页控制台需要 6 个接口和 1 个静态页，让手机在同一局域网内调音量。默认选择是
ASP.NET Core minimal API。

## 决定

改用 `System.Net.HttpListener`。

## 理由

ASP.NET Core 需要单独安装 `Microsoft.AspNetCore.App` 共享框架。实测本机
`C:\Program Files\dotnet\shared` 只有 `Microsoft.NETCore.App` 与
`Microsoft.WindowsDesktop.App`，没有 ASP.NET Core。结果是：托盘应用在开发机上能编译，
双击却起不来——对一个要常驻托盘的工具来说不可接受。

控制台的全部需求（路由、JSON 读写、发一个静态文件）用 `HttpListener` 约两百行就够了，
为此引入一个需要单独安装的框架不划算。

## 代价

HTTP.SYS 对通配前缀（`http://+:5055/`）要求 URL 预留，通常需要管理员执行一次：

```
netsh http add urlacl url=http://+:5055/ user=everyone
```

没有预留时控制台退回到只绑定 localhost，仍然可用，只是手机访问不了。程序通过
`WebConsole.IsLanVisible` 报告当前状态，并通过 `LanReservationCommand` 给出该命令，
界面会显示出来。

## 何时应重新考虑

若控制台长成真正的应用（认证、WebSocket 推送电平、多页面），`HttpListener` 的手工路由
会开始成为负担，那时应改用 ASP.NET Core，并把运行时依赖随发布包一起解决（自包含发布
或安装脚本）。
