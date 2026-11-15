param([Parameter(Mandatory = $true)][string]$Work)
# 部屋の写真が替わったとき、2-A の生成 4 枚を前の版に揃えて 1 本ずつ Codex へ流す（README「作り直す」）。
# 先に作業フォルダへ置くもの:
#   idle2x.png  その日の待機（左のしみ入り）の 2 倍 / stain.png  それに右のしみを掛けた 2 倍
#   ref_left_bulge.png / ref_right_bulge.png / ref_doll_laugh.png / ref_doll_watch.png  前の版
#   p_<段>.md  refit_prompts.py が書くプロンプト
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$w = (Resolve-Path $Work).Path
$run = Join-Path $env:USERPROFILE '.claude\scripts\codex-run.ps1'
$ref = Join-Path $root 'tools\doll-ref\out\gen_standing.png'

function Step($prompt, $images, $out) {
    if (Test-Path $out) { Write-Output "skip $out"; return }
    foreach ($i in $images) { if (-not (Test-Path $i)) { throw "missing input: $i" } }
    Write-Output "start $out $(Get-Date -Format HH:mm:ss)"
    & $run -PromptFile (Join-Path $w $prompt) -Mode image -Images $images -OutImage $out -Cwd $w
    if (-not (Test-Path $out)) { throw "no output: $out" }
    Write-Output "done $out $(Get-Date -Format HH:mm:ss)"
}

Step 'p_left_bulge.md' @("$w\idle2x.png", "$w\ref_left_bulge.png") "$w\left_bulge.png"
Step 'p_right_bulge.md' @("$w\stain.png", "$w\ref_right_bulge.png") "$w\bulge.png"
Step 'p_doll_laugh.md' @("$w\stain.png", $ref, "$w\ref_doll_laugh.png") "$w\doll_laugh.png"
Step 'p_doll_watch.md' @("$w\doll_laugh.png", "$w\ref_doll_watch.png") "$w\doll_watch.png"
Write-Output "refit finished"
