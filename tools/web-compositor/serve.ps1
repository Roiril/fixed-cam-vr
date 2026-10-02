# web compositor をローカルで配信して既定ブラウザで開く。
# capture-server.py が POST /save を受けてキャプチャ/録画を captures/ に保存する（PC 内）。
# getUserMedia(Webcam) は localhost なら http でも動く。
param([int]$Port = 8099, [switch]$Prepare, [switch]$Inspect)

if ($Prepare -and $Inspect) {
  Write-Error '-Prepare と -Inspect は同時に指定できません。'
  exit 2
}

# 引数なしと後方互換の -Prepare は準備用。点検専用だけを明示する。
$env:FIXEDCAM_PREPARATION = if ($Inspect) { '0' } else { '1' }

$dir = $PSScriptRoot
$url = "http://localhost:$Port/index.html"
$api = "http://localhost:$Port/ops/capabilities"
$mode = if ($Inspect) { '点検専用' } else { '準備' }

# ⚠ Get-Command python では判定にならない。PATH の python は Microsoft Store のアプリ実行
# エイリアスで、存在はするのに呼んでも版すら返さない（＝ここを通り抜けて起動だけ黙って失敗する）。
# 実際に import が通るかどうかで見る。
$pyArgs = @("-3.11")
$ok = $false
try {
  & py @pyArgs -c "import sys" *> $null
  $ok = ($LASTEXITCODE -eq 0)
} catch {
  $ok = $false   # py ランチャ自体が無い
}

if (-not $ok) {
  Write-Error 'py -3.11 で Python 3.11 を呼べません。Python 3.11 が必要です。'
  exit 1
}

try {
  $existing = Invoke-RestMethod -Uri $api -TimeoutSec 2
  if ($existing.ok -eq $true) {
    Write-Host "ポート $Port では設営サーバーがすでに動いています。起動中のモードを変えずに開きます。"
    Start-Process $url
    exit 0
  }
} catch {
  try {
    $listener = [System.Net.Sockets.TcpClient]::new()
    $pending = $listener.ConnectAsync('127.0.0.1', $Port)
    if ($pending.Wait(300) -and $listener.Connected) {
      $listener.Dispose()
      Write-Error "ポート $Port は別のアプリが使用しています。使用中のアプリを停止するか、-Port で空いている番号を指定してください。"
      exit 1
    }
    $listener.Dispose()
  } catch {
  }
}

Write-Host "$mode 用サーバーを起動します: $url"
$arguments = @('-3.11', ('"{0}"' -f (Join-Path $dir 'capture-server.py')), [string]$Port)
$server = Start-Process -FilePath 'py' -ArgumentList $arguments -NoNewWindow -PassThru
$ready = $false
try {
  for ($attempt = 0; $attempt -lt 50; $attempt++) {
    if ($server.HasExited) {
      Write-Error "設営サーバーが API 応答前に停止しました。終了コード: $($server.ExitCode)。ポート競合と上のエラーを確認してください。"
      exit 1
    }
    try {
      $response = Invoke-RestMethod -Uri $api -TimeoutSec 1
      if ($response.ok -eq $true) {
        $ready = $true
        break
      }
    } catch {
    }
    Start-Sleep -Milliseconds 200
  }
  if (-not $ready) {
    Write-Error '設営サーバーは起動しましたが、10 秒以内に API が応答しませんでした。'
    exit 1
  }
  Start-Process $url
  Write-Host 'ブラウザを開きました。Ctrl+C を押すとサーバーを停止します。'
  Wait-Process -Id $server.Id
  if ($server.ExitCode -ne 0) {
    Write-Error "設営サーバーが終了しました。終了コード: $($server.ExitCode)"
    exit $server.ExitCode
  }
} finally {
  if ($server -and -not $server.HasExited) {
    Stop-Process -Id $server.Id
    $server.WaitForExit()
    Write-Host '設営サーバーを停止しました。'
  }
}
