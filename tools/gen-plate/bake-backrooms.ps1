# 画面ごと差し替わる素材（`anomaly/backrooms-eyes.md`）を N 枚並列で焼く。
#
#   .\tools\gen-plate\bake-backrooms.ps1 -Plate tools\web-compositor\captures\plate_B_20260823_194235.jpg -Tries 4
#
# ⚠ **`compose.py` は使えない。** あれは「場所のプレートを暗くして種にし、その場所へ異変を足す」道具で、
#   ここは**場所ごと別世界に差し替える**（`canon/LEDGER.md` 0122）。プレートは**構図の参照だけ**に渡す。
# ⚠ 判定も `judge.py` は使えない（種との差で「足したもの」を測るので、全部が差になる）。
#   見るのは `deliver.py`（届いた画）と目。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Plate,   # 参照① 構図（そのカメラの当日のプレート）
    [string]$Mood = '',                             # 参照② 雰囲気（無ければ渡さない）
    [int]$Tries = 4,
    [string]$Anomaly = 'backrooms-eyes'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
$runner = Join-Path $env:USERPROFILE '.claude\scripts\codex-run.ps1'
$body = Join-Path $PSScriptRoot "anomaly\$Anomaly.md"

if (-not (Test-Path $body)) { throw "異変が無い: $body" }
$plateAbs = (Resolve-Path (Join-Path $repo $Plate)).Path

# frontmatter を落として本文だけ取る
$lines = Get-Content $body -Encoding UTF8
$cut = 0
if ($lines[0] -eq '---') {
    for ($i = 1; $i -lt $lines.Count; $i++) { if ($lines[$i] -eq '---') { $cut = $i + 1; break } }
}
$text = ($lines[$cut..($lines.Count - 1)] -join "`n").Trim()

$moodAbs = ''
if ($Mood) {
    $p = $Mood
    if (-not [System.IO.Path]::IsPathRooted($p)) { $p = Join-Path $repo $p }
    $moodAbs = (Resolve-Path $p).Path
}

$dirs = @()
for ($i = 1; $i -le $Tries; $i++) {
    $d = Join-Path $repo "logs\gen-plate\bk_${stamp}_$i"
    New-Item -ItemType Directory -Force $d | Out-Null
    Copy-Item $plateAbs (Join-Path $d 'ref.jpg') -Force
    $moodLine = ''
    if ($moodAbs) {
        # ⚠ 参照②は元のファイル名に日本語・空白が入ることがある（ユーザーのスクショ）。
        #   codex へ渡すパスは ASCII に寄せる。
        Copy-Item $moodAbs (Join-Path $d 'mood.png') -Force
        $moodLine = "`n- **参照画像②（雰囲気だけ合わせる相手。同じ絵にはしない）**: $(Join-Path $d 'mood.png')"
    }
    $prompt = @"
$text

## この画について（機械が書いた節）

- **参照画像①（構図だけ合わせる相手）**: $(Join-Path $d 'ref.jpg')$moodLine
- **寸法**: 640 x 480（この寸法のまま出す）
- 監視カメラの映像として見えること。カメラの色補正はこちらで掛けるので、**素の絵で出す**
"@
    Set-Content -Path (Join-Path $d 'prompt.txt') -Value $prompt -Encoding UTF8 -NoNewline
    $dirs += $d
}

"焼く: $Tries 枚を並列（$Anomaly / 参照 $(Split-Path -Leaf $plateAbs)）"
$procs = foreach ($d in $dirs) {
    Start-Process -FilePath 'powershell.exe' -PassThru -WindowStyle Hidden -ArgumentList @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $runner,
        '-PromptFile', (Join-Path $d 'prompt.txt'), '-Mode', 'image',
        '-Cwd', $d, '-OutDir', $d, '-OutImage', (Join-Path $d 'out.png'))
}
$procs | Wait-Process -Timeout 900

""
"焼けたもの"
foreach ($d in $dirs) {
    $p = Join-Path $d 'out.png'
    if (Test-Path $p) {
        $sz = & py -3.11 -c "from PIL import Image;im=Image.open(r'$p');print(f'{im.size[0]}x{im.size[1]}')"
        "  {0}  {1}" -f (Split-Path -Leaf $d), $sz
    } else {
        "  {0}  MISSING" -f (Split-Path -Leaf $d)
    }
}
""
"⚠ judge.py は使えない（種との差で測る道具）。見るのは delivered と目"
