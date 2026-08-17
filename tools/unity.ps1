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

    # 焼き直し待ちのチェックを承知の上で飛ばす（git checkout 直後は mtime が揃うので誤検知しうる）
    [switch]$Force,

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
                            Out = 'Assets/Scenes/Main.unity'
                            Src = @('Assets/Scripts/Streaming/Editor/MainDemoSceneSetup.cs') }
    'hud-font'         = @{ Method = 'FixedCamVr.Streaming.EditorTools.JapaneseHudFontSetup.Generate'
                            Desc = 'HMD 内の日本語フォントを再ベイク（⚠ 文言を足したら必須。忘れると実機で豆腐）'
                            Out = 'Assets/Resources/Fonts/JapaneseHud SDF.asset'
                            # JapaneseHudFontSetup.CollectHudCharset() の収集元と 1:1。向こうを足したらここも足す
                            Src = @('Assets/Scripts/Tracking/RegistrationGuidance.cs',
                                    'Assets/Scripts/Tracking/CourseRegistrationController.cs',
                                    'Assets/Scripts/Diagnostics/StatusHud.cs',
                                    'Assets/Scripts/Diagnostics/ControllerGuidePanel.cs',
                                    'Assets/Scripts/Diagnostics/TitleNotice.cs',
                                    'Assets/Scripts/Diagnostics/RecoveryGuidance.cs',
                                    'Assets/Scripts/Diagnostics/CommsPanel.cs',
                                    'Assets/Scripts/Diagnostics/VisitorMarkGuidance.cs',
                                    'Assets/Scripts/Diagnostics/OutroReportText.cs',
                                    'Assets/Scripts/OvrBridge/OvrControllerBridge.cs',
                                    'Assets/Scripts/Streaming/CommsGlitchLogic.cs',
                                    'Assets/Scripts/Streaming/ShowRunDirector.cs') }
    'comms-preview'    = @{ Method = 'FixedCamVr.Streaming.EditorTools.CommsPreview.Run'
                            Desc = 'AIエージェントからの連絡の出方を 1 コマずつ焼く（-Set decay=0..1 で周回の壊れ／→ make-preview-video.py で mp4）'
                            Out = 'Assets/Screenshots/comms-preview/place.png' }
    'glitch'           = @{ Method = 'FixedCamVr.Streaming.EditorTools.GlitchPreview.Run'
                            Desc = '乱れが起きるたびにどれだけ大きくなるかを 1 コマずつ焼く（→ make-preview-video.py で mp4）'
                            Out = 'Assets/Screenshots/glitch-preview/f0000.png' }
    'text-audit'       = @{ Method = 'FixedCamVr.Streaming.EditorTools.HmdTextAudit.Run'
                            Desc = 'HMD 内の文字を全面まとめて測る（1 文字の見かけ角 / 枠からのはみ出し。⚠ 大きさ・文言を触ったら通す）'
                            Out = $null }
    'sound-import'     = @{ Method = 'FixedCamVr.Streaming.EditorTools.SoundImportSetup.ApplySoundImportSettings'
                            Desc = '音の取り込み設定を揃える（⚠ 音を焼き直したら必須。正規化・Streaming が混ざると設計した音量が消える）'
                            Out = $null
                            Src = @('Assets/Scripts/Streaming/Editor/SoundImportSetup.cs') }
    'actor-prefab'     = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorPrefabBuilder.Build'
                            Desc = 'humanoid FBX から CG 人形プレハブを作る'
                            Set = 'model=<fbx のパス>'
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
                            Desc = '実写プレート × CG 人形の合成を PNG 化（合成品質の一次証拠）'
                            Set = 'show=<show.json のパス> / decay=<0..1 周回で進む解像度の劣化>'
                            Out = 'Assets/Screenshots/cgviz' }
    'actor'            = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorVizPreview.Run'
                            Desc = 'CG 人形を 4 ポーズ × 2 角度で PNG 化'
                            Set = 'actor=<人形名>'
                            Out = 'Assets/Screenshots/actorviz' }
    'actor-motion'     = @{ Method = 'FixedCamVr.Streaming.EditorTools.ShowActorMotionPreview.Run'
                            Desc = '体験者の動きを合成して腕の到達率・追従の遅れを測る'
                            Set = 'actor=<人形名>'
                            Out = 'Assets/Screenshots/actormotion' }
    'sealedbox'        = @{ Method = 'FixedCamVr.Streaming.EditorTools.SealedBoxPreview.Run'
                            Desc = '【退避中】封印の箱の模様を 3 枚 PNG 化（体験には出ない）'
                            Set = 'hex=<六角の大きさ m> / marks=<印の割合>'
                            Out = 'Assets/Screenshots/sealedbox' }
    'title'            = @{ Method = 'FixedCamVr.Streaming.EditorTools.TitlePreview.Run'
                            Desc = 'タイトル画面「廻リ視」を 9 枚 + 閉じる演出 56 枚 PNG 化（両眼の厚み確認つき）'
                            Out = 'Assets/Screenshots/title' }
    'intro'            = @{ Method = 'FixedCamVr.Streaming.EditorTools.IntroPreview.Run'
                            Desc = '導入演出の段 0〜5（素通し→格下げ→輪郭→割れる→映像）を 16 枚 PNG 化'
                            Out = 'Assets/Screenshots/intro' }
    'shatter'          = @{ Method = 'FixedCamVr.Streaming.EditorTools.IntroShatterPreview.Run'
                            Desc = '【退避中】封印の箱の破片を 9 枚 + 連番 60 枚 PNG 化（覆いの割れは menu intro）'
                            Out = 'Assets/Screenshots/shatter' }
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

