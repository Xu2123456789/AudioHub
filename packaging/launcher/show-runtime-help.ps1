# 框架依赖版缺少 .NET 8 桌面运行时时的提示。
# 单独成文件而不是写进 start-audiohub.bat：批处理只能按一种代码页解释，
# 中文写进 .bat 要么显示成乱码，要么把 cmd 的行解析搅乱（实际会拿中文片段当命令执行）。
# PowerShell 走 WriteConsoleW 直接输出 Unicode，不受控制台代码页影响。

Write-Host ''
Write-Host '[AudioHub] 没有检测到 .NET 8 桌面运行时，程序无法启动。' -ForegroundColor Yellow
Write-Host ''
Write-Host '请先安装（一次性，约几十 MB，来自微软官方）：'
Write-Host '  https://dotnet.microsoft.com/download/dotnet/8.0'
Write-Host '在下载页选择“.NET Desktop Runtime 8.0.x”（注意带 Desktop 字样），'
Write-Host '安装完成后再双击 start-audiohub.bat 即可。'
Write-Host ''
Write-Host '不想安装运行时的话，改用“自包含版”发行包，那个不需要电脑上装任何东西。'
Write-Host ''

Read-Host '按回车键关闭这个窗口'
