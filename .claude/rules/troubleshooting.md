---
name: troubleshooting
description: 「うまく動かない」時に層を切り分けるための診断フロー。Unity / 配信側 / ネットワーク / Meta XR / ビルド の責任境界マップ
paths:
  - "Assets/**"
  - "tools/**"
---

# 層別トラブルシュートガイド

fixed-cam-vr は **配信側 Android アプリ + ネットワーク + Unity Editor + Meta XR SDK + Quest 実機** が直列に並ぶ構成のため、症状から責任層を即断するのが難しい。**「層ごとに最小再現を持つ」** ことで責任境界を明確にする。

## 責任マップ

| 層 | 主な失敗例 | 最小再現 / 切り分けツール | 関連ドキュメント |
|---|---|---|---|
| **配信側 (Android/Kotlin)** | カメラ権限なし・MJPEG が出ない・解像度違い | ブラウザで `http://<phone>:8080/` 直接確認 / `curl /info` | [streaming.md](streaming.md), [streamer-android-build SKILL](../../.claude/skills/streamer-android-build/SKILL.md) |
| **ネットワーク** | 別 LAN・ポート閉鎖・Wi-Fi 帯域不足 | PC から `curl -m 3 /health` / `ping <phone>` | [streaming.md `/health`](streaming.md) |
| **Unity スクリプト** | デコード失敗・GC スパイク・Texture 白ノイズ | Editor Console + Profiler / [streaming-offline-test SKILL](../../.claude/skills/streaming-offline-test/SKILL.md) | [unity_pitfalls.md](../../.claude/memory/unity_pitfalls.md) |
| **Unity（コンパイル・Editor 機能）** | コンパイル失敗・prefab 再生時に値リセット | `.\tools\unity.ps1 test`（コンパイル込み）/ `.\tools\unity.ps1 menu` | [unity-vr.md](unity-vr.md), `unity-prefab-fields` スキル |
| **Meta XR / OpenXR** | パススルー出ない・トラッキング崩れ・90Hz 出ない | `adb-logcat` スキル (xr) / OVR Metrics Tool | [meta-xr.md](meta-xr.md) |
| **ビルド / 実機** | APK 起動しない・即落ち・黒画面 | `adb-logcat` スキル (unity) / `adb-logcat` スキル (crash) | [unity-vr.md](unity-vr.md) |

## 症状から逆引き

### 「映像が出ない」

順に確認：

1. **配信側を疑う** — ブラウザで `http://<phone>:8080/` を開く。HTML が出るか？ → 出ないなら streamer アプリ未起動 or ポート違い
2. **ネットワークを疑う** — PC から `curl -m 3 http://<phone>:8080/health`。タイムアウトなら同一 LAN にいない / FW ブロック
3. **Unity スクリプトを疑う** — `streaming-offline-test` で fake server に向け Editor 単体検証。ここで出るなら実機構成側の問題
4. **コンパイル** — `.\tools\unity.ps1 test` でエラー有無（走る前に必ずコンパイルする）
5. **Meta XR** — Editor で出るが Quest で出ない → ビルド層 (`adb-logcat` スキル (unity))

### 「カメラの IP が変わった / スロットの対応が崩れた」（2026-07-18 から自動化）

1. streamer v0.3.0+ は端末に cameraId（A/B/C・画面に巨大表示）を刻んであり、Web 卓が beacon で `cameras[].host` を自動追従、Quest もフレーム断時に ID で自動張り替えする。**基本は何もしなくて直る**
   - **配信中に streamer アプリで cameraId を切り替えた / 2 台で入れ替えた場合も自動反映される**（2026-07-23 修正。受信中 /info の id 継続照合・不一致 5s で張替。旧実装は映像が途切れない限り永遠に旧対応のまま表示し続けた）。入れ替え先の端末が存在しない片側だけの変更は張替先が無いので、現映像を出し続けつつ StatusHud にカメラ×が出る
