function Protect-UnityLogText([string]$Text) {
    $Text = [regex]::Replace($Text, '(?im)^(-accessToken|--access-token)[ \t]*\r?\n[^\r\n]+', '$1' + [Environment]::NewLine + '[redacted]')
    $Text = [regex]::Replace($Text, '(?im)((?:-accessToken|--access-token)[ =\t]+)[^\s]+', '$1[redacted]')
    return [regex]::Replace($Text, '(?im)(updated the access token[ \t]+)[^\s]+', '$1[redacted]')
}

function New-UnityInvocationLog([string]$Kind) {
    $directory = Join-Path $Root 'Logs'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    return Join-Path $directory ($Kind + '-' + [guid]::NewGuid().ToString('N') + '.log')
}

function Protect-UnityLogFile([string]$Path, [int]$Retries = 40) {
    $allowed = [System.IO.Path]::GetFullPath((Join-Path $Root 'Logs')).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '実行専用の Logs 配下だけを処理できます'
    }
    for ($attempt = 0; ; $attempt++) {
        try { $original = [System.IO.File]::ReadAllText($fullPath); break }
        catch [System.IO.IOException] {
            if ($attempt -ge $Retries - 1) { throw }
            Start-Sleep -Milliseconds 200
        }
    }
    $safe = Protect-UnityLogText $original
    if ($safe -ceq $original) { return }
    if (-not ('FixedCamUnityLogMove' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class FixedCamUnityLogMove {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileExW(string source, string target, uint flags);
}
'@
    }
    $tempPath = $fullPath + '.' + [guid]::NewGuid().ToString('N') + '.redacted'
    try {
        [System.IO.File]::WriteAllText($tempPath, $safe, [System.Text.UTF8Encoding]::new($false))
        for ($attempt = 0; ; $attempt++) {
            # 同じディレクトリで置換する。伏字前のバックアップは作らない。
            if ([FixedCamUnityLogMove]::MoveFileExW($tempPath, $fullPath, 9)) { break }
            if ($attempt -ge $Retries - 1) {
                throw [System.IO.IOException]::new('ログの置換に失敗しました')
            }
            Start-Sleep -Milliseconds 200
        }
    } finally {
        if ([System.IO.File]::Exists($tempPath)) { [System.IO.File]::Delete($tempPath) }
    }
}

function Invoke-SafeUnityCli([string[]]$Arguments, [string]$LogPath, [int]$MaskRetries = 40) {
    $script:HideUnityTokenLine = $false
    $script:UnityLogMaskFailed = $false
    $safeArguments = @($Arguments | Where-Object { -not [string]::IsNullOrEmpty($_) })
    & $Cli @safeArguments 2>&1 | ForEach-Object {
        $line = [string]$_
        if ($script:HideUnityTokenLine) {
            Write-Output '[redacted]'
            $script:HideUnityTokenLine = $false
        } else {
            Write-Output (Protect-UnityLogText $line)
            if ($line.Trim() -match '^(-accessToken|--access-token)$') {
                $script:HideUnityTokenLine = $true
            }
        }
    }
    $script:UnityCliExitCode = $LASTEXITCODE
    # 今回渡した専用ログだけを処理する。他 Editor の共有ログには触らない。
    if ($LogPath) {
        try { Protect-UnityLogFile -Path $LogPath -Retries $MaskRetries }
        catch {
            $script:UnityLogMaskFailed = $true
            Write-Warning ('実行ログの伏字を確認できません: ' + $LogPath)
            if ($script:UnityCliExitCode -eq 0) { $script:UnityCliExitCode = 1 }
        }
    }
}
