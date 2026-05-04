# トラブルシューティング

fixed-cam-vr で詰まりがちな箇所と対処方法。

---

## Q. DroidCam / IP Webcam が繋がらない

**症状**: `MjpegScreen` が黒いまま、Console に `WebException` / `connection refused` / `timeout`。

**チェック順**:

1. **同一 LAN か**
   - PC（Unity）/ Quest 3 / 配信スマホがすべて同じ Wi-Fi（推奨 5GHz）に接続されているか
   - スマホがモバイル回線にフォールバックしていないか
2. **ポートと URL**
   - DroidCam: `http://<phone-ip>:4747/mjpegfeed?640x480`
   - IP Webcam (Android): `http://<phone-ip>:8080/video`
   - PC ブラウザで上記 URL を直接開いて映像が見えるか確認
3. **`CameraSource` の設定**
   - `Assets/Settings/Cameras/Phone01.asset` 等の `host` / `port` / `path` が分割入力されているか
   - Inspector の **Test Connection** ボタンで Console に成否が出る
4. **PC / スマホ側ファイアウォール**
   - Windows Defender Firewall でポート 4747 / 8080 がブロックされていないか
   - スマホ側アプリが「ローカルネットワーク」へのアクセスを許可されているか
5. **一括 ping**
   - **Tools > FixedCamVr > Diagnostics > Ping DroidCams** を実行 → Console に各 `CameraSource` の到達状況が出る
6. **再接続挙動**
   - `MjpegStreamReceiver` は最大 30 秒の exponential backoff で再接続する。スマホ側を起動し直したあと最長 30 秒待つ

---

## Q. HMD を被ると真っ黒（Game ビューには映る）

**チェック**:

1. **`OVRCameraRig` の有無**
   - シーン Hierarchy に `OVRCameraRig` プレハブが配置されているか
   - 自前 `Camera` を置いていないか（置いてあるなら削除）
2. **Quad の Material**
   - URP **Unlit** Material か（Lit だとライティングなしで暗転する）
   - Material の Shader が `Universal Render Pipeline/Unlit` であること
3. **Quad の向き**
   - HMD 起点（`OVRCameraRig` の CenterEyeAnchor）から 2m 前方、法線がカメラを向いているか
   - 裏面表示で消えていないか（Unlit は基本両面ではないので注意）
4. **Color Space**
   - Player Settings → Other Settings → Color Space が **Linear** になっているか
5. **Graphics API**
   - Auto Graphics API: OFF / **Vulkan** が最上位
6. **XR Plug-in**
   - Edit → Project Settings → XR Plug-in Management → Android タブで **Oculus** が有効か

---

## Q. コンパイルエラーが消えない

**手順**:

1. Unity MCP 経由なら `refresh_unity` → `read_console` でエラー全件取得
2. **asmdef 参照漏れ**
   - 新規スクリプトが既存 namespace を使う場合、その namespace の asmdef を `.asmdef` の `references` に追加
   - 例: `FixedCamVr.Tracking` から `FixedCamVr.Streaming` を使うには Tracking 側 asmdef に `"FixedCamVr.Streaming"` を追加
3. **Meta XR Project Setup Tool の Fix All**
   - Edit → Project Settings → Meta XR → 残課題があれば **Fix All / Apply All**
   - これで TextMeshPro Essentials など欠落アセットも修復される
4. **OVRInput が見つからない**
   - `OvrControllerBridge.cs` のように **asmdef を持たないファイル** からのみ OVRInput にアクセス可能。asmdef 配下から直接呼ぶと Meta XR SDK の参照解決が必要
5. **Library/ScriptAssemblies の破損**
   - 最終手段: Unity を閉じて `Library/ScriptAssemblies/` を削除 → 再起動で再コンパイル

---

## Q. フレームレートが 90Hz を維持できない

**確認**:

1. **`OVRManager.display.displayFrequency`** が 90 になっているか（Play 中に Inspector から確認、または `OVRManager` を持つ GameObject にデバッグスクリプトを当てる）
2. **Profiler 起動**
   - Window → Analysis → Profiler
   - 実機接続: Quest 3 を USB-C 接続 → Profiler の Editor ドロップダウンから **AndroidPlayer (Quest 3)** を選択
   - Wi-Fi 接続も可（adb で port-forward）
3. **`MjpegScreen.Update` のアロケ**
   - Profiler → CPU Usage → GC Alloc 行を確認
   - フレーム内アロケが出ていれば `byte[]` プールの使い回しが効いていない
   - `Texture2D.LoadImage(bytes, markNonReadable: false)` を使い、Texture2D は再生成しない（既存をそのまま渡す）
4. **OVR Metrics Tool**
   - 詳細手順は本ファイル下部「Q. FPS が 60 以下」の計測ツールセクションを参照
