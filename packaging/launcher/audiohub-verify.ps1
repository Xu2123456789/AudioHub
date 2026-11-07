#requires -version 5.1
# AudioHub 一键验证（中文提示）。PowerShell 实现，配合同目录的 audiohub-verify.bat 双击使用。
# 只做验证/排查；日常使用请双击 start-audiohub.bat。

Set-Location -LiteralPath $PSScriptRoot
$ErrorActionPreference = "Continue"

function Write-Step([string]$n, [string]$title) {
    Write-Host ""
    Write-Host ("==================== 第 {0} 步：{1} ====================" -f $n, $title) -ForegroundColor Cyan
}
function Write-Info([string]$t) { Write-Host $t -ForegroundColor Gray }
function Write-Do([string]$t)   { Write-Host ("  >> 你要做：" + $t) -ForegroundColor Yellow }
function Write-Look([string]$t) { Write-Host ("  >> 看这里：" + $t) -ForegroundColor Green }

Write-Host ""
Write-Host "  AudioHub 验证工具" -ForegroundColor Cyan
Write-Host "  本工具只做验证 / 排查；日常使用请双击 start-audiohub.bat。"
Write-Host "  验证时请先关掉 start-audiohub.bat（两者都要占 UDP 6980，不能同开）。"

# 第 1 步
Write-Step 1 "查看电脑 IP（手机要填这个地址）"
Write-Info "作用：手机必须知道电脑的局域网地址，才能把声音发过来。"
Write-Host ""

# 只挑能用真实地址：169.254.x.x 是"没拿到 DHCP"的空地址，127.* 是本机回环，
# 蓝牙/虚拟网卡经常就是这一类。以前把全部地址都列出来，人很容易抄错那一个。
$script:PrimaryIp = $null
$script:PrimaryAlias = ""
try {
    $routes = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue |
        Sort-Object RouteMetric
    foreach ($r in $routes) {
        $addr = Get-NetIPAddress -InterfaceIndex $r.InterfaceIndex -AddressFamily IPv4 -ErrorAction SilentlyContinue |
            Where-Object { $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "127.*" } |
            Select-Object -First 1
        if ($addr) {
            $script:PrimaryIp = $addr.IPAddress
            $script:PrimaryAlias = $addr.InterfaceAlias
            break
        }
    }
} catch { }

if ($script:PrimaryIp) {
    Write-Host ("    手机请填这个：  {0}   （网卡：{1}）" -f $script:PrimaryIp, $script:PrimaryAlias) -ForegroundColor Green
} else {
    Write-Info "没能自动认出正在用的网卡，请看下面的完整列表。"
}

Write-Host ""
Write-Info "本机可用的全部地址（已滤掉 169.254.* 和 127.*，那些填了也通不了）："
Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
    Where-Object { $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -notlike "127.*" } |
    ForEach-Object { Write-Host ("    {0,-16} {1}" -f $_.IPAddress, $_.InterfaceAlias) -ForegroundColor Gray }

Write-Host ""
if ($script:PrimaryIp) {
    Write-Do ("记下 {0}，第 5 步要用。" -f $script:PrimaryIp)
} else {
    Write-Do "记下与手机在同一 Wi-Fi 的那个地址，第 5 步要用。"
}

# 第 2 步
Write-Step 2 "检查防火墙（缺规则会自动修）"
Write-Info "作用：防火墙不放行时，手机发来的包会被静默丢掉，表现就是“什么都收不到”。"
Write-Info "需要两条规则：UDP 6980（音频）+ UDP 6979（发现）。缺 6979 时“查找电脑”永远找不到。"

function Test-AhRule([string]$ruleName) {
    netsh advfirewall firewall show rule name="$ruleName" *> $null
    return ($LASTEXITCODE -eq 0)
}

$hasAudio = Test-AhRule "AudioHub audio (UDP 6980)"
$hasDiscovery = Test-AhRule "AudioHub discovery (UDP 6979)"

