# 展示網へ入り直させる。**上流の無い網を Android が恒久無効化したとき**の復旧。
#
#   .\tools\wifi-rejoin.ps1                 # adb に出ている全機
#   .\tools\wifi-rejoin.ps1 -Serial <序数>  # 1 台だけ
#   .\tools\wifi-rejoin.ps1 -Verify         # 直したあと、再起動して本当に戻るかまで見る
#
# ⚠⚠ **なぜ手で打つ必要があるか。**
#   Android は上流の無い網を NETWORK_SELECTION_DISABLED_NO_INTERNET_PERMANENT で
#   自動接続の対象から外す。これを解けるのは「人が選んだ接続」だけで、
#   `cmd wifi enable-network` は root が要る（shell は拒否される。実測）。
#   残る唯一の口が `cmd wifi connect-network` で、これはパスワードを引数に取る。
#
# ⚠ Wi-Fi のパスワードはこの画面に表示されず、PowerShell の履歴にも残らない（Read-Host -AsSecureString）。
#   ⚠ ただし adb へ渡す一瞬だけプロセスの引数に載る。閉じた展示網のパスワードなので許容する。
#
# ⚠ この経路で繋ぐと設定が作り直されるので、**静的 IP が DHCP に戻ることがある**。
#   Quest の IP は何からも参照されていないので体験には影響しない
#   （adb connect 192.168.10.31 が当たらなくなるだけ）。
#
# 詳しい機序は .claude/memory/no_internet_autojoin_kill.md

param(
  [string]$Ssid = "kougaku-lab-exp-a",
  [string]$Serial = "",
  [switch]$Verify
)

$ErrorActionPreference = "Stop"

function Get-Targets {
  $out = & adb devices
  $list = @()
  foreach ($line in $out) {
    if ($line -match "^(\S+)\s+device$") { $list += $Matches[1] }
  }
  if ($Serial) { return @($Serial) }
  return $list
}

function Get-WifiState([string]$s) {
  $st = & adb -s $s shell "cmd wifi status" 2>$null
  $txt = ($st -join " ")
  $ip = if ($txt -match "IP: /(\d+\.\d+\.\d+\.\d+)") { $Matches[1] } else { $null }
  $ss = if ($txt -match 'connected to "([^"]+)"') { $Matches[1] } else { $null }
  return [PSCustomObject]@{ Ip = $ip; Ssid = $ss }
}

$targets = Get-Targets
if ($targets.Count -eq 0) {
  Write-Host "adb に 1 台も出ていません。USB を挿すか adb connect してください。" -ForegroundColor Yellow
  exit 1
}

Write-Host ""
Write-Host "対象 $($targets.Count) 台: $($targets -join ', ')"
Write-Host "ネットワーク: $Ssid"
Write-Host ""
Write-Host "Wi-Fi のパスワードを入力してください（画面には出ません・履歴にも残りません）" -ForegroundColor Cyan
$sec = Read-Host -AsSecureString "  $Ssid のパスワード"
$bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
try {
  $pass = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
} finally {
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
}
if (-not $pass) { Write-Host "空でした。中止します。" -ForegroundColor Yellow; exit 1 }

$ok = 0
foreach ($s in $targets) {
  Write-Host ""
  Write-Host "── $s ─────────────────────────"

  # 予防を先に入れる。⚠ これは「これから」にしか効かないので、解く前に入れておく順番が正しい。
  & adb -s $s shell "settings put global captive_portal_mode 0" | Out-Null
  & adb -s $s shell "settings put global captive_portal_detection_enabled 0" | Out-Null

  & adb -s $s shell "cmd wifi connect-network $Ssid wpa2 $pass" | Out-Null

  $state = $null
  for ($i = 1; $i -le 20; $i++) {
    Start-Sleep -Seconds 3
    $state = Get-WifiState $s
    if ($state.Ip) { break }
  }
  if ($state.Ip) {
    Write-Host ("  繋がりました  IP={0}  SSID={1}" -f $state.Ip, $state.Ssid) -ForegroundColor Green
    $ok++
  } else {
    Write-Host "  繋がりませんでした（パスワード違い / 電波 / 別の理由）" -ForegroundColor Red
    & adb -s $s shell "dumpsys wifi" 2>$null | Select-String "mNetworkSelectionDisableReason" | Select-Object -First 1
  }
}

$pass = $null
[GC]::Collect()

Write-Host ""
Write-Host "$ok/$($targets.Count) 台 繋がりました"

if ($Verify -and $ok -gt 0) {
  Write-Host ""
  Write-Host "── 確かめ: 再起動しても戻るか ──────────────" -ForegroundColor Cyan
  Write-Host "⚠ 予防が効いていなければ、ここでまた落ちます（そこまで見るのがこの確認の目的）"
  foreach ($s in $targets) {
    $before = Get-WifiState $s
    if (-not $before.Ip) { continue }
    Write-Host ""
    Write-Host "  $s を再起動します（前: $($before.Ip)）"
    & adb -s $s reboot | Out-Null
    Start-Sleep -Seconds 20
    $back = $null
    for ($i = 1; $i -le 40; $i++) {
      Start-Sleep -Seconds 6
      try { $back = Get-WifiState $s } catch { $back = $null }
      if ($back -and $back.Ip) { break }
    }
    if ($back -and $back.Ip) {
      Write-Host ("  ★ 戻りました  IP={0}" -f $back.Ip) -ForegroundColor Green
    } else {
      Write-Host "  ⚠ 戻りませんでした。予防が効いていません" -ForegroundColor Red
    }
  }
}

Write-Host ""
Write-Host "仕上げに:  py -3.11 tools\onsite.py wifi-guard"
