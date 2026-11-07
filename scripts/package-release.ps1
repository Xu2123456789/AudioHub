<#
  AudioHub 发行打包 —— 一条命令从源码生成可分发的全部产物。

  生成什么：
    dist\                     框架依赖版（电脑上需要 .NET 8 桌面运行时）
    dist-self-contained\      自包含版（自带运行时，70 MB 左右）
    publish\AudioHub-PC-framework-dependent.zip
    publish\AudioHub-PC-self-contained.zip
    publish\AudioHub-Sender-<版本>.apk   （若安卓包已经构建过）

  为什么要有这个脚本：这些启动脚本以前只存在于 dist\ 里，不在版本库中；
  谁清一次 dist\ 就永久丢失，别人 clone 仓库构建出来的程序也没有入口脚本。
  现在脚本的唯一位于 packaging\launcher\，由本脚本负责拷进发行目录。

  用法（在仓库根目录）：
    powershell -NoProfile -ExecutionPolicy Bypass -File scripts\package-release.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    # 只重新打包（不重新编译），用于只改了启动脚本的情况
    [switch]$ZipOnly
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$launcher   = Join-Path $root 'packaging\launcher'
$publishDir = Join-Path $root 'publish'
$zipTool    = Join-Path $env:SystemRoot 'System32\tar.exe'

# ---------------------------------------------------------------- 前置检查

# 正在运行的托盘程序会锁住 dist\*.dll，publish 到最后一步才会报 MSB3027，
# 那时候已经白等了两分钟。不如一开始就说清楚。
$tray = Get-Process -Name 'AudioHub.App' -ErrorAction SilentlyContinue
if ($tray) {
    $pids = ($tray | ForEach-Object { $_.Id }) -join ', '
    throw "托盘程序正在运行（PID $pids），它锁住了发行目录里的 dll。请先右键托盘图标退出，再重跑本脚本。"
}

if (-not (Test-Path $launcher)) { throw "找不到启动脚本目录：$launcher" }
if (-not (Test-Path $zipTool))  { throw "找不到打包工具：$zipTool" }
if (-not (Test-Path $publishDir)) { New-Item -ItemType Directory -Path $publishDir | Out-Null }

# 光看 PATH 上有没有 dotnet 是不够的：只装运行时的机器也有 dotnet.exe，
# 它能跑程序但执行不了 publish（报 "No .NET SDKs were found"，退出码是个负数）。
# 本机就是这个情况：C:\Program Files\dotnet 只有运行时，SDK 装在 LOCALAPPDATA。
# 所以逐个候选去看它旁边有没有 sdk 目录，取第一个真正带 SDK 的。
$candidates = New-Object System.Collections.Generic.List[string]
$localSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'
if (Test-Path $localSdk) { $candidates.Add($localSdk) }
$cmd = Get-Command dotnet -ErrorAction SilentlyContinue
if ($cmd) { $candidates.Add($cmd.Source) }
$candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))

$dotnetExe = $null
$seenSdkLess = @()
foreach ($c in $candidates) {
    if (-not (Test-Path $c)) { continue }
    $sdkDir = Join-Path (Split-Path -Parent $c) 'sdk'
    if (Test-Path $sdkDir) {
        $dotnetExe = $c
        break
    }
    $seenSdkLess += $c
}
if (-not $dotnetExe) {
    $detail = ''
    if ($seenSdkLess.Count -gt 0) { $detail = "（找到的 dotnet 都没有 sdk 目录：$($seenSdkLess -join '; ')）" }
    throw "找不到带 SDK 的 dotnet。请装 .NET 8 SDK 后重试。$detail"
}

# ---------------------------------------------------------------- 发布

function Invoke-Publish {
    param(
        [string]$OutDir,
        [bool]$SelfContained
    )

    $flag = 'false'
    if ($SelfContained) { $flag = 'true' }

    foreach ($proj in @('src\AudioHub.App\AudioHub.App.csproj', 'src\AudioHub.Cli\AudioHub.Cli.csproj')) {
        Write-Host "  发布 $(Split-Path -Leaf $proj) ..." -ForegroundColor DarkGray
        & $dotnetExe publish (Join-Path $root $proj) -c Release -r $Runtime --self-contained $flag -o $OutDir
        if ($LASTEXITCODE -ne 0) { throw "发布失败：$proj（退出码 $LASTEXITCODE）" }
    }

    # 启动脚本不在 csproj 里，必须显式拷进来，否则包装好了却没有入口。
    Copy-Item (Join-Path $launcher '*') $OutDir -Force
    Write-Host "  已拷入启动脚本：$((Get-ChildItem $launcher).Count) 个" -ForegroundColor DarkGray
}