2. 直らない時は Web 卓の「🩺 疎通診断」— PC→カメラ HTTP / beacon / Quest heartbeat の 3 経路が ✅/❌ で出る。**beacon ✅ なのに HTTP ❌ = AP のクライアントアイソレーション**（技術で救えない → 自前 AP へ）
3. 手動で固定したい時はカメラの host を手入力（自動で 📌 pin され、以後の自動追従・discovery を抑止）
4. 詳細契約は [streaming.md](streaming.md) の「接続の堅牢化」節

### 「映像はカクつく / 遅い」

1. `curl /health` で配信側の `fps` / `latestFrameAgeMs` 確認 → 配信側 stall 判別
2. `recv_fps / phone_fps` が 0.7 未満なら lag detect 発火（自動再接続）— [streaming.md](streaming.md) 参照
3. それでもダメなら Wi-Fi 帯域 / 解像度 / JPEG quality を [streaming.md 性能ガイドライン](streaming.md) で見直し

### 「Quest で起動しない / 黒画面」

1. `adb devices` で実機認識
2. `adb-logcat` スキル (crash) で native クラッシュ確認
3. `adb-logcat` スキル (unity) で `Unity` タグの初期化エラー
4. `adb-logcat` スキル (xr) で OVRPlugin / VrApi 初期化失敗

### 「体験が思ったとおりに起きない」（演出が出ない / 周回が進まない / 映像が不安定）

**推測で触る前にログの数値で確かめる。** HMD を被らずに導入 → 3 周 → 終了を通せる:

```bash
bash tools/run-quest-xp-test.sh walk 300
```

`[XP]` テレメトリ（[`ShowTelemetryHost`](../../Assets/Scripts/Diagnostics/ShowTelemetryHost.cs)）が
相・周回・区間・演出・録画・砂嵐・遅延・表示 fps を出し、
[`analyze-xp-log.py`](../../tools/analyze-xp-log.py) が show.json の著作と突き合わせて
**「出るはずで出なかった演出」を名指しする**。手順・収集の罠・ベースライン実測は
[.claude/memory/onsite_experience_test.md](../memory/onsite_experience_test.md)。

**⚠ 「演出が出なかった」を信じる前に 2 つ確かめる**（どちらも実測で踏んだ）:

1. **実機が使った設定**。レポート冒頭の「実機が使った設定」が FAIL なら、原因はコードではなく
   設定のずれ。端末キャッシュは焼き込みより優先されるので、APK を焼き直しても変わらない
   → `py -3.11 tools/quest-fleet.py reset-config <serial>`
2. **Wi-Fi の帯域**。2.4GHz だと受信 fps が配信の 2/3 まで落ち、導入の最後の段（Swap）は
   映像が届いていることを要求するので**帯域が細いだけで演出が出ない**
   → `py -3.11 tools/quest-fleet.py list` の wifi 列（run スクリプトが走行前に警告も出す）

Quest が複数繋がっているときの機の選択・APK と設定の同期・使わない機のスリープは
[`tools/quest-fleet.py`](../../tools/quest-fleet.py)（[.claude/memory/quest_fleet_two_devices.md](../memory/quest_fleet_two_devices.md)）。

**⚠ ログが OK でも画は壊れていることがある。** 2026-07-31 に、FAIL ゼロ・演出 7 本 OK と
判定された走行の画を録って見たら、**導入演出が 1 段も出ていなかった**（パススルーの初期化失敗 +
シェーダのビルド剥がれ）。当時のテレメトリは「段が進んだ」しか出しておらず、
「画に何か出た」を 1 つも観測していなかった。
→ **同日、「効果の実在」を出す 10 項目を足した**（`veil` / `veilBuilt` / `pt` / `wire` / `bg` /
`font` / `ovl` / `cg` / `bgm` / `ev=step`）。解析レポートの
**「## 効果の実在（画・音に出たか）」**節がこれを判定する。一覧と取得元は
[.claude/memory/onsite_experience_test.md](../memory/onsite_experience_test.md)。
それでも**立体視のスケール感・見た目の質**は計測に置き換えられないので、
見た目に関わる変更をしたら画を録って確かめる:

```bash
py -3.11 tools/quest-record.py --sec 45 --walk
```

