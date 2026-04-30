---
status: done
owner: shubie
created: 2026-04-30
completed: 2026-04-30
---

## 完了サマリ（2026-04-30）

- Phase 1-4 のスクリプトを実装し、`Assets/Settings/Cameras/` に Phone01/Phone02 SO を作成
- `Main.unity` に `[Streaming]` GameObject を配置し `CameraStreamRegistry` + `CameraSwitchInput` + `OvrControllerBridge` を attach
- 既存 `Screen` Quad の `MjpegScreen` を registry モードに切替、子に TMP `SourceLabel` (CurrentSourceLabel) を追加
- UX 改善: ScreenAnchor デフォルトを unlock 化、MeshCollider 削除、Renderer の shadowCasting/probes OFF、overrideUrl クリア
- アーキ修正: Meta XR SDK 201 が asmdef を持たないため、OVR コントローラ入力は asmdef 外の `Assets/Scripts/OvrBridge/OvrControllerBridge.cs` に分離（Assembly-CSharp 経由で OVRInput 利用）

実機検証はユーザに移譲（Quest 3 ビルド & デプロイ）。


# 複数 DroidCam の切替表示

## 目的

複数台のスマホ (DroidCam / IP Webcam) を同時に走らせ、Unity 側 (Quest 3) で **どのスマホの映像を VR 内スクリーンに映すか** を実行時に切り替える。

## 設計方針

[.agent/rules/streaming.md](../rules/streaming.md) のガイドに従う：

- **切替時は常時受信を維持**（再接続コストを払わない）
- **同時 3 台まで** を実用想定。それ以上は要計測
- カメラ定義は `CameraSource` ScriptableObject（既存）

つまり「切替 = 受信を止める/再開する」ではなく、「**全カメラの受信は走らせっぱなしで、Quad に流すテクスチャだけ差し替える**」方式とする。

## アーキテクチャ

```
[CameraSource SO × N]              ← 設定（host/port/path/displayName）
        │
        ▼
[CameraStreamRegistry]             ← N 個の Receiver と Texture2D を保持・常時受信
        │  (per-camera) MjpegStreamReceiver → Texture2D
        │
        ▼
[CameraSwitcher]                   ← 現在のアクティブ index を持ち、Quad に Texture を割り当て
        │
        ▼
[MjpegScreen (Quad)]               ← 既存。受け持ちが「単一 receiver 駆動」から「Switcher 駆動」に変わる
        │
        ▲
[CameraSwitchInput]                ← Quest コントローラ / キー入力で next/prev/index 指定
```

## 実装ステップ

### Phase 1 — 受信側の複数化（MJPEG 受信を独立化）

**目的**: 1 つの `CameraSource` に紐付く「受信 + テクスチャ更新」を 1 ユニットとして再利用可能にする。

1. `Assets/Scripts/Streaming/` に **`CameraStream.cs`** を新規作成
   - `CameraSource` を 1 つ受け取り、内部で `MjpegStreamReceiver` を起動
   - 自前で `Texture2D` (RGB24, 2x2 動的リサイズ) を保持
   - `Update()` 相当を `Tick()` メソッド化（`MonoBehaviour` ではなくプレーンクラス）
   - `Texture2D Texture { get; }` / `bool IsConnected { get; }` / `string DisplayName { get; }` を公開
   - `Dispose()` で receiver と Texture2D を解放

2. **`CameraStreamRegistry.cs`** (`MonoBehaviour`) を新規作成
   - `[SerializeField] CameraSource[] sources;`
   - `Awake` で `CameraStream[]` を全件生成・常時受信開始
   - `Update` で全 stream の `Tick()` を呼ぶ（`Texture2D.LoadImage` はメインスレッドでないと不可なため）
   - `int Count` / `CameraStream Get(int index)` / `int ActiveIndex` (set/get) を公開
   - `OnDestroy` で全 stream を `Dispose`

### Phase 2 — 表示側の切替

**目的**: Quad が「Registry の現在 active な stream の Texture」を映すようにする。

