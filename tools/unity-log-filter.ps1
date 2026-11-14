# 伏せる値は名前付きの組 v。テキストの置換と、ファイルをその場で伏せる処理が同じ 3 つを使う。
$UnityTokenPatterns = @(
    '(?im)^(?:-accessToken|--access-token)[ \t]*\r?\n(?<v>[^\r\n]+)',
    '(?im)(?:-accessToken|--access-token)[ =\t]+(?<v>[^\s]+)',
    '(?im)updated the access token[ \t]+(?<v>[^\s]+)'
)

function Protect-UnityLogText([string]$Text) {
    $redact = [System.Text.RegularExpressions.MatchEvaluator]{
        param($m)
        return $m.Value.Substring(0, $m.Groups['v'].Index - $m.Index) + '[redacted]'
    }
    foreach ($pattern in $UnityTokenPatterns) { $Text = [regex]::Replace($Text, $pattern, $redact) }
    return $Text
}

# ファイルを掴んでいるプロセスの名前（Restart Manager）。調べられなければ $null。
function Get-UnityLogHolderNames([string]$Path) {
    if (-not ('FixedCamFileHolders' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class FixedCamFileHolders {
    [StructLayout(LayoutKind.Sequential)]
    struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType; public uint AppStatus; public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint session, int flags, string key);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint session);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps, IntPtr apps, uint nServices, string[] services);
    [DllImport("rstrtmgr.dll")]
    static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RM_PROCESS_INFO[] info, ref uint reasons);
    // 調べられなければ null。
    public static int[] Pids(string path) {
        uint session;
        if (RmStartSession(out session, 0, Guid.NewGuid().ToString()) != 0) return null;
        try {
            if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, null) != 0) return null;
            uint needed = 0, count = 0, reasons = 0;
            int rc = RmGetList(session, out needed, ref count, null, ref reasons);
            if (rc == 0) return new int[0];
            if (rc != 234) return null;  // ERROR_MORE_DATA 以外は失敗
            var info = new RM_PROCESS_INFO[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, info, ref reasons) != 0) return null;
            var pids = new List<int>();
            for (int i = 0; i < count; i++) pids.Add(info[i].Process.dwProcessId);
            return pids.ToArray();
        } finally { RmEndSession(session); }
    }
}
'@
    }
    $pids = [FixedCamFileHolders]::Pids($Path)
    if ($null -eq $pids) { return $null }
    return @($pids | ForEach-Object {
        $process = Get-Process -Id $_ -ErrorAction SilentlyContinue
        if ($process) { $process.ProcessName } else { '' }
    })
}

# 書き込み共有を許す相手が掴んだままのファイルを、その場で同じ長さの * に伏せる。
# 長さを変えないので、相手のハンドルの位置も壊さない。
function Protect-UnityLogFileInPlace([string]$Path) {
    $latin1 = [System.Text.Encoding]::GetEncoding(28591)  # 1 バイト = 1 文字。一致の位置がそのままバイトの位置になる
    $stream = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'ReadWrite,Delete')
    try {
        $bytes = New-Object byte[] $stream.Length
        $read = 0
        while ($read -lt $bytes.Length) {
            $n = $stream.Read($bytes, $read, $bytes.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
        $text = $latin1.GetString($bytes, 0, $read)
        foreach ($pattern in $UnityTokenPatterns) {
            foreach ($m in [regex]::Matches($text, $pattern)) {
                $value = $m.Groups['v']
                $stars = [byte[]](,42 * $value.Length)
                $stream.Position = $value.Index
                $stream.Write($stars, 0, $stars.Length)
            }
        }
        $stream.Flush($true)
    } finally { $stream.Dispose() }
}

function New-UnityInvocationLog([string]$Kind) {
    $directory = Join-Path $Root 'Logs'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    return Join-Path $directory ($Kind + '-' + [guid]::NewGuid().ToString('N') + '.log')
}

function Protect-UnityLogFile([string]$Path, [int]$Retries = 900) {
    $allowed = [System.IO.Path]::GetFullPath((Join-Path $Root 'Logs')).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $fullPath = [System.IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($allowed, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw '実行専用の Logs 配下だけを処理できます'
    }
    for ($attempt = 0; ; $attempt++) {
        try { $original = [System.IO.File]::ReadAllText($fullPath); break }
        catch {
            # .NET のメソッドの例外は MethodInvocationException に包まれて届く。
            # catch [System.IO.IOException] と書くと当たらず、再試行が 1 度も働かない（Unity がログを閉じる前に読むと即失敗）。
            $inner = $_.Exception
            while ($inner.InnerException) { $inner = $inner.InnerException }
            if (-not ($inner -is [System.IO.IOException])) { throw }
            # Unity.Licensing.Client は Editor からログのハンドルを引き継ぎ、Editor が終わっても放さない（2026-11-14）。
            # 待っても読めないので、Unity の本体がもう掴んでいなければその場で伏せる。
            $holders = Get-UnityLogHolderNames $fullPath
            if ($null -ne $holders -and $holders.Count -gt 0 -and -not ($holders -contains 'Unity')) {
                Protect-UnityLogFileInPlace $fullPath
                return
            }
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

function Invoke-SafeUnityCli([string[]]$Arguments, [string]$LogPath, [int]$MaskRetries = 900) {
    $script:HideUnityTokenLine = $false
    $script:UnityLogMaskFailed = $false
    $safeArguments = @($Arguments | Where-Object { -not [string]::IsNullOrEmpty($_) })
    # ⚠ PowerShell 5.1 は native の stderr を 2>&1 で ErrorRecord に包む。呼び出し元の
    #    $ErrorActionPreference = 'Stop' のままだと 1 行目で例外になり、終了コードもコンパイルエラーの
    #    拾い出しも通らずに落ちる（2026-10-15・CS1503 が「unity.exe : 」の 1 行にしか出なかった）。
    $ErrorActionPreference = 'Continue'
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
            Write-Warning ('実行ログの伏字を確認できません: ' + $LogPath + ' / ' + $_.Exception.GetType().Name + ': ' + $_.Exception.Message)
            if ($script:UnityCliExitCode -eq 0) { $script:UnityCliExitCode = 1 }
        }
    }
}
