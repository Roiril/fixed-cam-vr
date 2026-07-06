# TableDuo PC ホスト運用スクリプト
# PC を NGO ホスト（観戦 spectator ロール・L0 デスクトップビルド）にして、
# 2 台の Quest を両方クライアント（full/hand）として接続する。
#   PC (host/spectator)  ← Quest A (client/full=人)  ← Quest B (client/hand=手)
# 利点: SessionLogger CSV / WireTap が PC に直接落ちる・両者の pose が必ずワイヤを通る・
#       役割交代がセッション継続のまま Quest 再起動だけで済む・障害耐性が上がる。
#
# 使い方:
#   .\tableduo-pc-host.ps1                       # 接続中 2 台を自動検出、full/hand を対話で割当
#   .\tableduo-pc-host.ps1 -FullSerial AAA -HandSerial BBB
#   .\tableduo-pc-host.ps1 -HostIp 192.168.11.20 # PC の LAN IP を明示（自動検出が外す時）
#   .\tableduo-pc-host.ps1 -HandVariant robot    # 調査ブロックの手バリアントを両 client へ渡す
#   .\tableduo-pc-host.ps1 -NoHost               # PC ホストは既に起動済み。Quest 2 台だけ繋ぎ直す
param(
    [string]$FullSerial,
    [string]$HandSerial,
    [string]$HostIp,
    [ValidateSet("", "default", "realistic", "robot")]
    [string]$HandVariant = "",
    [string]$Package = "com.roiril.tableduo",
    [string]$ExePath = "Builds/tableduo-desktop/TableDuo.exe",
    [switch]$NoHost,     # PC ホストを起動しない（既に立っている時）
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$Activity = "$Package/com.unity3d.player.UnityPlayerActivity"
$repo = Split-Path -Parent $PSScriptRoot

function Get-QuestSerials {
    $lines = adb devices | Select-Object -Skip 1 | Where-Object { $_ -match "\tdevice$" }
    return $lines | ForEach-Object { ($_ -split "\t")[0] }
}

# --- PC の LAN IP を解決（Quest の tdv_ip に入れる）---
if (-not $HostIp) {
    $HostIp = (Get-NetIPAddress -AddressFamily IPv4 |
        Where-Object { $_.IPAddress -like "192.168.*" -and $_.PrefixOrigin -ne "WellKnown" } |
        Select-Object -First 1).IPAddress
    if ($HostIp) { Write-Host "PC の LAN IP を自動検出: $HostIp" }
    else { $HostIp = Read-Host "PC の LAN IP（Quest から見える IP）" }
}

# --- serial 解決 ---
if (-not $FullSerial -or -not $HandSerial) {
    $serials = @(Get-QuestSerials)
    if ($serials.Count -ne 2) {
        Write-Error "adb で 2 台認識されていません（現在 $($serials.Count) 台: $($serials -join ', ')）。-FullSerial / -HandSerial を明示してください。"
    }
    Write-Host "接続中の Quest: $($serials -join ' / ')"
    $ans = Read-Host "人役（full）にするのはどちら？ [1] $($serials[0])  [2] $($serials[1])"
    if ($ans -eq "1") { $FullSerial = $serials[0]; $HandSerial = $serials[1] }
    elseif ($ans -eq "2") { $FullSerial = $serials[1]; $HandSerial = $serials[0] }
    else { Write-Error "1 か 2 を入力してください。" }
}

$variantArg = ""
if ($HandVariant) { $variantArg = " -e tdv_hand $HandVariant" }

# --- 1) PC ホスト（観戦 spectator + L0）を起動 ---
# L0 で OVRCameraRig を切る（PC に HMD 無し）。spectator ロールで俯瞰カメラ＋両者の pose をリレー・記録。
# WireTap は F9 でトグル、SessionLogger CSV は PC の persistentDataPath に落ちる。
if (-not $NoHost) {
    $exeFull = Join-Path $repo $ExePath
    if (-not (Test-Path $exeFull)) {
        Write-Error "デスクトップビルドが見つかりません: $exeFull`n先に Unity メニュー『Tools/FixedCamVr/Diagnostics/Build TableDuo Desktop (L0 test)』でビルドしてください。"
    }
    # 旧ホストの残骸掃除: プロセスが残っていると MarkServer(7780) が bind できず reset_board が死ぬ。
    # kill 後もゴースト socket が 7780 を掴み続けることがある（その場合は接続/記録に無影響・PC 再起動で解消）
    Get-Process TableDuo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $logDir = Join-Path $repo "Builds/tableduo-desktop/logs"
    New-Item -ItemType Directory -Force $logDir | Out-Null
    $logFile = Join-Path $logDir "host_$stamp.log"
    $hostArgs = @("-tdvMode", "host", "-tdvRole", "spectator", "-tdvL0", "on", "-logFile", $logFile)
    if ($HandVariant) { $hostArgs += @("-tdvHand", $HandVariant) }
    Write-Host ""
    Write-Host "=== PC ホスト起動 ===" -ForegroundColor Cyan
    Write-Host "  $exeFull $($hostArgs -join ' ')"
    Write-Host "  log → $logFile"
    if ($DryRun) { Write-Host "[DryRun] host 起動スキップ" }
    else {
        Start-Process -FilePath $exeFull -ArgumentList $hostArgs -WorkingDirectory $repo
        Start-Sleep -Seconds 6   # host が listen を開始するまで待つ
    }
}

# --- 2) Quest 2 台をクライアントで起動 ---
$plan = @(
    @{ Serial = $FullSerial; Desc = "client / full（人）"; Args = "-e tdv_mode client -e tdv_ip $HostIp -e tdv_role full$variantArg" },
    @{ Serial = $HandSerial; Desc = "client / hand（手）"; Args = "-e tdv_mode client -e tdv_ip $HostIp -e tdv_role hand$variantArg" }
)

Write-Host ""
Write-Host "=== Quest クライアント起動プラン（host=$HostIp）===" -ForegroundColor Cyan
foreach ($p in $plan) { Write-Host "  $($p.Serial) → $($p.Desc)" }

foreach ($p in $plan) {
    $stop  = "adb -s $($p.Serial) shell am force-stop $Package"
    # スリープ中（HMD が顔/マネキンから外れて近接センサー OFF）だと am start が黙って失敗する → 先に WAKEUP
    $wake  = "adb -s $($p.Serial) shell input keyevent KEYCODE_WAKEUP"
    # USB 接続中に出る Quest Link ダイアログがアプリ起動をブロックするので先に潰す
    $killDlg = "adb -s $($p.Serial) shell am force-stop com.oculus.systemux"
    $start = "adb -s $($p.Serial) shell am start -n $Activity $($p.Args)"
    if ($DryRun) {
        Write-Host "[DryRun] $stop"
        Write-Host "[DryRun] $wake"
        Write-Host "[DryRun] $killDlg"
        Write-Host "[DryRun] $start"
        continue
    }
    Invoke-Expression $stop | Out-Null
    Invoke-Expression $wake | Out-Null
    Invoke-Expression $killDlg | Out-Null
    Start-Sleep -Milliseconds 800
    Invoke-Expression $start | Out-Null
    Start-Sleep -Seconds 6
    $procId = adb -s $($p.Serial) shell pidof $Package
    if ($procId) { Write-Host "  起動 OK: $($p.Serial) → $($p.Desc) (pid $procId)" -ForegroundColor Green }
    else { Write-Warning "  起動確認できず: $($p.Serial)（スリープ/Link ダイアログ/未インストールを確認。HMD を被るか近接センサーを指で塞ぐと wake する）" }
}

Write-Host ""
Write-Host "完了。PC 画面に俯瞰の観戦ビューが出ます。" -ForegroundColor Green
Write-Host "  記録: PC 画面をアクティブにして F9 で WireTap 開始/停止（tdv_wiretap_*.csv）"
Write-Host "  ログ: SessionLogger CSV / WireTap は PC の %USERPROFILE%\AppData\LocalLow\<company>\TableDuo\ に出ます"
Write-Host "  盤面リセット: curl `"http://localhost:7780/mark?label=reset_board`""
