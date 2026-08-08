# Unity CLI を呼ぶ唯一の入口。
#
# CLI は 1.0.0-beta.3 で、フラグの変わる余地がある。直接叩く場所をここ 1 つに集めておくと、
# CLI が変わったときに直すのはこのファイルだけで済む。
#
#   .\tools\unity.ps1 doctor                    # 前提が揃っているか（最初にこれ）
#   .\tools\unity.ps1 build fixedcam            # 廻リ視 APK
#   .\tools\unity.ps1 build fixedcam -Release   # 提出用（Development なし）
#   .\tools\unity.ps1 build tableduo-desktop    # 実機ゼロの L0 検証用 Standalone
#   .\tools\unity.ps1 test
#   .\tools\unity.ps1 menu                      # Editor の機能を CLI から呼ぶ（引数なしで一覧）
#   .\tools\unity.ps1 menu scene                # Setup Main Demo Scene
#   .\tools\unity.ps1 menu actor -Set actor=Ichimatsu
#   .\tools\unity.ps1 raw editors -i
#
# なぜ CLI なのか（旧経路との違い）:
#   旧: MCP の execute_menu_item → ほぼ確実に Timeout → APK の mtime を 240 回ポーリング。
#       さらに BuildVariant は「アクティブが Android でなければ切替だけして中断」するので、
#       GUI 経由だとメニューを 2 回実行する必要があった。
#   新: `unity build --target Android` が -buildTarget Android を張ってから -executeMethod を呼ぶので、
#       この中断ガード（Assets/Editor/BuildVariants.cs:128）を 1 回目で通過する。同期実行・戻り値あり。

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateSet('build', 'test', 'open', 'doctor', 'menu', 'raw')]
    [string]$Action,

    [Parameter(Position = 1)]
    [string]$App,

    # Development Build を外す（提出・配布用）
    [switch]$Release,

    # menu に値を渡す（`-Set actor=Ichimatsu -Set show=path\to\show.json`）。
    # -executeMethod は引数を取れないので、Unity のコマンドラインへ `-fcv key=value` として積む。
    # 受け側は Assets/Scripts/Streaming/Editor/EditorCliArgs.cs。
    [string[]]$Set,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Rest
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $PSScriptRoot
$Cli = Join-Path $env:LOCALAPPDATA 'Unity\bin\unity.exe'
$EditorVersion = '2022.3.62f2'

# App -> ビルド入口。Assets/Editor/BuildVariants.cs と 1:1 で対応する。
# ⚠ MyCobotHand だけ C# 側の命名が非対称（BuildMyCobotHand が Development なし、
#    BuildMyCobotHandDev が Development あり）。-Release の意味を揃えるため、ここで入れ替えている。
$Apps = @{
    'fixedcam'         = @{ Method = 'BuildFixedCam'; ReleaseMethod = 'BuildFixedCamRelease'
                            Target = 'Android'; Out = 'Builds/mawarimi.apk'; ReleaseOut = 'Builds/mawarimi-release.apk'
                            Desc = '廻リ視（本体）' }
    'tableduo'         = @{ Method = 'BuildTableDuo'; ReleaseMethod = 'BuildTableDuoRelease'
                            Target = 'Android'; Out = 'Builds/tableduo.apk'; ReleaseOut = 'Builds/tableduo-release.apk'
                            Desc = 'TableDuo（手アバター調査）' }
    'mycobothand'      = @{ Method = 'BuildMyCobotHandDev'; ReleaseMethod = 'BuildMyCobotHand'
                            Target = 'Android'; Out = 'Builds/mycobothand-dev.apk'; ReleaseOut = 'Builds/mycobothand.apk'
                            Desc = 'ロボットハンド操作VR' }
    'tableduo-desktop' = @{ Method = 'BuildTableDuoDesktop'; ReleaseMethod = $null
                            Target = 'StandaloneWindows64'; Out = 'Builds/tableduo-desktop/TableDuo.exe'; ReleaseOut = $null
                            Desc = 'TableDuo L0 検証用デスクトップ' }
}

$MethodPrefix = 'FixedCamVr.EditorTools.BuildVariants.'

