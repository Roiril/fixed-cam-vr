# Web マルチカメラ設定（IP + 画像加工）を Quest 実機へ流す

2026-06-16。fixedcam（廻リ視）専用。TableDuo は無関係。

## 目的（ユーザー要望）

1. マルチカメラビューを Web 管理画面の主役にする
2. そこでカメラ別の明るさ・画像加工を設定できるようにする
3. UI を整理する
4. **Quest にビルドした後も、その Web で設定した IP / 画像加工を実機が参照する**

## 設計：show.json を「唯一の設定契約」に昇格

Web も Quest も同じ `show.json` を読む。Quest への到達経路は 3 本、**後勝ち**で解決：

```
Quest 起動時の設定 = 焼き込み CameraSource.asset      ← 最終フォールバック
                   < persistentDataPath/show_config.json ← 起動時に上書き（前回ライブ受信のキャッシュ）
                   < ライブ long-poll（オペレータ卓が居れば）← 接続中ずっと上書き
```

「ビルド後も参照」の主役は **端末キャッシュ**。PC 不在でも前回設定で起動する。

## show.json スキーマ拡張

`cameras[i]` に既存の `host/port/auth`（従来 Web プレビュー専用だったものを **Unity 実機も読む**ように）＋ 新規 `post`（カメラ別画像加工、任意キー）：

```jsonc
"cameras": [
  { "id":"A", "sourceId":"Phone01", "host":"...", "port":8080, "auth":"user:pass",
    "post": { "exposure":0.4, "contrast":1.1, "saturation":1, "temperature":-0.1,
              "vignette":0, "grain":0, "scanline":0 } }   // 無ければ global post にフォールバック
],
"post": { ... }   // 既存 global = 全体グレーディング（カメラ別未設定時のフォールバック）
```

## 実装（このコミット）

### Unity C#（`Assets/Scripts/Streaming/`）
- **CameraSource.cs**: `[NonSerialized]` の実行時オーバーライド（host/port/user/pass）。`ApplyRuntimeEndpoint()` / `ClearRuntimeEndpoint()` / `ConnectionKey`。空 host は override 解除＝焼き込み .asset へ戻す。**asset は汚れない**（git 巻き込み防止）
- **CameraStream.cs**: `_receiver` を再生成可能に。`ReapplyConnection()` で host 変更時に MJPEG を張り直す（DHCP ズレを Web から復旧）
- **CameraStreamRegistry.cs**: `ApplyEndpoint(i, host, port, user, pass)`（変化時のみ再接続）/ `SourceCount`
- **ShowControlClient.cs**:
  - `CameraDef` を host/port/auth/post（+ `hasPost` bool）へ拡張
  - `ApplyCameraEndpoints()` で cameras[] を registry に反映
  - `ApplyPostForActive()` でアクティブカメラの個別 post（無ければ global）をマテリアルへ。`registry.ActiveChanged` 購読でゾーン切替に追従
  - 受信 show.json を `persistentDataPath/show_config.json` にキャッシュ（`SaveCache`）、起動 `Start()` で `LoadAndApplyCache`
  - **server 不在でも component を生かす**（旧コードは `enabled=false` で自滅 → キャッシュ適用・カメラ別 post が効かなくなる罠を解消）

### Web（`tools/web-compositor/`）
- **console.js**: 各カメラカードに「🎨 画像加工」（折りたたみ 7 スライダ）。変更で `cameras[i].post` を show.json へ。`●` 表示で個別有無、「↺ 全体グレーディングに戻す」で `post` 削除
- **index.html**: カメラ節を「マルチカメラ（IP・画像加工）」、global FX 節を「全体グレーディング（global）」に整理＋説明
- **style.css**: `.cam-fx`（左罫線アクセントで global と区別）
- **multicam.html**: IP の正を localStorage → **show.json** に（`syncFromState` で起動時反映、編集を `pushCameras` で書き戻し）。console / Quest と一本化
- **capture-server.py**: `_default_show()` の cameras に host/port/auth を追加（スキーマ明示）

### `hasPost` の理由（JsonUtility 罠）
JsonUtility は **null の入れ子クラスを既定値オブジェクトとして書き出す** → キャッシュ往復後に `post` の null 判定が壊れる。「個別 post を持つか」は明示 `bool hasPost` を正にし、ライブ受信パース直後（null 判定が信頼できる時）に確定してキャッシュにも保存する。

## 検証

- Unity: `refresh_unity` + `read_console` → **CS エラーなし**（実機未検証）
- Web: `node --check console.js` / multicam インライン JS OK
- 契約: capture-server に POST→GET で `cameras[A].post` 保持・`cameras[B]` は post キーなし（= Unity 側 global フォールバック）を確認

## 運用 runbook

1. `tools/web-compositor/serve.ps1` 起動 → `http://localhost:8099/`（コンソールタブ＝既定）
2. カメラカードで IP / 認証、「🎨 画像加工」でカメラ別明るさを設定 → show.json に自動保存
3. Editor+Link なら `ShowServer.asset` host=127.0.0.1。Quest 単体なら PC の LAN IP
4. Quest 実機: 一度オペレータ卓に繋いだ状態で起動すると設定がキャッシュされ、**以降 PC なしでもその IP / 画像加工で起動**

## 既知の制約 / 残課題

- **キャッシュは「一度ライブ受信した後」生成される**。完全新規インストール＋PC 不在の初回は焼き込み .asset 値で起動（Web 値は入らない）。必要なら将来 `Import show.json → Phone*.asset` エディタメニュー（git 注意：host はコミット禁止）
- per-camera post の Unity 側適用は**アクティブカメラ切替時**（全カメラ同時プレビューは VR では 1 画面なので不要）
- ShowControlClient は単一 Screen マテリアル前提（既存通り）
- 実機での per-camera post / host 再接続は未検証（コンパイルのみ）→ 現場で `[ShowControl]` / `[CameraStream] reconnect` ログ確認
