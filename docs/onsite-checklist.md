# 実機現場チェックリスト（onsite-checklist）

2026-05-04 想定。Quest 3 実機で Phase 0〜3 を通しで動作確認するための現場手順書。
事前準備から起動後の状態確認、Phase 別動作確認、切り分けフローまでを 1 枚にまとめる。

> 関連: [README.md](../README.md) / [TROUBLESHOOTING.md](../TROUBLESHOOTING.md) / [docs/performance.md](performance.md)

---

## 1. 持ち物・機材

- [ ] Quest 3 本体（充電済み）+ USB-C ケーブル（PC 接続用）
- [ ] 配信スマホ 3〜4 台（DroidCam または IP Webcam インストール済み・全台フル充電）
- [ ] ノート PC（Unity 2022.3.62f2 LTS + Android Build Support、adb 通る状態）
- [ ] Wi-Fi ルータ（5GHz、可能なら別 SSID で切り出し。来場者端末と分ける）
- [ ] 体験空間（4 畳半以上推奨。PlayerZone 3 つ＝中央／右／左を AABB で配置できる広さ）
- [ ] 養生テープ（ゾーン境界を床に貼って体験者の目印にする）

> 現状コミット済みの `Assets/Settings/Cameras/` には `Phone01.asset` / `Phone02.asset` の 2 件のみ。
> 3 台目以降を使う場合は **Create → FixedCamVr → Camera Source** で `Phone03.asset` 等を増やしてから出かける。

---

## 2. 事前セットアップ（PC 側、現場到着後 5 分）

1. [ ] スマホ全台で配信アプリ起動 → 各 IP をメモ
   - DroidCam: `http://<phone-ip>:4747/mjpegfeed?640x480`
   - IP Webcam: `http://<phone-ip>:8080/video`
2. [ ] PC ブラウザで上記 URL を直接開いて映像が見える事を全台で確認
3. [ ] Unity Editor で `Assets/Settings/Cameras/Phone01.asset` 〜 を開いて `host` を実 IP に書き換え
   - Inspector の **Test Connection** ボタンで疎通確認できる
4. [ ] **Tools > FixedCamVr > Ping DroidCams** で全 `CameraSource` 一括到達確認
5. [ ] Build Settings が `Assets/Scenes/Main.unity` のみ enabled であることを確認
6. [ ] Player Settings 確認（[TROUBLESHOOTING.md Q.ビルド入らない](../TROUBLESHOOTING.md) も参照）
   - Scripting Backend: IL2CPP / Target Architectures: ARM64 のみ / Min API Level: 29+
7. [ ] Quest 3 を USB-C 接続 → `adb devices` で `device` 表示を確認
8. [ ] **File > Build Settings > Build And Run**

---

## 3. 起動後 60 秒チェック（HMD を被って）

> 現状ランタイム HUD（オーバーレイ表示）は未実装。
> 接続状態 / アクティブカメラ番号 / フレームレートは Editor 接続で確認する：
> - **Game ビュー**: 右上の Stats でフレームレート / DrawCalls
> - **Hierarchy → `[Streaming]` 選択 → Inspector の Runtime State 行**: ● Connected / ○ Disconnected
> - **OVR Metrics Tool**（Quest 内）: GPU/CPU レベル・実フレームレート

最低限の主観チェック:

- [ ] スクリーン Quad に映像が出ている（黒くない / 色が落ちていない）
- [ ] 頭を左右に振って、Quad がワールド固定（または head-lock）に追従する
- [ ] 30 秒程度立ったまま見て、明らかなフリーズ・カクつき・大きな黒挿入が無い

> HUD が必要なら別タスクで `RuntimeDebugHud`（仮）を起こす。本日は Editor + OVR Metrics Tool で代替。

---

## 4. Phase 別動作確認

### Phase 2（基本表示）

- [ ] スクリーン正面に立つ → Phone01 の映像が出る
- [ ] スマホをカメラ側に向けて手を振る → 1 秒以内に反映
- [ ] 配信スマホ電源 OFF → 30 秒以内に再接続ログ → 復旧（exponential backoff、最大 30 秒）

### Phase 2.5（手動切替）

[`OvrControllerBridge.cs`](../Assets/Scripts/OvrBridge/OvrControllerBridge.cs) のマッピングに準拠：

- [ ] 右コントローラ **A**（`Button.One`）→ 次のカメラに切替
- [ ] 右コントローラ **B**（`Button.Two`）→ 前のカメラに切替
- [ ] 左コントローラ **X**（`Button.Three`）→ ScreenAnchor の head-lock ON/OFF（[`ScreenAnchor.Toggle`](../Assets/Scripts/Streaming/ScreenAnchor.cs)）
- [ ] 切替時に黒フレーム / フリーズが無い

> キーボードからは Tab / Shift+Tab / 数字キー 1〜9 で切替可（[`CameraSwitchInput.cs`](../Assets/Scripts/Streaming/CameraSwitchInput.cs)）。HMD 検証中は使わないが、デバッグで PC を覗き込む時に便利。

### Phase 2.7（自動切替）

