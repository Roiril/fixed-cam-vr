# web compositor をローカルで配信して既定ブラウザで開く。
# capture-server.py が POST /save を受けてキャプチャ/録画を captures/ に保存する（PC 内）。
# getUserMedia(Webcam) は localhost なら http でも動く。
param([int]$Port = 8099)

$dir = $PSScriptRoot
$url = "http://localhost:$Port/index.html"
Write-Host "serving $dir at $url (captures -> $dir\captures)"

$py = (Get-Command python -ErrorAction SilentlyContinue)
if ($py) {
  Start-Process $url
  python "$dir\capture-server.py" $Port
  return
}

Write-Host "python が見つかりません。python をインストールしてください（保存機能に必須）。" -ForegroundColor Yellow
