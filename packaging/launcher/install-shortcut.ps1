# AudioHub 一键创建"开始菜单"快捷方式（不建桌面图标）。
# 由 install-shortcut.bat 调用（带 -ExecutionPolicy Bypass，不依赖系统执行策略）。
# 用途：以后按 Win 键搜 "AudioHub" 回车就能启动；启动后程序缩到系统托盘（右下角）。

$ErrorActionPreference = 'Stop'

$root   = $PSScriptRoot
$target = Join-Path $root 'start-audiohub.bat'
$icon   = Join-Path $root 'AudioHub.App.exe'

if (-not (Test-Path -LiteralPath $target)) {
    Write-Host ''
    Write-Host '[错误] 本目录里没找到 start-audiohub.bat'
    Write-Host '请把这个脚本放到 AudioHub 解压目录（和 start-audiohub.bat 同级）再运行。'
    Write-Host ''
    exit 1
}

$ws = New-Object -ComObject WScript.Shell

$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\AudioHub'
if (-not (Test-Path -LiteralPath $startMenu)) {
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
}
$lnk = Join-Path $startMenu 'AudioHub.lnk'

$sc = $ws.CreateShortcut($lnk)
$sc.TargetPath       = $target
$sc.WorkingDirectory = $root
if (Test-Path -LiteralPath $icon) { $sc.IconLocation = "$icon,0" }
$sc.Description      = 'AudioHub - 把手机声音传到电脑播放'
$sc.Save()

Write-Host ''
Write-Host ('已创建开始菜单快捷方式：' + $lnk)
Write-Host ''
Write-Host '以后按 Win 键，输入 AudioHub，回车即可启动。'
Write-Host '启动后程序会缩到系统托盘（右下角），双击托盘图标可打开控制窗口。'
Write-Host ''