3. **`MjpegScreen.cs` をリファクタ**
   - 既存の単一 `CameraSource` モード（`overrideUrl` / `source` 直結）は **互換のため残す**
   - 新規モード: `[SerializeField] CameraStreamRegistry? registry;` を追加
   - `registry` が設定されていれば毎フレーム `registry.GetActive().Texture` を `Renderer.material.mainTexture` に再アサイン（同じ参照なら no-op で OK）
   - 自前の `MjpegStreamReceiver` 起動ロジックは registry モードでは使わない

   ※ 既存 PoC (Phase 2 シーン未配置) を壊さないため、既存挙動はフォールバックとして温存。

### Phase 3 — 切替入力

4. **`CameraSwitchInput.cs`** を新規作成
   - `[SerializeField] CameraStreamRegistry registry;`
   - 入力ソース（最初は全部 OR）：
     - **キーボード** (Editor): `1` / `2` / `3` で index 直接指定、`Tab` で next、`Shift+Tab` で prev
     - **Quest コントローラ**: `OVRInput.Button.One` (A) で next、`OVRInput.Button.Two` (B) で prev
   - 範囲外 index は無視 or wrap-around（**wrap-around** を採用）

### Phase 4 — VR 内デバッグ表示（軽量）

5. 現在の `displayName` と接続状態を Quad の四隅 / 上端に **TextMeshPro** で表示（任意・最小限）
   - `CurrentSourceLabel.cs` で `registry.GetActive().DisplayName` を毎フレーム反映
   - 切替直後 2 秒だけ表示するフェード（後回し可）

### Phase 5 — シーン配置 & 検証

6. `Assets/Settings/Cameras/` ディレクトリを新設、`Phone01.asset` / `Phone02.asset` (CameraSource SO) を作成
7. `Main.unity` に空 GameObject `[Streaming]` を配置し `CameraStreamRegistry` をアタッチ、`sources` に 2 つを assign
8. Quad の `MjpegScreen` を registry モードに切替、registry を参照
9. `[Streaming]` に `CameraSwitchInput` をアタッチ
10. **コンパイル検証**（MCP `refresh_unity` → `read_console`）
11. 実機 Quest 3 ビルド & 動作確認はユーザー依頼

## 受け入れ基準

- 2 台のスマホで DroidCam を起動した状態で Quest 3 を起動すると、**両方が常時受信状態**になる（ログで確認）
- A ボタン押下で表示が Phone02 に切り替わる。**切替に再接続待ちが入らない**（即座に映像が切り替わる）
- B ボタンで Phone01 に戻る
- どちらか片方が切断中でも、他方は影響を受けない
- 90Hz が維持される（Quad 1 枚 + 受信 2 本まで。3 本以上は計測課題）

## 受け入れ基準外（今回やらない）

- WebRTC 切替（streaming.md の将来項）
- 同時 4 台以上
- スクリーンを複数 Quad に並べてマルチビュー（フェーズ 4 以降の演出側）
- 切替時のクロスフェード演出（CCTV 風シェーダ Phase 3 と一緒にやる）

## リスク

- **GC アロケ増加**: Receiver を N 本走らせるとフレーム内 `LoadImage` が N 回起き得る。表示中以外の stream は `LoadImage` を **間引く**（例: 30Hz cap or 「アクティブ以外は 5fps」）必要が出るかもしれない → Phase 5 検証後に対応
- **帯域**: 720p × 30fps × 3 本 ≈ 30Mbps。5GHz Wi-Fi 必須
- **同一スレッドプール枯渇**: `MjpegStreamReceiver` は `Task.Run` 1 本ずつ。3 本程度なら問題なし

## 触るファイル一覧

- 新規: `Assets/Scripts/Streaming/CameraStream.cs`
- 新規: `Assets/Scripts/Streaming/CameraStreamRegistry.cs`
- 新規: `Assets/Scripts/Streaming/CameraSwitchInput.cs`
- 新規: `Assets/Scripts/Streaming/CurrentSourceLabel.cs`（任意）
- 改修: `Assets/Scripts/Streaming/MjpegScreen.cs`
- 新規 SO: `Assets/Settings/Cameras/Phone01.asset`, `Phone02.asset`
- 改修シーン: `Assets/Scenes/Main.unity`
- README: `## 配信側（スマホ）設定例` に「複数台時の切替操作」節を追記