$targets = @(
    @{ Dir = Join-Path $root 'dist';              SelfContained = $false; Zip = 'AudioHub-PC-framework-dependent.zip' },
    @{ Dir = Join-Path $root 'dist-self-contained'; SelfContained = $true;  Zip = 'AudioHub-PC-self-contained.zip' }
)

if (-not $ZipOnly) {
    foreach ($t in $targets) {
        Write-Host "==> $($t.Dir)" -ForegroundColor Cyan
        Invoke-Publish -OutDir $t.Dir -SelfContained $t.SelfContained
    }
}

# ---------------------------------------------------------------- 打包

# 用 Windows 自带的 bsdtar：-a 按扩展名选 zip 格式，--exclude 能直接排除 .pdb。
# （Git Bash 里的 GNU tar 也认 -a，但它产出的其实是改了后缀的 tar，装了会打不开。）
foreach ($t in $targets) {
    $zipPath = Join-Path $publishDir $t.Zip
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Write-Host "==> 打包 $($t.Zip)" -ForegroundColor Cyan
    & $zipTool -a -c -f $zipPath '--exclude=*.pdb' -C $t.Dir '.'
    if ($LASTEXITCODE -ne 0) { throw "打包失败：$zipPath" }

    $magic = [System.IO.File]::ReadAllBytes($zipPath)[0..3]
    if (-not ($magic[0] -eq 0x50 -and $magic[1] -eq 0x4B)) {
        throw "产出的不是 zip（魔数不对）：$zipPath"
    }
    $mb = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)
    Write-Host "    $mb MB" -ForegroundColor DarkGray
}

# ---------------------------------------------------------------- 安卓包

# 本脚本不负责构建安卓端（那是 android\build-apk.bat 的事），只把已构建好的 debug APK
# 归档到 publish\。危险在于：app-debug.apk 可能是上一版留下的旧包，这里却按
# build.gradle.kts 的新版本号命名 —— 名字对、内容旧，而且全程零报错。
# 所以归档前用 aapt2 读出 APK 里真正的 versionName，与源码版本号对一次。
$apk = Join-Path $root 'android\app\build\outputs\apk\debug\app-debug.apk'
if (Test-Path $apk) {
    $gradle = Join-Path $root 'android\app\build.gradle.kts'
    $version = 'unknown'
    if (Test-Path $gradle) {
        $m = Select-String -Path $gradle -Pattern 'versionName\s*=\s*"([^"]+)"' | Select-Object -First 1
        if ($m) { $version = $m.Matches[0].Groups[1].Value }
    }

    $aapt2 = $null
    $sdk = $env:ANDROID_HOME
    if (-not $sdk) { $sdk = 'D:\app\Android\Android-SDK' }
    $bt = Join-Path $sdk 'build-tools'
    if (Test-Path $bt) {
        $candidate = Get-ChildItem -Path $bt -Directory | Sort-Object Name -Descending | Select-Object -First 1
        if ($candidate) {
            $probe = Join-Path $candidate.FullName 'aapt2.exe'
            if (Test-Path $probe) { $aapt2 = $probe }
        }
    }

    if ($aapt2) {
        $badging = (& $aapt2 dump badging $apk 2>&1) | Out-String
        $n = [regex]::Match($badging, "versionName='([^']+)'")
        if (-not $n.Success) { throw "读不出 APK 的 versionName：$apk" }
        if ($n.Groups[1].Value -ne $version) {
            throw "APK 是旧版本：包里是 $($n.Groups[1].Value)，源码里是 $version。请先跑 android\build-apk.bat 重新出包，再执行本脚本。"
        }
        Write-Host "     APK 版本核对：$($n.Groups[1].Value) = 源码 $version" -ForegroundColor DarkGray
    } else {
        Write-Host '     警告：找不到 aapt2，无法核对 APK 版本。请确认已先跑过 android\build-apk.bat。' -ForegroundColor Yellow
    }

    $apkOut = Join-Path $publishDir "AudioHub-Sender-$version.apk"
    Copy-Item $apk $apkOut -Force
    Write-Host "==> 安卓包 -> $(Split-Path -Leaf $apkOut)" -ForegroundColor Cyan
} else {
    Write-Host '==> 跳过安卓包（还没构建过；先跑 android\build-apk.bat）' -ForegroundColor Yellow
}

# ---------------------------------------------------------------- 收尾核对

Write-Host ''
Write-Host '产物版本：' -ForegroundColor Cyan
$cli = Join-Path $root 'dist\AudioHub.Cli.exe'
if (Test-Path $cli) { & $cli --version }

Write-Host ''
Get-ChildItem $publishDir | ForEach-Object {
    Write-Host ("  {0,-42} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB))
}