# menu -> Editor の static メソッド（完全修飾）。`unity run -- -executeMethod <名前>` で呼ぶ。
#
# Out は「実行後に更新されているはずのもの」。**exit 0 でも中で LogError して何もしないことがある**
# ので、build と同じく出力で判定する（ログにしか出ないものは空にしてある）。
$Menus = [ordered]@{
    # ---- Setup（作る・焼く）----
    'scene'            = @{ Method = 'FixedCamVr.Streaming.EditorTools.MainDemoSceneSetup.Setup'
                            Desc = 'Main シーンへ演出・HUD・ゾーンを配置して保存（⚠ ビルド前に必須）'
                            Out = 'Assets/Scenes/Main.unity' }
    'hud-font'         = @{ Method = 'FixedCamVr.Streaming.EditorTools.JapaneseHudFontSetup.Generate'
                            Desc = 'HMD 内の日本語フォントを再ベイク（⚠ 文言を足したら必須。忘れると実機で豆腐）'
                            Out = 'Assets/Resources/Fonts/JapaneseHud SDF.asset' }
    'actor-prefab'     = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorPrefabBuilder.Build'
                            Desc = 'humanoid FBX から CG 人形プレハブを作る（-Set model=<fbx のパス>）'
                            Out = $null }
    'crt-material'     = @{ Method = 'FixedCamVr.Fx.Editor.FxRendererSetup.CreateCrtMaterial'
                            Desc = 'CRT ポスト FX のマテリアルを作る'
                            Out = 'Assets/Art/Materials/Fx/FxCrtMaterial.mat' }
    'fx-sandbox'       = @{ Method = 'FixedCamVr.Fx.Editor.FxSandboxBuilder.Setup'
                            Desc = 'FxSandbox シーンを作り直す'
                            Out = 'Assets/Scenes/FxSandbox.unity' }
    'tableduo-scene'   = @{ Method = 'TableDuoVr.EditorTools.TableDuoSceneSetup.Setup'
                            Desc = 'TableDuoMain シーンを作り直す'
                            Out = 'Assets/TableDuo/Scenes/TableDuoMain.unity' }

    # ---- Diagnostics 廻リ視（見る・測る）----
    'composite'        = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowCompositePreview.Run'
                            Desc = '実写プレート × CG 人形の合成を PNG 化（合成品質の一次証拠・-Set show=<パス>）'
                            Out = 'Assets/Screenshots/cgviz' }
    'actor'            = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorVizPreview.Run'
                            Desc = 'CG 人形を 4 ポーズ × 2 角度で PNG 化（-Set actor=<人形名>）'
                            Out = 'Assets/Screenshots/actorviz' }
    'actor-motion'     = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorMotionPreview.Run'
                            Desc = '体験者の動きを合成して腕の到達率・追従の遅れを測る（-Set actor=<人形名>）'
                            Out = 'Assets/Screenshots/actormotion' }
    'regviz'           = @{ Method = 'FixedCamVr.Tracking.EditorTools.RegistrationVizPreview.Run'
                            Desc = '位置合わせのワイヤーとゾーンのタイルを多角度で PNG 化'
                            Out = 'Assets/Screenshots/regviz' }
    'hud'              = @{ Method = 'FixedCamVr.Streaming.EditorTools.HudPreviewScreenshot.Capture'
                            Desc = 'HMD 内のステータス・操作パネルを PNG 化'
                            Out = 'Assets/Screenshots/hud-preview' }
    'ping-cams'        = @{ Method = 'FixedCamVr.Streaming.EditorTools.FixedCamVrMenu.PingCameras'
                            Desc = 'CameraSource の .asset が指す先へ HTTP して疎通を見る'
                            Out = $null }

    # ---- Diagnostics TableDuo ----
    'td-hands'         = @{ Method = 'TableDuoVr.EditorTools.TableDuoHandVariantPreview.Capture'
                            Desc = '手バリアント 3 種を実録画データで比較'
                            Out = 'Assets/Screenshots/tableduo/hands' }
    'td-avatar'        = @{ Method = 'TableDuoVr.EditorTools.TableDuoAvatarPreview.Capture'
                            Desc = 'フルボディアバターを PNG 化'
                            Out = 'Assets/Screenshots/tableduo/avatar' }
    'td-hand-role'     = @{ Method = 'TableDuoVr.EditorTools.TableDuoAvatarPreview.CaptureHandRoleInitial'
                            Desc = '手役の初期姿勢を PNG 化'
                            Out = 'Assets/Screenshots/tableduo/avatar' }
    'td-self-body'     = @{ Method = 'TableDuoVr.EditorTools.TableDuoAvatarPreview.CaptureSelfBody'
                            Desc = '一人称の自分の体を PNG 化'
                            Out = 'Assets/Screenshots/tableduo/avatar' }
    'td-table'         = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.Capture'
                            Desc = 'テーブルを PNG 化'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-table-remy'    = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.CaptureWithRemy'
                            Desc = 'テーブル + 着席した Remy'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-table-geister' = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.CaptureGeister'
                            Desc = 'テーブル（ガイスター）'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-table-algo'    = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.CaptureAlgo'
                            Desc = 'テーブル（アルゴ）'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-table-bandido' = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.CaptureBandido'
                            Desc = 'テーブル（バンディド）'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-table-bear'    = @{ Method = 'TableDuoVr.EditorTools.TableDuoTablePreview.CaptureSixStrokesBear'
                            Desc = 'テーブル（6 本の線でクマ）'
                            Out = 'Assets/Screenshots/tableduo/table' }
    'td-algo-physics'  = @{ Method = 'TableDuoVr.EditorTools.TableDuoAlgoPhysicsCheck.Run'
                            Desc = 'アルゴ山札が静止しているかを判定（ログのみ）'
                            Out = $null }
    'td-replay'        = @{ Method = 'TableDuoVr.EditorTools.TableDuoReplayDump.Dump'
                            Desc = '手の録画データをテキストへ書き出す（ログのみ）'
                            Out = $null }
}