`adb screenrecord` の出力（両眼・レンズ逆歪みの台形）から片眼を切り出して矩形へ戻す。
**パススルーも映る**。走行 1 回で画・ログ・判定・画の証拠がまとめて出るので、
**`logs/evidence/<日時>/index.md` の一覧を見て、気になる段はフル解像度の PNG を開く**
（縮小した一覧では細い線が消える）。

かつて「走行後は `grep -iE "見つかりません|Failed to|Error" <log>` を必ず通すこと」と書いていたが、
**手で通す前提だったので誰も通していなかった**（2026-07-31 の 2 件はどちらも実機ログに警告として
出ていたのに、`[XP]` だけ見ていて気づけなかった）。→ **判定に入れた**。レポートの
**「## 実機ログの警告」**節が数え、エラーがあれば FAIL・日本語の警告は別立てで名指しする。

### 「コンパイルが通らない」

```powershell
.\tools\unity.ps1 test
```

EditMode テストは走る前に必ずコンパイルするので、**これ 1 本でコンパイルエラーが分かる**
（`Logs/test-EditMode.xml` に結果）。Editor を開く必要も MCP もいらない。

⚠⚠ **コンパイルエラーがあると Unity は古いアセンブリでテストを走らせる**（2026-08-17 実害）。
XML は普通に「1369 中 1 件失敗」のような結果を返すので、**直したはずの行がいつまでも失敗し続ける**
（スタックトレースが現在のソースに無い行を指しているのが唯一の手掛かりだった。30 分溶かした）。

- **CLI の標準出力に出るのは `Scripts have compiler errors.` の 1 行だけ。**
  `error CS...` の本文は `%LOCALAPPDATA%\Unity\Editor\Editor.log` にしか無い
- ⇒ `unity.ps1 test` が**走行前後の Editor.log の差分**から `error CS` を拾って
  赤字で出し、**exit 5 で落とす**ようにした（2026-08-17）。
  「結果が出た＝コンパイルは通った」ではないので、この判定を外さないこと

⚠ **Editor がこのプロジェクトを開いていると止まる**（`Temp/UnityLockfile`）。閉じてからやり直す。
起動中の Editor をそのまま調べたいときだけ MCP（[reference/mcp-unity.md](../reference/mcp-unity.md)）へ落ちる。

### 「prefab に保存した値が再生時に変わる / 0 になる」

→ `unity-prefab-fields` スキルを読む。SerializeField 追加直後の prefab YAML 未反映パターン。

### 「Web で演出 ON にしても Quest にオーバーレイが出ない」（2026-06-17 実害）

1. **cue 未保存が最多**: 演出 ON は `cue_<camId>` を発火するだけ。合成素材+マスクを選び 💾 cue 保存していないと show.json に cue が無く、Quest は「unknown cue id」で何も出せない（Web 側は未保存なら警告を出すようにした）
2. **動画だけ出ない（画像は出る）**: ログに `E/NuCachedSource2: source returned error -1`。Android ネイティブ VideoPlayer が Python http.server(HTTP/1.0) からの HTTP ストリーミングを扱えない。→ Quest は UnityWebRequest でローカル DL してから `file://` 再生する（[`ScreenOverlayController.GetLocalVideoUrlAsync`](../../Assets/Scripts/Streaming/ScreenOverlayController.cs)）。`[ScreenOverlay] video cached` ログが出れば DL 成功。画像/マスクは UnityWebRequest なので直 URL で出る（=切り分けに使える）。
   動画 Prepare 失敗/タイムアウト時は自動で cue 中止＋live 復帰し、自動切替の恒久凍結は起きない（2026-07-23 監査修正・`OverlayPlaybackLogic`）。`[ScreenOverlay] cue aborted` ログで検知
3. ファイル名のスペース/括弧は URL を percent-encode（Web の cue 保存で対応済み）

### 「コントローラのボタンが意図と違う / 左右どちらも同じ操作になる」（2026-06-17 実害）

