# パフォーマンス計測ガイド

Quest 3 で **90Hz 維持** を確認するための計測手順とチェックリスト。

## ベンチマーク基準

| 指標 | 目標 | 備考 |
|---|---|---|
| FPS | **90fps** 維持 | Quest 3 既定リフレッシュレート |
| GPU Time | **< 11.1ms** / frame | 1 frame = 1000/90 ≒ 11.1ms |
| CPU Main Thread | **< 11.1ms** / frame | 同上 |
| GC Alloc / frame | **0 byte** を目標 | 特に Update / FixedUpdate / 描画パス |
| Dropped frames | 0 | OVR Metrics Tool で監視 |

90fps を割ると Asynchronous SpaceWarp (ASW) が 45fps + 補完にフォールバックし、視覚的に目立つアーティファクトが出る。

## 計測ツール

### 1. OVR Metrics Tool（実機オーバーレイ）

Quest 内で常時 FPS / GPU / CPU を表示する公式ツール。

**インストール**:

```bash
# Quest 3 を USB-C 接続して開発者モード ON 済み前提
adb install -r OVRMetricsTool.apk   # Meta 公式サイトから入手
```

**使い方**:

1. Quest 内 → ライブラリ → 提供元不明 → **OVR Metrics Tool**
2. **Persistent Overlay** を ON にすると、自作アプリ起動中に FPS / GPU ms / CPU ms / Temperature / Battery がオーバーレイ表示される
3. **CSV ログ出力** も可（後述の Profiler 解析と突き合わせる用）

### 2. Unity Profiler（USB / Wi-Fi 接続）

詳細なフレーム解析。

**USB 接続（推奨・低レイテンシ）**:

1. Quest 3 を USB-C 接続
2. Build Settings → **Development Build** + **Autoconnect Profiler** にチェックしてビルド
3. Window → Analysis → **Profiler**
4. 上部ドロップダウン `Editor` を **AndroidPlayer (Quest 3)** に切替

**Wi-Fi 接続**（USB が使えない時）:

```bash
adb tcpip 5555
adb connect <quest-ip>:5555
# 以降 USB を抜いても adb 通信が続く
```

**主要ビュー**:

- **CPU Usage** → Hierarchy / Timeline で重い関数を特定
- **GPU Usage** → Quest の Vulkan ドライバ経由でカメラパス / ポストエフェクト時間を見る
- **Memory** → Texture / Mesh / GC Allocated の増減

### 3. `OVRManager.display.displayFrequency` 確認

実行中のリフレッシュレートをコードから確認:

```csharp
Debug.Log($"Display Frequency: {OVRManager.display.displayFrequency} Hz");
```

90 以外（72 / 80 / 120）になっていたら Player Settings → XR Plug-in Management → Oculus → **Target Devices: Quest 3** と **Display Refresh Rates** の設定を確認。120Hz は GPU 余裕がないと逆に揺れる。

### 4. RenderDoc for Oculus（GPU 詳細解析）

Vulkan のドローコール単位で時間を見たい時に使う。シェーダのどのパスが重いかを断定できる。Meta 公式の **RenderDoc for Oculus** をダウンロードして Quest 3 に接続。

## ボトルネック チェックリスト

優先度高→低の順で潰す。

### A. GC アロケ（CPU スパイク原因 No.1）

- [ ] Profiler → CPU Usage → **GC Alloc** 列を frame ごとに確認
- [ ] `MjpegScreen.Update` でアロケが出ていないか（→ `byte[]` プールが効いているか / `Texture2D.LoadImage(markNonReadable: false)` で再利用しているか）
- [ ] `string` 連結 / `LINQ` / `foreach` over `IEnumerable` をホットパスで使っていないか
- [ ] `Debug.Log` をフレーム内で呼んでいないか（出力時に boxing で確実にアロケする）
- [ ] イベント（`Action<T>`）の subscribe / unsubscribe をフレーム内でやっていないか

### B. Texture2D 再生成

- [ ] フレームごとに `new Texture2D(...)` していないか
- [ ] `LoadImage` の第二引数 `markNonReadable` を **false** で呼び、Texture2D 自体を使い回しているか
- [ ] MJPEG 解像度変更時のみ作り直す設計になっているか（毎フレーム作り直すと GPU メモリ断片化）

### C. シェーダ複雑度

- [ ] Phase 3-1 CRT (`FxCrtPostFx.shader`) の scanline / grain / vignette を全部 ON にしてもフレームに収まるか
- [ ] Phase 3-4 Sobel (`FxSobel.compute`) の dispatch サイズが入力解像度に対して妥当か（thread group 数が多すぎると wave が詰まる）
- [ ] フラグメントシェーダ内の `pow` / `sin` / `cos` / `tex2Dgrad` の数（モバイル GPU は超越関数が高コスト）
- [ ] **MSAA**: URP Asset の MSAA を 4x → 2x に下げて GPU が空くか試す

### D. Compute Shader dispatch サイズ

- [ ] `Dispatch(threadGroupsX, threadGroupsY, 1)` の値が `(width / 8, height / 8, 1)` のように切り上げられているか（端数を 0 にすると端のピクセルが処理されない）
- [ ] kernel 内の `[numthreads(8, 8, 1)]` 等のスレッド数が GPU の wave size（Quest 3 Adreno は 64 / 128）に対して効率的か
- [ ] グローバルメモリへの read/write 回数が多すぎないか（`groupshared` で共有メモリに展開）

### E. その他

- [ ] フレーム内で `Camera.allCameras` / `FindObjectsOfType` を呼んでいないか
- [ ] Physics の `FixedTimestep` を必要以上に細かくしていないか（既定 0.02s で十分）
- [ ] Animator / Particle System が画面外でも動いていないか（Culling Mode を `Cull Update Transforms` に）
- [ ] **DRS (Dynamic Resolution Scaling)** を URP で有効にして余裕を作る選択肢

## 計測フロー（標準手順）

1. **OVR Metrics Tool** をオーバーレイ ON で起動
2. 自作アプリを Build And Run（Development Build + Autoconnect Profiler）
3. オーバーレイで **FPS が 90 を割らないか** をシーン全域歩いて確認
4. 割っているシーン位置を特定したら、その状態のまま Unity Profiler の **Frame Debugger** で当該フレームを撮る
5. CPU / GPU いずれが律速かを見て、上記チェックリスト A→E の順で潰す
6. 修正後、同じシーン位置で再計測 → 90fps 安定を確認

## 関連

- [TROUBLESHOOTING.md](../TROUBLESHOOTING.md) — 「フレームレート低下」セクション
- [CLAUDE.md](../CLAUDE.md) — Quest 3 / VR 固有の規約（GC アロケ禁止 等）
- [docs/architecture.md](architecture.md) — どのモジュールがホットパスかの俯瞰