# batchmode に持ち込めないもの。**画面が無いので原理的に動かない**ものだけをここへ置く
# （「まだ対応していない」ものは 1 つも無い）。
$GuiOnly = [ordered]@{
    'Open Main / Debug / PlayerZone Sandbox Scene' = 'シーンを開くだけ → `unity.ps1 open`'
    'Diagnostics/Run All Tests・Open Test Runner'  = 'テスト実行 → `unity.ps1 test`'
    'Diagnostics/Reveal Camera Sources Folder'     = 'エクスプローラを開くだけ'
    'Diagnostics/Preview Eye - Seat0 / Seat1'      = 'Scene ビューを席へ動かす。絵が要るなら td-avatar'
    'Layout/*（3 つ）'                              = 'Editor のウィンドウ配置'
}

function Assert-Cli {
    if (-not (Test-Path $Cli)) {
        throw "unity CLI が無い: $Cli （導入は ~/.claude/reference/unity-cli-ops.md §1）"
    }
}

# Editor を開いたまま batchmode を回すと衝突する（unity-cli-ops.md §6）
function Assert-NotLocked {
    $lock = Join-Path $Root 'Temp\UnityLockfile'
    if (Test-Path $lock) {
        Write-Host "⚠ Editor がこのプロジェクトを開いている（Temp/UnityLockfile）。batchmode と衝突する" -ForegroundColor Yellow
        Write-Host "  Editor を閉じてからやり直す" -ForegroundColor Yellow
        exit 1
    }
}

# 出力の「まとまり全体の最新時刻」。ファイルなら自身、フォルダなら再帰の最大。
# 無ければ MinValue（＝まだ 1 度も焼いていない）
function Get-OutputStamp([string]$path) {
    if (-not (Test-Path $path)) { return [datetime]::MinValue }
    $item = Get-Item $path
    if (-not $item.PSIsContainer) { return $item.LastWriteTime }
    $newest = Get-ChildItem $path -Recurse -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest) { return $newest.LastWriteTime }
    return [datetime]::MinValue
}

# ⚠ `unity run` は Unity 本体のログを標準出力へ出さない（~/.claude/reference/unity-cli-ops.md §7）。
#    `Debug.Log` も `error CS` も Editor.log にしか無い。**Unity は起動のたびにこれを上書きする**
#    （前回分は Editor-prev.log）ので、実行直後の中身 = いま走らせた 1 回分。
$EditorLog = Join-Path $env:LOCALAPPDATA 'Unity\Editor\Editor.log'

# ⚠ Unity 本体のタグを外さないと、終了時の [Performance] 統計（実測 359 行）だけで
#    末尾が埋まり、**肝心の [FixedCamVr] / [ActorViz] が 1 行も見えない**（2026-08-09 実測）。
$EditorLogNoise = '^\[(Performance|Subsystems|Licensing|Package Manager|XR|PhysX|MODES|UnityMemory|Physics)\b'

