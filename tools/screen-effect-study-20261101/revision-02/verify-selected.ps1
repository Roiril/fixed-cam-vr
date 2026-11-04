$ErrorActionPreference = 'Stop'
$studyDeliveryRoot = 'G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study/revision-02'
$studyStill = Join-Path $studyDeliveryRoot 'still/pass-08'
$studyVerificationArgs = @(
    '-3.11', (Join-Path $PSScriptRoot 'verify.py'),
    '--output', $studyDeliveryRoot,
    '--still-image', (Join-Path $studyStill 'B-pass08.png'),
    '--still-view', (Join-Path $studyStill 'B-pass08-on-black.png'),
    '--base-image', (Join-Path (Split-Path $studyDeliveryRoot -Parent) 'still/round-11/base-B.png'),
    '--still-manifest', (Join-Path $studyStill 'manifest.json'),
    '--still-rgba-hash-key', 'RGBA_sha256',
    '--motion-dir', (Join-Path $studyDeliveryRoot 'motion/pass-05'),
    '--spatial-verification', (Join-Path $studyDeliveryRoot 'spatial/coverage/verification.json'),
    '--audit', (Join-Path $studyDeliveryRoot 'audits/static08.json'),
    '--manual-review-delivery'
)
& py @studyVerificationArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
