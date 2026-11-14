$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'unity-log-filter.ps1')
$dummy = "-accessToken`ndummy-token`nerror CS1234`n--access-token=dummy-token`nupdated the access token dummy-token"
$safe = Protect-UnityLogText $dummy
if ($safe.Contains('dummy-token') -or -not $safe.Contains('error CS1234')) { throw 'mask changed diagnostics or retained token' }
$Root = Join-Path $env:USERPROFILE ('Projects/Unity/fixed-cam-vr/Logs/filter-test-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $Root 'Logs') -Force | Out-Null
$Cli = (Get-Command powershell.exe).Source
Invoke-SafeUnityCli -Arguments @('-NoProfile', '-Command', "Write-Output '-accessToken'; Write-Output 'dummy-token'; Write-Output 'diagnostic-kept'; exit 7")
if ($script:UnityCliExitCode -ne 7) { throw 'native exit code lost' }
$log = New-UnityInvocationLog 'test-mask'
[System.IO.File]::WriteAllText($log, $dummy)
Protect-UnityLogFile $log
if ([System.IO.File]::ReadAllText($log).Contains('dummy-token')) { throw 'saved token retained' }
if (@(Get-ChildItem (Join-Path $Root 'Logs') -Filter '*.previous').Count -ne 0) { throw 'unmasked backup created' }
$other = Join-Path $Root 'Editor.log'
[System.IO.File]::WriteAllText($other, 'other-editor-untouched')
Invoke-SafeUnityCli -LogPath $log -Arguments @('-NoProfile','-Command','exit 0')
if ($script:UnityCliExitCode -ne 0) { throw 'successful mask failed' }
if ([System.IO.File]::ReadAllText($other) -ne 'other-editor-untouched') { throw 'other editor changed' }
$locked = [System.IO.File]::Open($log, 'Open', 'ReadWrite', 'None')
try {
    Invoke-SafeUnityCli -LogPath $log -MaskRetries 1 -Arguments @('-NoProfile','-Command','exit 7')
    if ($script:UnityCliExitCode -ne 7 -or -not $script:UnityLogMaskFailed) { throw 'failed masking lost native failure' }
    Invoke-SafeUnityCli -LogPath $log -MaskRetries 1 -Arguments @('-NoProfile','-Command','exit 0')
    if ($script:UnityCliExitCode -eq 0 -or -not $script:UnityLogMaskFailed) { throw 'failed masking reported success' }
} finally { $locked.Dispose() }
# 書き込み共有を許す相手（Editor からハンドルを引き継いだ Unity.Licensing.Client の代役）が掴んだままでも、その場で伏せる
[System.IO.File]::WriteAllText($log, $dummy)
$held = [System.IO.File]::Open($log, 'Open', 'ReadWrite', 'ReadWrite')
try {
    $before = $held.Length
    Invoke-SafeUnityCli -LogPath $log -MaskRetries 1 -Arguments @('-NoProfile','-Command','exit 0')
    if ($script:UnityCliExitCode -ne 0 -or $script:UnityLogMaskFailed) { throw 'held log was not masked in place' }
    $held.Position = 0
    $reader = New-Object System.IO.StreamReader($held, [System.Text.Encoding]::UTF8, $false, 4096, $true)
    $text = $reader.ReadToEnd()
    if ($text.Contains('dummy-token') -or -not $text.Contains('error CS1234')) { throw 'in-place mask retained token or lost diagnostics' }
    if ($held.Length -ne $before) { throw 'in-place mask changed the length' }
} finally { $held.Dispose() }
Remove-Item -LiteralPath $Root -Recurse -Force
Write-Output 'mask checks passed; native exit=7 retained; no plaintext backup; other log unchanged; mask failures rejected; held log masked in place'