function Show-UnityLog {
    if (-not (Test-Path $EditorLog)) {
        Write-Host "  Editor.log が無い: $EditorLog" -ForegroundColor DarkGray
        return
    }
    # 行頭 [ = プロジェクトのタグ付きログ（[IntroVeil] 等）。あとはコンパイルエラーと例外。
    $hit = Select-String -Path $EditorLog -Pattern '^\[|error CS\d|Exception:|Unhandled' -Encoding utf8 |
        Where-Object { $_.Line -notmatch $EditorLogNoise } |
        Select-Object -Last 40
    if (-not $hit) { return }
    Write-Host "--- Editor.log ---" -ForegroundColor DarkGray
    foreach ($h in $hit) { Write-Host "  $($h.Line)" -ForegroundColor DarkGray }
}

function Show-Apps {
    Write-Host "使える App:" -ForegroundColor Cyan
    foreach ($k in $Apps.Keys | Sort-Object) { "  {0,-18} {1}" -f $k, $Apps[$k].Desc | Write-Host }
}

function Show-Menus {
    Write-Host "使える menu:" -ForegroundColor Cyan
    foreach ($k in $Menus.Keys) { "  {0,-18} {1}" -f $k, $Menus[$k].Desc | Write-Host }
    Write-Host ""
    Write-Host "  一覧に無いものは raw:<完全修飾名> で直接呼べる" -ForegroundColor DarkGray
    Write-Host "  例) .\tools\unity.ps1 menu raw:FixedCamVr.Foo.Bar.Baz" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "APK は build を使う:" -ForegroundColor Cyan
    foreach ($k in $Apps.Keys | Sort-Object) { "  .\tools\unity.ps1 build {0,-18} {1}" -f $k, $Apps[$k].Desc | Write-Host }
    Write-Host ""
    Write-Host "GUI 専用（batchmode に画面が無いので原理的に動かない）:" -ForegroundColor DarkGray
    foreach ($k in $GuiOnly.Keys) { "  {0,-45} {1}" -f $k, $GuiOnly[$k] | Write-Host -ForegroundColor DarkGray }
}

