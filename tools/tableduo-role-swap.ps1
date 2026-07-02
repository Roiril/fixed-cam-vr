# TableDuo 役割交代スクリプト
# 2 台の Quest の tdv_role（full/hand）を反転してアプリを再起動する。
# 使い方:
#   .\tableduo-role-swap.ps1                          # 接続中 2 台を自動検出し、現在の割当を尋ねて反転
#   .\tableduo-role-swap.ps1 -FullSerial AAA -HandSerial BBB -HostIp 192.168.1.10
#     → AAA(full/host) と BBB(hand/client) を入れ替えて再起動（AAA が hand/client、BBB が full/host に）
param(
    [string]$FullSerial,   # 現在 full（=host）役の Quest serial
    [string]$HandSerial,   # 現在 hand（=client）役の Quest serial
    [string]$HostIp,       # host Quest の LAN IP（client の tdv_ip に入れる）。省略時は入力を求める
    [string]$Package = "com.roiril.tableduo",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Activity = "$Package/com.unity3d.player.UnityPlayerActivity"

function Get-QuestSerials {
    $lines = adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\tdevice$" }
    return $lines | ForEach-Object { ($_ -split "\t")[0] }
}

# --- serial 解決 ---
if (-not $FullSerial -or -not $HandSerial) {
    $serials = @(Get-QuestSerials)
    if ($serials.Count -ne 2) {
        Write-Error "adb で 2 台認識されていません（現在 $($serials.Count) 台: $($serials -join ', ')）。-FullSerial / -HandSerial を明示してください。"
    }
    Write-Host "接続中: $($serials -join ' / ')"
    $ans = Read-Host "現在 full（host）役はどちら？ [1] $($serials[0])  [2] $($serials[1])"
    if ($ans -eq "1") { $FullSerial = $serials[0]; $HandSerial = $serials[1] }
    elseif ($ans -eq "2") { $FullSerial = $serials[1]; $HandSerial = $serials[0] }
    else { Write-Error "1 か 2 を入力してください。" }
}

if (-not $HostIp) {
    # 交代後の host は「現在 hand 役」の端末。その wlan0 IP を自動取得してみる
    $ipLine = adb -s $HandSerial shell ip -f inet addr show wlan0 2>$null | Select-String "inet "
    if ($ipLine) {
        $HostIp = ($ipLine -split "\s+" | Where-Object { $_ -match "^\d+\.\d+\.\d+\.\d+/" }) -replace "/.*", ""
        Write-Host "交代後 host（$HandSerial）の IP を自動検出: $HostIp"
    }
    if (-not $HostIp) { $HostIp = Read-Host "交代後の host（$HandSerial）の LAN IP" }
}

# --- 交代プラン: full⇄hand を反転 ---
# 旧 full($FullSerial) → 新 hand/client、旧 hand($HandSerial) → 新 full/host
$plan = @(
    @{ Serial = $HandSerial; Desc = "新 host/full"; Args = "-e tdv_mode host -e tdv_role full" },
    @{ Serial = $FullSerial; Desc = "新 client/hand"; Args = "-e tdv_mode client -e tdv_ip $HostIp -e tdv_role hand" }
)

Write-Host ""
Write-Host "=== 役割交代プラン ==="
foreach ($p in $plan) { Write-Host "  $($p.Serial) → $($p.Desc)" }
Write-Host "======================"

foreach ($p in $plan) {
    $stop  = "adb -s $($p.Serial) shell am force-stop $Package"
    $start = "adb -s $($p.Serial) shell am start -n $Activity $($p.Args)"
    if ($DryRun) {
        Write-Host "[DryRun] $stop"
        Write-Host "[DryRun] $start"
        continue
    }
    Write-Host "[$($p.Serial)] 停止中..."
    Invoke-Expression $stop | Out-Null
    Start-Sleep -Seconds 1
    Write-Host "[$($p.Serial)] $($p.Desc) で起動中..."
    Invoke-Expression $start | Out-Null
}

if (-not $DryRun) {
    Write-Host ""
    Write-Host "完了。host（$HandSerial）が先に起動してから client が繋がるまで数秒待ってください。"
    Write-Host "接続確認: adb -s $FullSerial logcat -d -s Unity | Select-String TableDuo"
}