- **`OVRInput.GetDown(Button.One/Two)` はコントローラ未指定だと両手から拾う**。`Button.One`=A(右)**または**X(左)、`Button.Two`=B(右)**または**Y(左)。左手を明示しないと左の X/Y までカメラ操作に化ける
- → 用途ごとに `OVRInput.Controller.RTouch` / `LTouch` を明示する。**2026-07-20〜 廻リ視は右手 4 入力のみ**（[`OvrControllerBridge`](../../Assets/Scripts/OvrBridge/OvrControllerBridge.cs)：A=Next / B=ステータストグル / 右グリップ長押し=ランリセット / 右トリガー長押し=登録。左手・スティックは読まない）
- **トグル系ボタンの「初回押下が空振り」（2026-06-18 実害）**: ローカルに `bool _visible=true` 等で持つ状態が**対象側の初期状態とズレる**と、初回押下が「既にその状態」へのトグルになり何も起きない。→ **トグルは真実源（対象の `IsVisible`）を毎回読んで反転する**（`OvrControllerBridge.ToggleStatus()` が `StatusHud.IsVisible` を反転。`HudToggleInput`（Editor H）も同様）

## ⚠ まとめて直して「直った」は、どれが効いたかを教えてくれない（2026-07-31 実害）

実機ビルドは 1 回 5〜10 分かかるので、**複数の修正をまとめて入れて 1 回で試したくなる**。
それ自体は正しい（ビルドが最も高い資源）。危ないのはその後で、**「直った」を確認した時点で
「入れた変更はすべて必要だった」と記録してしまう**こと。

実例: 「パススルーが実機で一切出ない」に対して 3 つ同時に入れて直った。

1. `OculusProjectConfig._insightPassthroughSupport` を 0 → 1 ← **これが真因**
2. `IntroVeil` シェーダを Always Included に追加 ← **これも要る**
3. OVRCameraRig の全カメラ背景を不透明 → alpha 0 ← **不要。しかも有害だった**

3 は**別の演出（段 4 の「枠の外が黒く閉じる」）を原理的に殺していた**（乗算ブレンドは alpha を
戻せない）。「実機で確認済み」としてルールにも書かれ、次のセッションが疑わない状態になっていた。

→ **まとめて入れてよい。ただし「全部要ると確認した」とは書かない。**
- 変更ごとに**なぜ要るのかの機序**を書く。書けないものは切り分け候補として残す
- 「実機で確認した」と書くときは**何が確認できたか**を具体的に書く（今回なら「パススルーが出た」
  であって「3 つとも必要だった」ではない）
- 副作用の見落としは、**直した機能の隣を見る**と見つかる（パススルーを直したなら、
  パススルーを使う他の段も全部見る）

## 鉄則

- **Unity の中で全部見ようとしない** — 配信側は配信側のツール（ブラウザ / curl）で先に潰す
- **実機ビルドは最後** — Editor で再現できないことだけを実機に持ち込む
- **fake server で Unity 単体検証** — [streaming-offline-test](../../.claude/skills/streaming-offline-test/SKILL.md) が用意してある
- **ログは層タグで切る** — `[CameraStream]` `[MJPEG]` (Unity) / `FixedCamStreamer` (Android) / `OVRPlugin` (XR) — タグでフィルタすれば層がすぐ分かる
- **論理の判定と画の判定は別物** — `[XP]` が FAIL ゼロでも画が壊れていることがある（[visual-verification.md](visual-verification.md) §6・§7）

## 対応スキル

| スキル | 用途 |
|---|---|
| `quest-build` | APK を焼いて Quest に入れる（**Unity の入口はここ**）。ビルド以外のメニューは `.\tools\unity.ps1 menu` |
| `adb-logcat` | 実機 Quest / Pixel のログ取得（`unity` / `xr` / `streamer` / `crash` フィルタ） |
| `quest-capture` | **実機の「見ている絵」を動画で取り出す**（HMD 不要・パススルーも映る）。ログが OK でも画が壊れている時 |
| `streaming-offline-test` | スマホ無しで Unity の MJPEG パイプライン検証 |
| `streamer-android-build` | 姉妹リポ APK ビルド & 実機インストール |
| `unity-prefab-fields` | prefab YAML / SerializeField 不整合の修正 |
| `unity-editor-status`（グローバル） | **退避路**。起動中の Editor をライブ操作する必要があるときだけ（要 MCP 登録） |
