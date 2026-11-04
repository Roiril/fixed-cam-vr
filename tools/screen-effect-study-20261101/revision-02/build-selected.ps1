$ErrorActionPreference = 'Stop'
$study = 'G:/マイドライブ/shubie/reports/2026-11-01_screen-effect-study'
$selectedRoot = "$study/revision-02"
$selectedStill = "$selectedRoot/still/pass-08"
$selectedMotion = "$selectedRoot/motion/pass-05"
$selectedSpace = "$selectedRoot/spatial/coverage"
$reportArgs = @(
    "$PSScriptRoot/report.py",
    '--output', $selectedRoot,
    '--still-dir', $selectedStill,
    '--still-image', "$selectedStill/B-pass08.png",
    '--still-view', "$selectedStill/B-pass08-on-black.png",
    '--still-rgba-hash-key', 'RGBA_sha256',
    '--still-opaque-hash-key', 'opaque_actual_sha256',
    '--still-base', "$selectedStill/base-B-on-black.png",
    '--still-preview', "$selectedStill/preview.html",
    '--previous-still-preview', "$selectedRoot/still/pass-07/preview.html",
    '--still-native', "$selectedStill/comparison-nativecable.png",
    '--still-native', "$selectedStill/comparison-nativefloor.png",
    '--still-native', "$selectedStill/comparison-nativevertical.png",
    '--still-native', "$selectedStill/comparison-nativecentral.png",
    '--still-native', "$selectedStill/comparison-nativeboard.png",
    '--still-native', "$selectedStill/reference-white-window-edge-native.png",
    '--still-native', "$selectedStill/central-new-support.png",
    '--spatial-dir', "$selectedRoot/spatial",
    '--spatial-variant-dir', $selectedSpace,
    '--spatial-preview', "$selectedSpace/coverage-preview.html",
    '--spatial-verification', "$selectedSpace/verification.json",
    '--spatial-image', "$selectedSpace/global-8.5.png",
    '--spatial-image', "$selectedSpace/local-8.5.png",
    '--spatial-image', "$selectedSpace/combined-8.5.png",
    '--spatial-image', "$selectedSpace/none-8.5.png",
    '--spatial-image', "$selectedSpace/comparison-native-8.5.png",
    '--spatial-image', "$selectedSpace/comparison-native-10.5.png",
    '--reference-image', "$study/sources/Screenshot_20261101-004612.png",
    '--reference-video', "$study/reference-preview.mp4",
    '--new-video', "$selectedMotion/motion-revision.mp4",
    '--old-still', "$study/still/round-23/B-standard-on-black.png",
    '--old-video', "$study/motion/round-07/alternate-b/motion-prototype.mp4",
    '--comparison-manifest', "$selectedMotion/reference-comparison-manifest.json",
    '--feedback', "$PSScriptRoot/feedback.md",
    '--house-css', 'C:/Users/kouga/Projects/Web/claude-global/skills/visual-deliverable/assets/report.css',
    '--audit', "$selectedRoot/audits/static08.json"
)
py -3.11 @reportArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
node "$PSScriptRoot/check-report.mjs" $selectedRoot
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
