# 開発用スマホ（motorola edge 20 fusion）を adb で操作する小道具。
#   .\phone.ps1 shot [name]      スクリーンショットを output/romi/<name>.png へ
#   .\phone.ps1 tap X Y
#   .\phone.ps1 text "..."       ASCII のみ
#   .\phone.ps1 ui               画面の要素（text / desc / bounds）を一覧
#   .\phone.ps1 wake             点灯してロック解除（PIN は環境変数 PHONE_PIN。無ければ解除しない）
param([string]$Cmd, [string]$A, [string]$B)
$s = if ($env:PHONE_SERIAL) { $env:PHONE_SERIAL } else { 'ZY22CWZHZM' }
$out = Join-Path $PSScriptRoot '..\..\output\romi'
switch ($Cmd) {
  'shot' {
    $n = if ($A) { $A } else { 'phone' }
    adb -s $s shell screencap -p /sdcard/_romi_shot.png
    adb -s $s pull /sdcard/_romi_shot.png (Join-Path $out "$n.png") | Out-Null
    Join-Path $out "$n.png"
  }
  'tap' { adb -s $s shell input tap $A $B }
  'text' { adb -s $s shell input text ($A -replace ' ', '%s') }
  'ui' {
    adb -s $s shell uiautomator dump /sdcard/_romi_ui.xml | Out-Null
    $x = [xml](adb -s $s shell cat /sdcard/_romi_ui.xml)
    $x.SelectNodes('//node') | Where-Object { $_.text -or $_.'content-desc' } |
      ForEach-Object { '{0,-40} {1,-28} {2}' -f $_.text, $_.'content-desc', $_.bounds }
  }
  'wake' {
    adb -s $s shell input keyevent KEYCODE_WAKEUP
    Start-Sleep 1
    adb -s $s shell input swipe 540 1800 540 600 300
    if ($env:PHONE_PIN) { Start-Sleep 1; adb -s $s shell input text $env:PHONE_PIN; adb -s $s shell input keyevent 66 }
  }
}