[`PlayerZoneTracker`](../Assets/Scripts/Tracking/) の挙動確認。検証は `Assets/Scenes/Sandbox/PlayerZone.unity` 推奨：

- [ ] 中央ゾーンに立つ → 規定カメラ（例: Phone01）
- [ ] 右ゾーンに歩く → 自動切替
- [ ] 左ゾーンに歩く → 自動切替
- [ ] ゾーン境界を細かく出入り → 切替が暴れない（shrink 0.15m のヒステリシス）
- [ ] 全ゾーン外に出る → 直前のカメラが維持される（`keepLastWhenOutside` 既定 ON）

### Phase 3（映像加工）

- [ ] Editor で **FixedCamVr > Fx > Setup FxSandbox Scene** で生成済みである事を確認（実機では FxSandbox は build に入っていない点注意）
- [ ] Main シーン側で FX を有効にしたビルドを使う場合、各ゾーンに対応した FX が ON になる（CRT / Dust / 全部入り 等）
- [ ] OVR Metrics Tool で GPU < 11ms / 72fps 維持。落ちる場合は FX を 1 つずつ OFF にして原因特定

---

## 5. 切り分けフローチャート（接続問題 vs ロジック問題）

```
HMD を被る
  │
  ├─ Quest にアプリすら起動しない / 一覧に出ない
  │   → APK ビルド・インストール失敗
  │     ・adb devices で Quest が `device` として見えるか
  │     ・Build Settings に Main.unity が enabled で入ってるか
  │     ・Player Settings: ARM64 / IL2CPP / API 29+
  │     ・Build And Run のログを Console で確認
  │
  ├─ 起動した・映像出るが FPS < 60
  │   → パフォーマンス問題（実装側）
  │     ・OVR Metrics Tool で GPU / CPU を切り分け
  │     ・Profiler 接続（USB or Wi-Fi、AndroidPlayer 選択）
  │     ・Phase 3 FX を OFF にして再測（原因 FX を二分探索）
  │     ・MjpegScreen.Update の GC Alloc を Profiler で確認
  │
  ├─ 起動した・スクリーンが真っ黒
  │   → 接続問題 or 描画設定問題
  │     [接続疑い]
  │     ・PC から配信 URL がブラウザで見える？
  │     ・Quest と PC とスマホが同一 LAN（5GHz）？
  │     ・CameraSource の host/port/path 正しい？
  │     ・スマホがモバイル回線にフォールバックしてないか？
  │     [描画疑い]
  │     ・Quad の Material が URP Unlit か（Lit だと暗転）
  │     ・Quad の表裏（裏面消失）
  │     ・Color Space: Linear
  │     ・Auto Graphics API: OFF / Vulkan が最上位
  │
  ├─ 映像出る・手動切替が効かない
  │   → 入力ブリッジ問題
  │     ・OVRCameraRig 配下に OVRManager がいるか（OVRInput 初期化に必要）
  │     ・OvrControllerBridge の registry / screenAnchor 参照がアサインされているか
  │     ・asmdef を持たない場所（Assembly-CSharp）に OvrControllerBridge が居るか
  │       （asmdef 配下から OVRInput を直接呼ぶと参照解決が必要）
  │     ・キーボード Tab で切替できる？ できれば Registry は生きている → ブリッジ側の問題確定
  │
  ├─ 切替効く・ゾーンで自動切替しない
  │   → 物理空間 or PlayerZone 設定
  │     ・PlayerZone.halfExtents が現場サイズに合ってない（広すぎ / 狭すぎ）
  │     ・PlayerZone.centerOffset が床基準で設定されているか（HMD 高さ ≈1.6m を考慮）
  │     ・PlayerZoneTracker.zones にシーンの全 PlayerZone が刺さっているか
  │     ・Scene ビュー / Editor の Gizmo（緑半透明 Box）でゾーン位置を再確認
  │
  └─ ゾーン切替効く・FX が出ない
      → URP RendererFeature 配線
        ・Settings/URP の UniversalRendererData に FullScreenPassRendererFeature が入ってるか
        ・FX 用 Material（FxCrtMaterial 等）がアサイン済みか
        ・カメラの Renderer Index と Renderer 設定が一致しているか
        ・FixedCamVr > Fx > Create CRT Material を実行済みか
```

---

## 6. 不具合発生時の記録テンプレ

その場で潰せない不具合は次セッションに引き継ぐため、以下を記録：

```
- 発生時刻:
- フローチャートのどの分岐で詰まったか:
- 直前の操作（カメラ番号 / ゾーン / FX 状態）:
- HMD 内で見えていた状態（スクリーン色 / 文言 / 振る舞い）:
- Editor Console のエラー（コピペ）:
- adb logcat 抜粋（adb logcat -s Unity *:E）:
- 暫定回避策（FX OFF / カメラ固定 / スマホ再起動 等）:
- 再現性（毎回 / 散発 / 1 回のみ）:
```

> ログ取り: `adb logcat -s Unity *:E > onsite_YYYYMMDD_HHMM.log` を別ターミナルで流しっぱなしにしておくと、後追いが楽。
