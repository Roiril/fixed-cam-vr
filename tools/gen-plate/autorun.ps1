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
#   絵が入れ替わる（手形の走行に幕の顔が入った）。同じ異変の N 枚並列だと**同じ絵が 2 走行に入る**
#   （重複。4 枚中 2 枚が一致した）。そのため autorun は
#   - 鍵（logs/gen-plate/.autorun.lock）を持ち、別の autorun が走っていれば終わるまで待つ
#   - 終わりに out.png の md5 を突き合わせ、重複した枚を **1 枚ずつ**焼き直す（1 回まで）

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Anomaly,
    [Parameter(Mandatory = $true)][string]$Site,
    [string]$Place = 'left-half',
    [int]$Tries = 3,
    [double]$Scale = 0.5,
    [double]$Blur = 0.0,
    [double]$Lap = 0,
    [switch]$NoRefs,
    [switch]$NoRetry
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

function Wait-Lock {
    $waited = $false
    while (Test-Path -LiteralPath $lock) {
        $age = (Get-Date) - (Get-Item -LiteralPath $lock).LastWriteTime
        if ($age.TotalMinutes -gt 20) {
            "古い鍵（$([int]$age.TotalMinutes) 分前）を外す: $lock"
            Remove-Item -LiteralPath $lock -Force
            break
        }
        if (-not $waited) {
            "別の autorun が焼いている（$((Get-Content -LiteralPath $lock -Raw).Trim())）。終わるまで待つ — 別の異変を同時に焼くと絵が入れ替わる"
            $waited = $true
        }
        Start-Sleep -Seconds 10
    }
    Set-Content -LiteralPath $lock -Value "$PID $Anomaly $(Get-Date -Format s)" -Encoding UTF8
}

function Release-Lock { Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue }

function Get-RunnerArgs([string]$d) {
    @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner,
      '-PromptFile', (Join-Path $d 'prompt.txt'), '-Mode', 'image',
      '-Cwd', $d, '-OutDir', $d, '-OutImage', (Join-Path $d 'out.png'))
}

function Find-Dups([string[]]$ds) {
    $hashes = foreach ($d in $ds) {
        $p = Join-Path $d 'out.png'
        if (Test-Path -LiteralPath $p) {
            [pscustomobject]@{ dir = $d; hash = (Get-FileHash -LiteralPath $p -Algorithm MD5).Hash }
        }
    }
    $hashes | Group-Object hash | Where-Object { $_.Count -gt 1 }
}

# --- 並列で焼く（鍵の中で） ---
Wait-Lock
try {
    "焼く: $Tries 枚を並列（$Anomaly @ $Site / $Place）"
    $procs = foreach ($d in $dirs) {
        Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList (Get-RunnerArgs $d)
    }
    $procs | Wait-Process -Timeout 900
}
finally { Release-Lock }

# --- 重複（同じ絵が 2 走行に入る）は 1 枚ずつ焼き直す ---
$dups = Find-Dups $dirs
if ($dups) {
    ""
    "⚠ 同じ絵が複数の走行に入っている（並列の重複。README §9）:"
    foreach ($g in $dups) { "   " + (($g.Group | ForEach-Object { Split-Path -Leaf $_.dir }) -join ' = ') }
    if (-not $NoRetry) {
        $retry = @(foreach ($g in $dups) { $g.Group | Select-Object -Skip 1 | ForEach-Object { $_.dir } })
        "  → $($retry.Count) 枚を 1 枚ずつ焼き直す"
        Wait-Lock
        try {
            foreach ($d in $retry) {
                Remove-Item -LiteralPath (Join-Path $d 'out.png') -Force -ErrorAction SilentlyContinue
                & powershell.exe @(Get-RunnerArgs $d) | Out-Null
            }
        }
        finally { Release-Lock }
        $again = Find-Dups $dirs
        if ($again) {
            "⚠⚠ 焼き直しても重複が残った: " + (($again | ForEach-Object { ($_.Group | ForEach-Object { Split-Path -Leaf $_.dir }) -join ' = ' }) -join ' / ')
        }
        else { "  重複は解けた" }
    }
}

# --- 半分マスクの place なら、境目を跨いだ塊を後処理で消す（despill.py・0120） ---
$despilled = @()
if ($Place -match '^(left|right)-') {
    foreach ($d in $dirs) {
        if (-not (Test-Path (Join-Path $d 'out.png'))) { continue }
        & py -3.11 (Join-Path $PSScriptRoot 'spill.py') --run $d 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) {
            & py -3.11 (Join-Path $PSScriptRoot 'despill.py') --run $d 2>&1 | ForEach-Object { "  $_" }
            $dd = "${d}_despill"
            if ($LASTEXITCODE -eq 0 -and (Test-Path (Join-Path $dd 'out.png'))) { $despilled += $dd }
        }
    }
    if ($despilled.Count -gt 0) { "跨いだ塊を消した走行 $($despilled.Count) 本も判定に入れる（_despill）" }
}

# 判定して良い順に並べる（exit 0 = 合格 / 1 = 不合格）
$results = foreach ($d in ($dirs + $despilled)) {
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
