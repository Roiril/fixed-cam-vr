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
   - **Tools > FixedCamVr > Ping DroidCams** を実行 → Console に各 `CameraSource` の到達状況が出る
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
   - 詳細手順は [docs/performance.md](docs/performance.md) 参照
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

1. Unity Editor 上部メニュー: **FixedCamVr > Fx > Setup FxSandbox Scene**
   - これで `FxSandbox.unity` が生成され、テストパターン入力 + 4 系統の GameObject (`[Fx]_BlitChromatic` / `[Fx]_DustParticles` / `[Fx]_SobelCompute` 等) が配置される
2. **FixedCamVr > Fx > Create CRT Material**
   - `Assets/Art/Materials/Fx/FxCrtMaterial.mat` を生成
   - Phase 3-1 (CRT) の手動セットアップ手順が Console に出るので従う
3. 各 Phase の見比べ
   - Hierarchy で `[Fx]_*` を SetActive 切替
4. メニューが出ない場合
   - `Assets/Scripts/Fx/Editor/FxSandboxBuilder.cs` がコンパイル通っているか `read_console` で確認
   - asmdef が Editor 限定になっているか（`FixedCamVr.Fx.Editor.asmdef`）