# ⚠ build の失敗理由は Editor.log ではなく `Logs\build-<Target>-<epoch>.log` に出る
#    （`unity build` が -logFile をそこへ張る）。**場所を案内するだけだと誰も開かない**ので、
#    落ちたその場で出す。menu 側の Show-UnityLog と対になる扱い。
function Show-BuildLog([string]$target, [datetime]$since) {
    $dir = Join-Path $Root 'Logs'
    $log = Get-ChildItem $dir -Filter "build-$target-*.log" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $log) {
        Write-Host "  build ログが無い: $dir\build-$target-*.log" -ForegroundColor DarkGray
        return
    }
    # ⚠ **前回のログを今回の失敗として見せない。** CLI が Unity を起こす前に落ちると
    #    新しいログが 1 つも増えず、末尾に前回の `[BuildVariants] OK:` が出て「成功したのに失敗」に見える。
    if ($log.LastWriteTime -lt $since) {
        Write-Host "  この実行のログが無い（Unity が起動する前に落ちている）。上の CLI の出力を読む" -ForegroundColor Yellow
        return
    }
    Write-Host "--- $($log.Name) ---" -ForegroundColor DarkGray
    # ⚠ ビルドログは Editor.log と違う形をしているので、パターンを共有しない（実測して 2 回外した）。
    #   - `^\[` だけだと Bee の進捗 `[1012/1019  0s] CopyFiles …` が全部当たって末尾が埋まる
    #     → **括弧の中が英字のものだけ**（`[BuildVariants]` `[IntroVeil]`）
    #   - `Exception` を裸で拾うと、末尾の容量内訳 `… .../NotListeningException.cs` が大量に当たる
    #     → **コロンと空白まで**（`NullReferenceException: Object reference …` は拾いたい）
    $hit = Select-String -Path $log.FullName -Encoding utf8 `
        -Pattern '^\[[A-Za-z]|error CS\d|Exception: |Unhandled [Ee]xception|BuildFailed|Error building Player|Build completed with a result' |
        Where-Object { $_.Line -notmatch $EditorLogNoise } |
        Select-Object -Last 30
    if ($hit) { foreach ($h in $hit) { Write-Host "  $($h.Line)" -ForegroundColor DarkGray } }
    else { Write-Host "  拾える行が無い。全文: $($log.FullName)" -ForegroundColor DarkGray }
}

# 焼き直していないものを、8 分かけて焼いてから気づかないようにする。
#
# 演出は**シーンに焼かれた GameObject**、HMD の日本語は**静的ベイクのアトラス**なので、
# .cs を直しただけでは APK に入らない。どちらも実害があり（2026-07-30 の演出 0 段・
# 2026-07-22 の全文字豆腐）、どちらも「ルールに書いてあるのに誰も見ていない」形で起きた。
# → 見る側を人からここへ移す。判定は $Menus の Src（入力）と Out（焼いたもの）の mtime 比較。
$BakeGuard = @('scene', 'hud-font')

function Get-StaleBakes {
    $stale = @()
    foreach ($k in $BakeGuard) {
        $m = $Menus[$k]
        if (-not $m -or -not $m.Src -or -not $m.Out) { continue }
        $baked = Get-OutputStamp (Join-Path $Root $m.Out)
        foreach ($s in $m.Src) {
            $p = Join-Path $Root $s
            if (-not (Test-Path $p)) { continue }
            if ((Get-Item $p).LastWriteTime -gt $baked) { $stale += $k; break }
        }
    }
    return $stale
}

function Show-Apps {
    Write-Host "使える App:" -ForegroundColor Cyan
    foreach ($k in $Apps.Keys | Sort-Object) { "  {0,-18} {1}" -f $k, $Apps[$k].Desc | Write-Host }
}

function Show-Menus {
    Write-Host "使える menu:" -ForegroundColor Cyan
    foreach ($k in $Menus.Keys) {
        "  {0,-18} {1}" -f $k, $Menus[$k].Desc | Write-Host
        if ($Menus[$k].Set) { "  {0,-18}   -Set {1}" -f '', $Menus[$k].Set | Write-Host -ForegroundColor DarkGray }
    }
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
        # ⚠ 在り処は PC によって違う。CLI で入れた機体は %USERPROFILE%\Unity\Editors\、
        #    Unity Hub で入れた機体は C:\Program Files\Unity\Hub\Editor\。**両方見る**
        #    （片方だけ見ていたので、Hub で入れた PC では doctor が必ず ✗ を出していた）。
        #    CLI 自身（`unity editors --installed`）はどちらも見つけるので、ビルドは通る。
        $edCandidates = @(
            (Join-Path $env:USERPROFILE "Unity\Editors\$EditorVersion\Editor"),
            (Join-Path ${env:ProgramFiles} "Unity\Hub\Editor\$EditorVersion\Editor")
        )
        $edRoot = $edCandidates | Where-Object { Test-Path (Join-Path $_ 'Unity.exe') } | Select-Object -First 1
        if (-not $edRoot) {
            $edRoot = $edCandidates[0]
            Write-Host "  ✗ Unity.exe がどこにも無い（見た場所: $($edCandidates -join ' / ')）" -ForegroundColor Red
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

        # 「揃っているのに build が落ちる」の最頻値がこれ。前提と並べて必ず見せる（§Assert-NotLocked）
        Write-Host "── いま誰か握っていないか ──" -ForegroundColor Cyan
        if (Test-Path (Join-Path $Root 'Temp\UnityLockfile')) {
            Write-Host "  ⚠ Temp\UnityLockfile あり = Editor か別の batchmode が開いている" -ForegroundColor Yellow
            Write-Host "    この状態では build / test / menu は即エラーで落ちる。閉じるか終わるのを待つ" -ForegroundColor Yellow
            Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction SilentlyContinue |
                ForEach-Object {
                    $cl = if ($_.CommandLine.Length -gt 140) { $_.CommandLine.Substring(0, 140) + '…' } else { $_.CommandLine }
                    Write-Host "    PID $($_.ProcessId)  $cl" -ForegroundColor DarkGray
                }
        }
        else { Write-Host "  ✓ 誰も握っていない" }

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

        if ($App -eq 'fixedcam' -and -not $Force) {
            $stale = Get-StaleBakes
            if ($stale) {
                Write-Host "✗ 焼き直していないものがある。このまま焼いても APK には入らない" -ForegroundColor Red
                foreach ($k in $stale) {
                    Write-Host "    .\tools\unity.ps1 menu $k" -ForegroundColor Yellow
                    Write-Host "      $($Menus[$k].Desc)" -ForegroundColor DarkGray
                }
                Write-Host "  古いまま焼くと決めたなら -Force（git checkout 直後は mtime が揃うので誤検知しうる）" -ForegroundColor DarkGray
                exit 3
            }
        }

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
        $startedAt = Get-Date
        & $Cli build $Root --target $spec.Target --execute-method "$MethodPrefix$method" `
            --non-interactive @Rest
        $code = $LASTEXITCODE

        # ⚠ 出力の有無で判定する。exit 0 でも BuildVariant が中断していれば APK は増えない
        #   （C# 側は「アクティブが Android でない」「コンパイル中」で LogError して return する）
        $after = Get-OutputStamp $outDir
        if ($after -eq [datetime]::MinValue) {
            Write-Host "✗ 出力が無い: $out (exit=$code)" -ForegroundColor Red
            Show-BuildLog $spec.Target $startedAt
            exit 1
        }
        if ($after -le $before) {
            Write-Host "✗ 出力が更新されていない（前回のまま）: $out" -ForegroundColor Red
            Write-Host "  中断の典型: アクティブが Android でない / スクリプトコンパイル中" -ForegroundColor Yellow
            Show-BuildLog $spec.Target $startedAt
            exit 1
        }
        if ($code -ne 0) { Show-BuildLog $spec.Target $startedAt }
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

        # ⚠⚠ コンパイルエラーがあると Unity は **古いアセンブリでテストを走らせる**（2026-08-17 実害）。
        #    XML は普通に結果を返すので、**直したはずの行がいつまでも失敗し続ける**（30 分溶かした）。
        #    CLI の標準出力に出るのは `Scripts have compiler errors.` の 1 行だけで、
        #    `error CS...` の本文は Editor.log にしか無い。
        #    ⚠ 過去の実行のエラーを拾わないよう、**走る前の長さ**を覚えて差分だけ読む。
        $editorLog = Join-Path $env:LOCALAPPDATA 'Unity\Editor\Editor.log'
        $logStart = 0
        if (Test-Path $editorLog) { $logStart = (Get-Item $editorLog).Length }

        & $Cli test $Root --mode $mode --output $outXml --non-interactive @Rest
        $code = $LASTEXITCODE

        $csErrors = @()
        if (Test-Path $editorLog) {
            try {
                $fs = [System.IO.File]::Open($editorLog, 'Open', 'Read', 'ReadWrite')
                try {
                    # ⚠⚠ **Unity は起動のたびに Editor.log を作り直す**（追記ではない）。
                    #    走行後のサイズが走行前より小さければローテートされたので、**全部が今回の分**。
                    #    2026-08-17 にここを取り違えて、検出を入れたのに 1 件も拾えなかった。
                    if ($fs.Length -lt $logStart) { $logStart = 0 }
                    if ($fs.Length -gt $logStart) {
                        $fs.Position = $logStart
                        $sr = New-Object System.IO.StreamReader($fs)
                        $csErrors = @([regex]::Matches($sr.ReadToEnd(), '(?m)^.*: error CS\d+:.*$') |
                                      ForEach-Object { $_.Value.Trim() } |
                                      Select-Object -Unique -First 8)
                    }
                }
                finally { $fs.Dispose() }
            }
            catch { }
        }

        # exit code だけで判断しない。NUnit XML を読む
        if (Test-Path $outXml) {
            [xml]$xml = [System.IO.File]::ReadAllText($outXml, [Text.Encoding]::UTF8)
            $r = $xml.'test-run'
            $color = if ([int]$r.failed -gt 0) { 'Red' } else { 'Green' }
            Write-Host "$mode : 全 $($r.total) / 通過 $($r.passed) / 失敗 $($r.failed) / 除外 $($r.skipped)" -ForegroundColor $color
        }
        else { Write-Host "結果 XML が無い: $outXml" -ForegroundColor Yellow }

        if ($csErrors.Count -gt 0) {
            Write-Host "✗ コンパイルエラー — **上の結果は古いアセンブリで走ったもの**" -ForegroundColor Red
            foreach ($e in $csErrors) { Write-Host "    $e" -ForegroundColor Red }
            exit 5
        }
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
