# 同じ指示で N 枚を**並列に**焼いて、判定して、良い順に並べる。
#
#   .\tools\gen-plate\autorun.ps1 -Anomaly dolls-many -Site A_20260901 -Place left-half -Tries 3
#
# なぜ「回す」のが正解か: 同じプロンプトでも回ごとに落ち方が違う（部屋ごと描き直す /
# 寸法を上げる / スクリプトで作る）。**直すより回す方が速い**（1 枚 2〜4 分）。
# 落ち方が毎回同じときだけ、プロンプトを直しに行く（tools/gen-plate/README.md §8）。
#
# ⚠ 判定が緑でも `sheet.png` と `heads.png` を開くまでが 1 周。
#
# ⚠⚠ **別の異変を同時に焼かない**（2026-09-04 実測・README §9）。同時に走る Codex の exec の間で
#   絵が入れ替わる（手形の走行に幕の顔が入った）。同じ異変の N 枚並列は構わない（入れ替わっても同じ指示）。
#   そのため autorun は鍵（logs/gen-plate/.autorun.lock）を持ち、別の autorun が走っていれば終わるまで待つ。
#   終わりに out.png の md5 を突き合わせ、同じ絵が 2 走行に入っていたら警告する。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Anomaly,
    [Parameter(Mandatory = $true)][string]$Site,
    [string]$Place = 'left-half',
    [int]$Tries = 3,
    [double]$Scale = 0.5,
    [double]$Blur = 0.0,
    [double]$Lap = 0,
    [switch]$NoRefs
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runner = Join-Path $env:USERPROFILE '.claude\scripts\codex-run.ps1'
$lock = Join-Path $repo 'logs\gen-plate\.autorun.lock'

$dirs = @()
for ($i = 1; $i -le $Tries; $i++) {
    $d = Join-Path $repo "logs\gen-plate\auto_${stamp}_$i"
    $args = @('-3.11', (Join-Path $PSScriptRoot 'compose.py'),
              '--anomaly', $Anomaly, '--site', $Site, '--place', $Place,
              '--scale', $Scale, '--out-dir', $d)
    if ($Blur -gt 0) { $args += @('--blur', $Blur) }
    if ($Lap -gt 0)  { $args += @('--lap', $Lap) }
    if ($NoRefs) { $args += '--no-refs' }
    & py @args | Out-Null
    $dirs += $d
}

# --- 鍵: 別の autorun（別の異変）が Codex を回している間は待つ ---
$waited = $false
while (Test-Path -LiteralPath $lock) {
    $age = (Get-Date) - (Get-Item -LiteralPath $lock).LastWriteTime
    if ($age.TotalMinutes -gt 20) {
        "古い鍵（$([int]$age.TotalMinutes) 分前）を外す: $lock"
        Remove-Item -LiteralPath $lock -Force
        break
    }
    if (-not $waited) {
        "別の autorun が焼いている（$(Get-Content -LiteralPath $lock -Raw)）。終わるまで待つ — 別の異変を同時に焼くと絵が入れ替わる"
        $waited = $true
    }
    Start-Sleep -Seconds 10
}
Set-Content -LiteralPath $lock -Value "$PID $Anomaly $(Get-Date -Format s)" -Encoding UTF8

try {
    "焼く: $Tries 枚を並列（$Anomaly @ $Site / $Place）"
    $procs = foreach ($d in $dirs) {
        Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
            '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner,
            '-PromptFile', (Join-Path $d 'prompt.txt'), '-Mode', 'image',
            '-Cwd', $d, '-OutDir', $d, '-OutImage', (Join-Path $d 'out.png'))
    }
    $procs | Wait-Process -Timeout 900
}
finally {
    Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue
}

# --- 入れ替わりの印: 同じ絵が 2 走行に入っていないか ---
$hashes = foreach ($d in $dirs) {
    $p = Join-Path $d 'out.png'
    if (Test-Path -LiteralPath $p) {
        [pscustomobject]@{ dir = (Split-Path -Leaf $d); hash = (Get-FileHash -LiteralPath $p -Algorithm MD5).Hash }
    }
}
$dups = $hashes | Group-Object hash | Where-Object { $_.Count -gt 1 }
if ($dups) {
    ""
    "⚠⚠ 同じ絵が複数の走行に入っている（入れ替わりの印。README §9）:"
    foreach ($g in $dups) { "   " + (($g.Group | ForEach-Object { $_.dir }) -join ' = ') }
}

# 判定して良い順に並べる（exit 0 = 合格 / 1 = 不合格）
$results = foreach ($d in $dirs) {
    if (-not (Test-Path (Join-Path $d 'out.png'))) {
        [pscustomobject]@{ dir = $d; ng = 99; note = 'image=MISSING' }
        continue
    }
    $out = & py -3.11 (Join-Path $PSScriptRoot 'judge.py') --run $d 2>&1
    $ng = ($out | Select-String -Pattern '^\s*NG ').Count
    [pscustomobject]@{ dir = $d; ng = $ng; note = ($out | Select-String -Pattern '→ ').Line }
}

""
"結果（良い順）"
$results | Sort-Object ng | ForEach-Object {
    "  NG {0,2} : {1}" -f $_.ng, (Split-Path -Leaf $_.dir)
    "           {0}" -f ($_.note -replace '\s+', ' ')
}
$bestRow = ($results | Sort-Object ng | Select-Object -First 1)
$best = $bestRow.dir
""
"いちばん良かったのは $best"
"  絵を開く:  $best\sheet.png  /  $best\heads.png"
"  素材にする: py -3.11 tools/gen-tone.py undim $best\out.png tools\web-compositor\captures\<名前>.png --cam <id>"

if ($bestRow.ng -eq 0) { exit 0 } else { exit 1 }
