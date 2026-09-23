# web compositor をローカルで配信して既定ブラウザで開く。
# capture-server.py が POST /save を受けてキャプチャ/録画を captures/ に保存する（PC 内）。
# getUserMedia(Webcam) は localhost なら http でも動く。
param([int]$Port = 8099, [switch]$Prepare)

# Preparation permits local POV recording, collection and adoption only.
$env:FIXEDCAM_PREPARATION = if ($Prepare) { '1' } else { '0' }

$dir = $PSScriptRoot
$url = "http://localhost:$Port/index.html"
Write-Host "serving $dir at $url (captures -> $dir\captures)"

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

if ($ok) {
  Start-Process $url
  & py @pyArgs "$dir\capture-server.py" $Port
  return
}

Write-Host "py -3.11 で Python 3.11 を呼べません。Python 3.11 をインストールしてください（保存機能に必須）。" -ForegroundColor Yellow