5. **基準値**
   - 90fps / GPU < 11ms / CPU Main < 11ms
   - 超えるなら Phase 3 の Compute Shader / Blit / RendererFeature のいずれかが重い候補

---

## Q. Quest 3 にビルドが入らない / 起動しない

**チェック**:

1. **Build Settings**
   - Platform: **Android**（Switch Platform 済みか）
   - Texture Compression: **ASTC**
2. **Player Settings → Other Settings**
   - Scripting Backend: **IL2CPP**
   - Target Architectures: **ARM64** のみ（ARMv7 は無効）
   - Minimum API Level: **Android 10 (API 29)** 以上
3. **Quest 3 側**
   - 開発者モード有効（Meta Quest アプリから）
   - USB デバッグ許可（Quest 内の確認ダイアログで「常に許可」）
   - PC との USB 接続が「ファイル転送」または「PTP」モード
4. **adb 確認**
   - `adb devices` で Quest 3 が `device` として表示されるか
   - `unauthorized` なら Quest 内で再度許可
5. **Build And Run でエラー**
   - Console を読む。署名・keystore エラーは Player Settings → Publishing Settings で keystore 再生成

---

## Q. Phase 3 の FxSandbox シーンが見つからない / 生成されない

`Assets/Scenes/FxSandbox.unity` は **Editor メニューから生成する** 仕組みになっている。

**手順**:

1. Unity Editor 上部メニュー: **Tools > FixedCamVr > Setup > Setup FxSandbox Scene**
   - これで `FxSandbox.unity` が生成され、テストパターン入力 + 4 系統の GameObject (`[Fx]_BlitChromatic` / `[Fx]_DustParticles` / `[Fx]_SobelCompute` 等) が配置される
2. **Tools > FixedCamVr > Setup > Create CRT Material**
   - `Assets/Art/Materials/Fx/FxCrtMaterial.mat` を生成
   - Phase 3-1 (CRT) の手動セットアップ手順が Console に出るので従う
3. 各 Phase の見比べ
   - Hierarchy で `[Fx]_*` を SetActive 切替
4. メニューが出ない場合
   - `Assets/Scripts/Fx/Editor/FxSandboxBuilder.cs` がコンパイル通っているか `read_console` で確認
   - asmdef が Editor 限定になっているか（`FixedCamVr.Fx.Editor.asmdef`）

---

## 明日の現場で詰まった時（Quest 3 実機セッション）

実機デモ当日に発生しがちな症状を、症状 → 疑う層 → 即試すこと の順で並べる。詳細は [docs/onsite-checklist.md](docs/onsite-checklist.md) と相互参照。

### Q. 起動後 HUD が出ない

**原因候補**:

- APK ビルド失敗 → `adb logcat -s Unity` で起動時例外
- RuntimeDebugHud の参照漏れ（Inspector で receiver/registry/tracker/hmd が null）
- HUD Canvas が頭に追従していない（OVRCameraRig の CenterEye 配下に置く）

**確認手順**:

1. `adb devices` で Quest が見えるか
2. APK 再ビルド（File > Build And Run）
3. Editor の Main シーンで HUD が出るか先に確認

### Q. HUD は出るが CONN が `○`（未接続）

接続レイヤーの問題。順に確認:

1. PC で `ping <スマホIP>` 通るか
2. Quest からも繋がるか（PC と Quest が同一 LAN / SSID か）
3. ルータの 5GHz 帯と 2.4GHz 帯で別 SSID なら統一
4. スマホの DroidCam / IP Webcam アプリが起動しているか（バックグラウンド落ちしてないか）
5. `Assets/Settings/Cameras/Phone01.asset` の host/port が実 IP と一致

### Q. CONN は `●` だが映像が出ない

描画レイヤーの問題:

- Quad の Material が **URP/Unlit** になっているか
- Quad の表裏が逆（HMD から見て裏面）→ rotation Y を 180 度
- URP の Color Space が Linear
- スマホ側解像度が異常（4K など）→ `Assets/Settings/Cameras/Phone01.asset` の path で `?640x480` を指定

### Q. FPS が 60 以下

#### 目標値
- FPS **90** / GPU < **11.1ms** / CPU Main < **11.1ms** / GC Alloc 0 byte / 90fps を割ると ASW が 45fps + 補完にフォールバック

#### 切り分け手順
1. Phase 3 FX を全 OFF にして再測（FX が原因か即判定）
2. **配信スマホ解像度を `?640x480` に固定**（メインスレッド JPEG decode のコスト削減）
3. RuntimeDebugHud の updateInterval を 0.5 秒以上に
4. **Editor Play は実機より遥かに重い**（Quest Link でも 5〜10fps に落ちることがある）→ 性能評価は APK ビルドで