switch ($Action) {

    'doctor' {
        # ⚠ `unity editors -i` はこの機体で数分返ってこないことがある（2026-08-08 実測）。
        #    doctor が待たされると誰も打たなくなるので、**ディスクを直接見る**。
        #    知りたいのは「Android を焼けるか」で、それは実体のあるフォルダの有無で決まる。
        Assert-Cli
        $ng = 0

        Write-Host "── Unity CLI ──" -ForegroundColor Cyan
        Write-Host "  version: $(& $Cli --version 2>&1)"

        Write-Host "── Editor $EditorVersion ──" -ForegroundColor Cyan
        # CLI の既定の導入先。ここに無ければ `unity raw editors -i` で在り処を探す
        $edRoot = Join-Path $env:USERPROFILE "Unity\Editors\$EditorVersion\Editor"
        if (-not (Test-Path (Join-Path $edRoot 'Unity.exe'))) {
            Write-Host "  ✗ $edRoot に Unity.exe が無い" -ForegroundColor Red
            Write-Host "    unity install $EditorVersion / 在り処の確認は .\tools\unity.ps1 raw editors -i" -ForegroundColor Yellow
            $ng++
        }
        else {
            Write-Host "  ✓ $edRoot"
            # 2026-08-08 の実害: modules が WebGL だけで APK が 1 枚も焼けないのに気づいていなかった。
            # SDK / NDK / OpenJDK まで見る（AndroidPlayer だけあって中身が無いことがある）
            $ap = Join-Path $edRoot 'Data\PlaybackEngines\AndroidPlayer'
            $missing = @('SDK', 'NDK', 'OpenJDK') | Where-Object { -not (Test-Path (Join-Path $ap $_)) }
            if (-not (Test-Path $ap) -or $missing) {
                Write-Host "  ✗ Android モジュールが不完全 → APK は焼けない（欠け: $($missing -join ', ')）" -ForegroundColor Red
                Write-Host "    unity install-modules -e $EditorVersion -m android --cm --accept-eula -y" -ForegroundColor Yellow
                $ng++
            }
            else { Write-Host "  ✓ Android モジュール（SDK / NDK / OpenJDK まで揃っている）" }
        }

        Write-Host "── 周辺の道具 ──" -ForegroundColor Cyan
        # python は Store のスタブが PATH に居て、呼んでも落ちない（版すら返さない）。py -3.11 が本物
        $py = (& py -3.11 --version 2>&1 | Out-String).Trim()
        if ($py -match 'Python 3') { Write-Host "  ✓ py -3.11 : $py" }
        else { Write-Host "  ✗ py -3.11 が無い（tools/*.py が全部動かない）" -ForegroundColor Red; $ng++ }

        # adb は PATH に無くても Android モジュール同梱のものが使える（別途 platform-tools を入れない）
        $adb = Get-Command adb -ErrorAction SilentlyContinue
        if ($adb) { Write-Host "  ✓ adb : $($adb.Source)" }
        else {
            $bundled = Join-Path $edRoot 'Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe'
            if (Test-Path $bundled) {
                Write-Host "  △ adb は PATH に無いが、Android モジュール同梱のものがある" -ForegroundColor Yellow
                Write-Host "    $bundled"
                Write-Host "    PATH に足す（現セッションのみ）:" -ForegroundColor DarkGray
                Write-Host "    `$env:PATH += `";$(Split-Path $bundled)`"" -ForegroundColor DarkGray
            }
            else {
                Write-Host "  ✗ adb が無い（実機への install / logcat / 録画が全部できない）" -ForegroundColor Red
                $ng++
            }
        }

        Write-Host ""
        if ($ng -eq 0) { Write-Host "揃っている。" -ForegroundColor Green; exit 0 }
        Write-Host "$ng 件足りない。上の指示を先に済ませる。" -ForegroundColor Red
        exit 1
    }

    'build' {
        Assert-Cli
        if (-not $App -or -not $Apps.ContainsKey($App)) {
            if ($App) { Write-Host "知らない App: $App" -ForegroundColor Red }
            Show-Apps; exit 2
        }
        Assert-NotLocked

        $spec = $Apps[$App]
        $method = if ($Release) { $spec.ReleaseMethod } else { $spec.Method }
        $out = if ($Release) { $spec.ReleaseOut } else { $spec.Out }
        if (-not $method) { Write-Host "$App に -Release は無い" -ForegroundColor Red; exit 2 }

        $outPath = Join-Path $Root $out
        # ⚠ 単一ファイルの mtime で新旧を判定しない。
        #   Standalone の差分ビルドはランチャー stub（TableDuo.exe）を書き換えないので、
        #   exe を見ると「更新されていない」と誤判定する（2026-07-24 偽 TIMEOUT の実害）。
        #   出力の**まとまり全体の最新**で見る（APK は単一ファイルなので同じ値になる）。
        $outDir = if ($spec.Target -eq 'Android') { $outPath } else { Split-Path $outPath }
        $before = Get-OutputStamp $outDir

        Write-Host "$($spec.Desc) を焼く → $out" -ForegroundColor Cyan
        & $Cli build $Root --target $spec.Target --execute-method "$MethodPrefix$method" `
            --non-interactive @Rest
        $code = $LASTEXITCODE

        # ⚠ 出力の有無で判定する。exit 0 でも BuildVariant が中断していれば APK は増えない
        #   （C# 側は「アクティブが Android でない」「コンパイル中」で LogError して return する）
        $after = Get-OutputStamp $outDir
        if ($after -eq [datetime]::MinValue) {
            Write-Host "✗ 出力が無い: $out (exit=$code)" -ForegroundColor Red
            Write-Host "  Logs\build-*.log の末尾を見る" -ForegroundColor Yellow
            exit 1
        }
        if ($after -le $before) {
            Write-Host "✗ 出力が更新されていない（前回のまま）: $out" -ForegroundColor Red
            Write-Host "  Logs\build-*.log の [BuildVariants] の行を見る" -ForegroundColor Yellow
            Write-Host "  中断の典型: アクティブが Android でない / スクリプトコンパイル中" -ForegroundColor Yellow
            exit 1
        }
        if (Test-Path $outPath) {
            $f = Get-Item $outPath
            "✓ {0}  {1:N1} MB  {2:HH:mm:ss}" -f $out, ($f.Length / 1MB), $after | Write-Host -ForegroundColor Green
        }
        else { "✓ {0}  {1:HH:mm:ss}" -f $out, $after | Write-Host -ForegroundColor Green }
        if ($spec.Target -eq 'Android') {
            Write-Host "  install: adb install -r --no-streaming `"$outPath`"" -ForegroundColor DarkGray
        }
        exit $code
    }

    'test' {
        Assert-Cli
        Assert-NotLocked
        $mode = if ($App) { $App } else { 'EditMode' }
        $outXml = Join-Path $Root "Logs\test-$mode.xml"
        New-Item -ItemType Directory -Force (Split-Path $outXml) | Out-Null

        & $Cli test $Root --mode $mode --output $outXml --non-interactive @Rest
        $code = $LASTEXITCODE

        # exit code だけで判断しない。NUnit XML を読む
        if (Test-Path $outXml) {
            [xml]$xml = [System.IO.File]::ReadAllText($outXml, [Text.Encoding]::UTF8)
            $r = $xml.'test-run'
            $color = if ([int]$r.failed -gt 0) { 'Red' } else { 'Green' }
            Write-Host "$mode : 全 $($r.total) / 通過 $($r.passed) / 失敗 $($r.failed) / 除外 $($r.skipped)" -ForegroundColor $color
        }
        else { Write-Host "結果 XML が無い: $outXml" -ForegroundColor Yellow }
        exit $code
    }

    'menu' {
        Assert-Cli
        if (-not $App -or $App -in @('--list', '-l', 'list')) { Show-Menus; exit 0 }

        # ⚠ `unity run` は -batchmode / -quit / -logFile を自分で管理する。
        #    `--` の後ろにこれらを書くと CLI が exit 6 で弾く（2026-08-08 実測）。渡すのは -executeMethod だけ。
        if ($App.StartsWith('raw:')) {
            $method = $App.Substring(4)
            $desc = "(直接指定) $method"
            $out = $null
        }
        elseif ($Menus.Contains($App)) {
            $method = $Menus[$App].Method
            $desc = $Menus[$App].Desc
            $out = $Menus[$App].Out
        }
        else {
            Write-Host "知らない menu: $App" -ForegroundColor Red
            Show-Menus; exit 2
        }

        Assert-NotLocked

        # -Set key=value -> Unity のコマンドラインへ `-fcv key=value`（受け側は EditorCliArgs）
        $fcv = @()
        foreach ($kv in $Set) {
            if ($kv -notmatch '^[^=]+=.') {
                Write-Host "-Set は key=value の形で書く（受け取ったもの: $kv）" -ForegroundColor Red
                exit 2
            }
            $fcv += '-fcv'; $fcv += $kv
        }

        $outPath = if ($out) { Join-Path $Root $out } else { $null }
        $before = if ($outPath) { Get-OutputStamp $outPath } else { [datetime]::MinValue }

        Write-Host $desc -ForegroundColor Cyan
        Write-Host "  $method" -ForegroundColor DarkGray
        & $Cli run $Root --non-interactive -- -executeMethod $method @fcv @Rest
        $code = $LASTEXITCODE

        Show-UnityLog

        # ⚠ 出力で判定する。**Unity は Debug.LogError では終了コードを変えない**ので、
        #   中で「見つかりません」と言って何もせず返っても exit 0 になる。
        if ($outPath) {
            $after = Get-OutputStamp $outPath
            if ($after -le $before) {
                Write-Host "✗ 出力が更新されていない: $out (exit=$code)" -ForegroundColor Red
                Write-Host "  全文は $EditorLog" -ForegroundColor Yellow
                exit 1
            }
            "✓ {0}  {1:HH:mm:ss}" -f $out, $after | Write-Host -ForegroundColor Green
        }
        elseif ($code -eq 0) { Write-Host "✓ 実行した（出力は上の Editor.log を見る）" -ForegroundColor Green }
        exit $code
    }

    'open' {
        Assert-Cli
        & $Cli open $Root @Rest
        exit $LASTEXITCODE
    }

    'raw' {
        Assert-Cli
        $a = @()
        if ($App) { $a += $App }
        if ($Rest) { $a += $Rest }
        & $Cli @a
        exit $LASTEXITCODE
    }
}
