param([string]$Name = 'real')
# 2-A の右の幕（LEDGER 0376）の生成を 4 枚順に Codex へ流す。入力の base2x.png と出力は Logs/gen-plate/stain2A_right_20261115/<Name>/
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$w = Join-Path $root "Logs\gen-plate\stain2A_right_20261115\$Name"
$p = $PSScriptRoot
$run = Join-Path $env:USERPROFILE '.claude\scripts\codex-run.ps1'
$ref = Join-Path $root 'tools\doll-ref\out\gen_standing.png'

function Step($prompt, $images, $out) {
    if (Test-Path $out) { Write-Output "skip $out"; return }
    Write-Output "start $out $(Get-Date -Format HH:mm:ss)"
    & $run -PromptFile (Join-Path $p $prompt) -Mode image -Images $images -OutImage $out -Cwd $w
    if (-not (Test-Path $out)) { throw "no output: $out" }
    Write-Output "done $out $(Get-Date -Format HH:mm:ss)"
}

Step 'prompt-right-stain.md' @("$w\base2x.png") "$w\stain.png"
Step 'prompt-right-bulge.md' @("$w\stain.png") "$w\bulge.png"
Step 'prompt-right-doll-laugh.md' @("$w\stain.png", $ref) "$w\doll_laugh.png"
Step 'prompt-right-doll-watch.md' @("$w\doll_laugh.png") "$w\doll_watch.png"
Write-Output "chain $Name finished"