if ($hasAudio -and $hasDiscovery) {
    Write-Info "两条规则都在：OK"
} else {
    if (-not $hasAudio) { Write-Info "缺 UDP 6980（音频）规则。" }
    if (-not $hasDiscovery) { Write-Info "缺 UDP 6979（发现）规则。" }
    Write-Info "正在打开防火墙配置（会弹 UAC，请点“是”）……"
    Start-Process -FilePath (Join-Path $PSScriptRoot "allow-firewall.bat") -Verb RunAs
    Write-Do "等防火墙窗口显示 Done 后，回到这里按回车继续。"
    Read-Host | Out-Null

    $hasAudio = Test-AhRule "AudioHub audio (UDP 6980)"
    $hasDiscovery = Test-AhRule "AudioHub discovery (UDP 6979)"
    if ($hasAudio -and $hasDiscovery) {
        Write-Info "现在两条规则都在：OK"
    } else {
        Write-Info "仍然缺规则。UAC 被取消的话，请手动右键 allow-firewall.bat，选“以管理员身份运行”。"
    }
}

# 第 3 步
Write-Step 3 "查看播放设备（声音会送到带 * 的那一个）"
Write-Info "作用：确认声音输出到你的蓝牙耳机，而不是别的设备。"
Write-Host ""
& (Join-Path $PSScriptRoot "AudioHub.Cli.exe") --list-devices

# 第 4 步
Write-Step 4 "显示配对密钥（手机要填这个）"
Write-Info "作用：加密发送时，手机和电脑必须用同一把密钥。"
Write-Host ""
& (Join-Path $PSScriptRoot "AudioHub.Cli.exe") --show-key
Write-Host ""
Write-Do "抄下上面的“配对密钥”（形如 ABCDE-FGHIJ-...）。"

# 第 5 步
Write-Step 5 "在手机上填写这些参数（关键）"
Write-Info "打开手机上的 AudioHub App，按下表填写："
Write-Host ""
$ipForPhone = if ($script:PrimaryIp) { $script:PrimaryIp } else { "第 1 步记下的地址（如 192.168.1.5）" }
Write-Host ("    电脑 IP  ：{0}" -f $ipForPhone) -ForegroundColor White
Write-Host "    端口     ：6980（默认，不要改）" -ForegroundColor White
Write-Host "    流名称   ：留空（默认 AudioHub，不要改）" -ForegroundColor White
Write-Host "    密钥     ：第 4 步抄下的那串（要加密就填；不填 = 明文）" -ForegroundColor White
Write-Host ""
Write-Info "重要：加密只有在“点【查找电脑】并选中这台电脑”后才生效。"
Write-Info "      手动填 IP 时手机只会明文发送，填了密钥也不会用（这是 App 的限制）。"
Write-Host ""
Write-Do "点【查找电脑】→ 选中这台电脑 → 输入密钥 → 点【开始发送】→ 同意录屏授权。"
Write-Info "如果【查找电脑】什么都找不到，就手填第 1 步的 IP（明文也能通，只是不加密）。"

# 第 6 步
Write-Step 6 "电脑开始接收（90 秒）"
Write-Info "作用：这 90 秒里，电脑会应答手机的“查找电脑”，并接收手机发来的声音。"
Write-Info "下面每秒打印收到的包数；戴耳机应能听到手机里的声音。"
Write-Host ""
Write-Do "现在在手机上点【开始发送】。"
Read-Host "  准备好后按回车，电脑开始接收" | Out-Null
Write-Host ""
& (Join-Path $PSScriptRoot "AudioHub.Cli.exe") --vban --seconds 90

# 收尾
Write-Host ""
Write-Host "==================== 验证结束 ====================" -ForegroundColor Cyan
Write-Info "判断标准："
Write-Info "  1) 上面 accepted 数字在涨（或看到“收到包”）= 声音到达了电脑。"
Write-Info "  2) 耳机里听到手机的声音 = 完整成功。"
Write-Info "  3) 若 90 秒内 accepted 一直是 0，逐个检查："
Write-Info "     - 手机填的 IP 是第 1 步那个绿色地址、端口 6980；"
Write-Info "     - 防火墙两条规则都在（第 2 步显示 OK）；缺了就在根目录右键 allow-firewall.bat，"
Write-Info "       选“以管理员身份运行”，再重开本脚本；"
Write-Info "     - 手机和电脑在同一 Wi-Fi（手机热点也行，但要关掉“客户端隔离”）；"
Write-Info "     - 验证时没有同时开 start-audiohub.bat；"
Write-Info "     - 用【查找电脑】选中电脑（手填 IP 只能明文）。"
Write-Host ""
Read-Host "按回车关闭" | Out-Null