#### 計測ツール

**OVR Metrics Tool**（実機オーバーレイ）:
```bash
adb install -r OVRMetricsTool.apk   # Meta 公式から入手
```
Quest 内 → ライブラリ → 提供元不明 → **OVR Metrics Tool** → **Persistent Overlay** ON で常時表示。CSV ログ出力可。

**Unity Profiler**（USB / Wi-Fi）:
- Build Settings で **Development Build** + **Autoconnect Profiler** にチェック
- Window → Analysis → Profiler、上部ドロップダウンを **AndroidPlayer (Quest 3)** に
- Wi-Fi 経由なら `adb tcpip 5555` → `adb connect <quest-ip>:5555`

#### よくあるボトルネック

| 場所 | 症状 | 対処 |
|---|---|---|
| `Texture2D.LoadImage` | CPU Main で 30〜100ms / frame | 配信解像度下げ・JPEG quality 下げ・将来 NativeArray + 別スレッド decode 検討 |
| `MjpegStreamReceiver.RunLoop` | 別スレッド受信 → メインに lock 取得待ち | バッファサイズが小さい時に頻発、`acc` 初期サイズ拡大 |
| GC Spike | Update / 描画パスで new / 文字列結合 | StringBuilder 化・ToString() 回避（HUD は対策済み）|
| URP Renderer Feature 過剰 | GPU > 11ms | Phase 3 の RendererFeature 数を絞る |
| Material 複製 | Renderer.material 呼び出しで都度コピー | OnEnable で 1 度だけ取得して保持（Fx は対策済み）|

### Q. ゾーン切替が暴れる / 反応しない

PlayerZone 設定:

- `halfExtents` が現場の体験空間サイズと合っているか（Inspector で Gizmo 表示）
- `centerOffset` の Y を HMD 高さ（≈1.6m）込みで考えているか
- `[Tracker]` の `zones` 配列に `[Zones]` の子全部が刺さっているか
- hysteresisShrink を一時的に 0 にして「単純に判定が外れてる」のか「ヒステリシスで動かない」のか切り分け

### Q. FX が出ない

URP RendererFeature の配線:

1. `Assets/Settings/URP-Balanced-Renderer.asset`（または現在使用中の `URP-*-Renderer.asset`）を Inspector で開く
2. Renderer Features に `FullScreenPassRendererFeature` が登録されているか
3. Material が `Assets/Art/Materials/Fx/FxCrtMaterial.mat` を指しているか
4. Camera の Renderer Index が一致しているか
5. Editor の FxSandbox で動くなら Renderer 構成の差分を疑う

### 切り分けフロー早見表

```
症状 → 疑う層 → 即試すこと
HUD 出ない         → ビルド層       → adb logcat で起動例外確認
CONN ○             → ネットワーク層 → ping → ルータ → 配信アプリ
映像出ない         → 描画層         → Material URP/Unlit / Quad 向き
FPS 低             → CPU/GPU 層     → FX OFF / Profiler / OVR Metrics
ゾーン誤判定       → Tracking 層    → halfExtents / Y オフセット
FX 出ない          → URP 層         → RendererData の Features 確認
```

詳細フローは [docs/onsite-checklist.md](docs/onsite-checklist.md) を参照。

---

## 実機検証で得た知見（2026-05-04 セッション、シュビー向け）

### Quest Link で Editor Play 時の制約

| 項目 | 仕様 / 観測 | 対処 |
|---|---|---|
| 表示周波数 | **72Hz 固定**（Quest 3 でも Link 接続時は 72 までしか出ない） | 90/120Hz 検証は実機 APK で |
| 実 FPS | Editor Play は実機より重く **5〜10fps** 程度に落ちることがある | 主要な性能評価は APK ビルドで |
| `Local Dimming feature is not supported` 警告 | Link 接続時の仕様 | 無視 |
| `InputFocusLost` ログ | PC 側で別ウィンドウにフォーカスが移ると出る | Game ビュークリックで回復 |

### 必要パッケージ

- `com.unity.xr.oculus`（Oculus XR Plugin）が **Standalone (Windows) 用に必須**。Quest Link 経由で Editor Play する場合これがないと HMD に映像が出ない
- インストール後、**XR Plug-in Management の Windows / Mac / Linux タブで Oculus にチェック**

### Project Setup Tool の残り項目

- **OVROverlayCanvas Recommended** は Apply ボタンが効かない（PlayerSettings ではなくシーン内 Canvas への手動 AddComponent が必要）。実機で HUD テキストがぼやけたら個別対応
- **Data Use Checkup / Application ID** は Platform SDK（Achievements / IAP 等）使うときだけ。**Mark as Fixed** で消して OK

### 体験中の挙動と原因

