---
name: table_duo_l0_desktop_test
description: TableDuo を実機ゼロ・MCP ゼロで検証する L0 Standalone desktop build + CLI 起動の手順と罠
metadata: 
  node_type: memory
  type: reference
  originSessionId: bc58b7b9-f00c-45c5-8be5-540c433c6bce
---

TableDuo（[[table_duo_study_status]]）を **Quest も Unity Editor も MCP も使わず**にマルチプレイ＋観戦まで検証できる仕組み。2026-06-30 構築。MCP ブリッジが重い使用で wedge する（手動再接続が要る）問題を恒久的に迂回するために作った。

## なぜ
- 描画/観戦の Editor 検証は元々「実機ゼロ」（host も Editor 内にできる）。真の壁は **MCP ブリッジが連続使用で wedge → 手動再接続**。
- Standalone Windows ビルドを CLI(`Bash`/`PowerShell`)で起動すれば Editor も MCP も不要。`-logFile` でログ、観戦は PNG 自動保存。全部スクリプトで回せる。

## L0 モードとは
`tdv_l0=on`（Editor は ConnectionManager.preplaceAvatars 隣の `enableL0InEditor`）で：OVRCameraRig OFF / DebugCamera ON / FakeHandDriver ON。HMD/XR 無しのフラット描画＋**合成 pose**。Windows standalone では OVRPlugin が graceful に init 失敗（`Unable to start Oculus XR Plugin`）するが L0 がリグを切るので flat で動く。実装は [ConnectionManager.ConfigureL0IfRequested](../../Assets/TableDuo/Scripts/Net/ConnectionManager.cs)。

## ビルド（batchmode・MCP 不要）
メニューは `Tools/FixedCamVr/Diagnostics/Build TableDuo Desktop (L0 test)`、batchmode は `-executeMethod FixedCamVr.EditorTools.BuildVariants.BuildTableDuoDesktop`。出力 `Builds/tableduo-desktop/TableDuo.exe`（コードは `TableDuo_Data/Managed/*.dll`、exe bootstrap の mtime は変わらないので **Data の DLL mtime で更新確認**）。

**罠（重要）**:
1. **Editor を閉じてから**（プロジェクトロック競合 `another Unity instance` で即 abort）
2. **初回 Standalone target 切替は巨大 reimport**（Meta XR SDK で 1000+ アセット）。**途中で Unity を kill しない**（中断すると未完で executeMethod 到達せず）。完走させる
3. **batchmode はドメインリロードで自プロセスを relaunch する** → `&`/`-Wait` では待ち切れない。**「Unity プロセスが全消滅するまで poll」**で待つ（`while(Get-Process Unity){sleep}`）
4. **.cs を変えた後は 2 回連続実行**：run1=再コンパイル（reload で executeMethod スキップ）→ run1 完全終了を待つ → run2=ビルド（再コンパイル無し→executeMethod 到達）。`[BuildVariants] DESKTOP OK` が出れば成功
5. 終わったら **target を Android へ戻す**（`-buildTarget Android -batchmode -quit`。Quest ワークフロー復帰。再度 desktop は再 switch で可）

## 起動（3プロセス・127.0.0.1）
各 `TableDuo.exe` に共通: `-tdvL0 on -screen-fullscreen 0 -screen-width W -screen-height H -logFile <uniq>`
- host: `-tdvMode host -tdvRole full -tdvPreplace on`
- 手役: `-tdvMode client -tdvRole hand -tdvIp 127.0.0.1`
- 観戦: `-tdvMode client -tdvRole spectator -tdvIp 127.0.0.1 -tdvPreplace on`

観戦は preplace 時、起動6秒後に **`%LocalLow%/DefaultCompany/TableDuo/spectator_shot.png`** を自動保存（[SpectatorController.CaptureAfter](../../Assets/TableDuo/Scripts/Net/SpectatorController.cs)）→ `Read` で framing 確認。ログの `[TableDuo] 診断: 席N ライブ接続→静的撤去` / `観戦カメラ起動 pos=...` で段階確認。

## 確認できること
先置き（描画）→ 接続で静的→ライブ差替（疎通）→ 合成 pose 追従（トラッキング）／ maxClients=3 で host+hand+観戦の3接続共存（kick 無し）／ 手 layout 同期／ 観戦カメラの2席中点 framing。全部 Quest・Editor・MCP 無しで。

## キービジュアル撮影モード（同じ基盤で）
`TableDuo.exe -tdvKeyVisual on` で [KeyVisualDirector](../../Assets/TableDuo/Scripts/Net/KeyVisualDirector.cs) が発動：ネットワーク/preplace せず、人役(Remy)＋手役を authored ポーズで置き、暖色キーライト＋シネマ 3/4 カメラで `persistentDataPath/keyvisual.png`（superSize 2）を保存して `Application.Quit`。接続 GUI は出さない（ConnectionManager の keyvisual 分岐で `showGui=false`）。成果物 docs/table-duo/keyvisual_tableduo.png。ポーズ/カメラ/ライトは KeyVisualDirector.cs で調整→1コマンド再撮影。

## ポーズの罠（キービジュアル/アバター撮影）
- **【2026-06-30 解消】RemyAvatarRig の腕 IK は堅牢化済み**（旧: 未校正で遠い/低い wrist target に届かず腕が上がっていた）。TwoBoneIK が `FromToRotation` で照準を強制するので**任意の wrist target に必ず届く**（座位ポーズも AimBone 化）。卓上を指す遠い reach も OK。検証は `Tools/FixedCamVr/Diagnostics/Preview Full Avatar (screenshot)` → `Temp/AvatarPreview/*.png`。詳細は [[table_duo_study_status]] の「IK しっかり化」。**手役（hand-only）は別系統**で、WristPos.y を卓の高さ(≈-0.42)まで下げないと宙に浮くのは従来どおり
- 画像を見ながらのイテレーションは「ポーズ.cs 編集→batchmode 2回ビルド→`TableDuo.exe -tdvKeyVisual on`→PNG を Read」。**起動 exe を Editor の `Unity.exe` と取り違えない**（standalone は `Builds/tableduo-desktop/TableDuo.exe`）
- **Editor を開いたまま MCP 経由でビルドできる**（2026-06-30）: `execute_menu_item("Tools/FixedCamVr/Diagnostics/Build TableDuo Desktop (L0 test)")`。コンパイルは先に `refresh_unity(compile=request)` で済ませる。MCP 応答は **build 同期実行で timeout するが build は進む**（`result=Unknown` ログや複数回実行が出ても**出力 DLL は正しく焼ける**）→ **`Builds/.../Managed/TableDuoVr.Net.dll` の mtime 更新＋安定で完了判定**して exe 実行。batchmode（Editor 閉じ・2回ビルド）より速い。ターゲットは BuildVariants の finally で Android に自動復元
- **手役（hand-only）の手首向き校正（2026-06-30）**: 手だけアバターは `wristRot` を bind に直接かける（補正なし）。`OVRCustomHandPrefab_R` の bind は **手のひら上・指=ローカル -X**。キービジュアルで「指=人/卓中央へ前向き・手のひら=下」にするのは **`Quaternion.AngleAxis(180f, new Vector3(1,0,1).normalized)`**（[KeyVisualDirector.HandPose](../../Assets/TableDuo/Scripts/Net/KeyVisualDirector.cs)）。identity だと手が外/上を向いて不自然。校正は `KeyVisualDirector` に一時的にカメラを手へロックする block を足して bind 向きを実画像で読んで確定した（確定後 block は撤去）
