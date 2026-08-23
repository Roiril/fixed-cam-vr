# 笑い声の調整台をローカルで配信して既定ブラウザで開く。
# ⚠ 卓サーバ（tools/web-compositor/serve.ps1・8099）とは別物。show.json も実機も触らない。
param([int]$Port = 8130)

$dir = $PSScriptRoot
$url = "http://localhost:$Port/"
Write-Host "serving $dir at $url"

# ⚠ Get-Command python では判定にならない。PATH の python は Microsoft Store のアプリ実行
# エイリアスで、存在はするのに呼んでも版すら返さない（＝ここを通り抜けて起動だけ黙って失敗する）。
$ok = $false
try {
  & py -3.11 -c "import numpy" *> $null
  $ok = ($LASTEXITCODE -eq 0)
} catch {
  $ok = $false
}

if (-not $ok) {
  Write-Host "py -3.11 で numpy を読めません。tools/requirements.txt を入れてください。" -ForegroundColor Yellow
  return
}

Start-Process $url
& py -3.11 "$dir\server.py" $Port
