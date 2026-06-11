# TableDuo 調査ログ回収スクリプト
# 使い方: .\tools\pull-tdv-logs.ps1 -PairId pair01 [-Serial <adbシリアル>]
# 接続中の Quest から CSV（SessionLogger）とバイナリ録画（HandPoseRecorder）を ./logs/<日付_PairId>/ へ
param(
    [string]$PairId = "pair",
    [string]$Serial = ""
)

$pkg = "com.UnityTechnologies.com.unity.template.urpblank"
$remote = "/sdcard/Android/data/$pkg/files"
$stamp = Get-Date -Format "yyyyMMdd"
$dest = Join-Path $PSScriptRoot "..\logs\${stamp}_$PairId"
New-Item -ItemType Directory -Force $dest | Out-Null

$adbArgs = @()
if ($Serial -ne "") { $adbArgs += @("-s", $Serial) }

Write-Host "回収先: $dest"
& adb @adbArgs shell "ls $remote"
& adb @adbArgs pull "$remote" "$dest"
Write-Host "完了。tdv_session_*.csv（ロガー）/ tdv_handrec.bin（録画）を確認してください。"