| 症状 | 原因 | 対処 |
|---|---|---|
| **HUD FPS が 7 fps 程度** | Editor Play の重さ + メインスレッド JPEG decode | 配信解像度を `?640x480` に固定 / 実機 APK で再測 |
| **配信スマホ電源 OFF→ON で映像が白いまま** | スマホ DHCP リース失効で **IP が変わる** → 古い URL に無限 backoff | `Tools > FixedCamVr > Diagnostics > Ping DroidCams` で IP 変動確認 → CameraSource.host 書き直し → Play 再起動 / 根治はルータで MAC 固定 IP |
| **Quest Boundary で数歩で画面消失** | ガーディアン未設定 or 範囲外 | Quest 設定 > 物理的空間 で Roomscale 拡張、または開発者モードで Boundary 無効 |
| **PingDroidCams が 1 回目失敗 / 2 回目成功** | 初回 ARP 解決 / TCP セッション確立に時間 | 既に対策済み（5s timeout + 1 回リトライ実装、`FixedCamVrMenu.cs`） |
| **配信未接続時にスクリーンが白くチカチカする** | `CameraStream` が `Texture2D(2,2)` を未初期化のまま `material.mainTexture` に貼っており、Quest GPU 上で未定義メモリが毎フレ違う色で見えていた | `CameraStream` コンストラクタで `SetPixels` + `Apply` で黒に初期化済み（[`CameraStream.cs:34`](Assets/Scripts/Streaming/CameraStream.cs:34)）。未接続中は黒画面で固定される |
| **歩いても PlayerZone が切り替わらない** | ゾーンが現場サイズに対し離れすぎ / `cameraIndex` が重複 / デッドゾーン（`hysteresisShrink` で縮めた A の外側 ↔ B の内側に届かない狭間） | 後述の `[HmdTrace]` ログで実機の `pos` と `*_in/inShrunk` を 1 Hz 採取してゾーン形状を逆算する |

### `[HudDump]` ログ

実機 / Editor Play どちらでも HUD 値を Console に時系列出力する `HudLogDumper` が `Assets/Scripts/Diagnostics/HudLogDumper.cs` にある。

- 状態変化（CONN / CAM index / ZONE label）時に 1 行 + 30 秒に 1 回定期 ping
- フォーマット: `[HudDump #N t=XX.X why=...] FPS=X.X CONN=0|1 CAM=N/N <name> ZONE=<label>@<pri> HMD=X.XX,Y.YY,Z.ZZ`
- MCP 経由抽出: `read_console filter_text="HudDump" count=100`
- 自動配置: `Tools > FixedCamVr > Setup > Setup Main Demo Scene` で DebugHud Canvas に Component 追加される

### `[HmdTrace]` ログ（ゾーン形状調査用）

「歩いてもゾーンが切り替わらない」など、**ゾーンの AABB を実測ベースで詰めたい時**の一時計測コンポーネント `HmdTrajectoryRecorder` が [`Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs`](Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs) にある。

- 1 Hz で `[HmdTrace #N t=X.X] pos=(x,y,z) zone=Label cam=N conn=0|1 Center=in/inShrunk Right=in/inShrunk Left=in/inShrunk` を出力
- `in` = 各 PlayerZone の AABB に含まれているか、`inShrunk` = `hysteresisShrink=0.15m` を効かせた縮小 AABB。**両方が 0 の区間 = どのゾーンにも属さずに `keepLastWhenOutside` で前ゾーンに張り付くデッドゾーン**
- MCP 経由抽出: `read_console filter_text="HmdTrace" count=200`
- 自動配置: `Setup Main Demo Scene` 実行で DebugHud Canvas に追加される。常時 ON 想定ではないので、調査が終わったら Component を削除して良い
- Quest Link で Editor Play 中に動かすのが基本。スタンドアロン APK で動かす場合は `adb logcat -s Unity *:I | findstr HmdTrace`

### 動作確認できたこと（5/4 セッション）

- ✓ Quest Link 経由で OVRCameraRig の HMD トラッキング
- ✓ MJPEG 受信・スクリーン Quad 描画
- ✓ コントローラ A/B/X/Y（カメラ切替・head-lock 切替・HUD トグル）
- ✓ HUD 各行の値表示
- ✓ `Tools > FixedCamVr > Setup > Setup Main Demo Scene` メニューでのワンクリック配置

### まだ試せていない / 残課題

- 実機 APK ビルドでの 90Hz 維持確認
- Phase 3（CRT / Dust）の URP RendererFeature 本配線後の見え方
- ゾーン自動切替（CenterEye の絶対位置と Zone AABB の整合）→ **`[HmdTrace]` ログで調査中**
- 体験空間の物理レイアウト調整（halfExtents の現場サイズ調整）→ HmdTrace 結果待ち
