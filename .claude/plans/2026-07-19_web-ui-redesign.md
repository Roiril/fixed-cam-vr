---
status: done
created: 2026-07-19
updated: 2026-07-19
slug: web-ui-redesign
---

# Web UI の根本再設計（IP 追放・タイムライン・周回の堅牢化・オーサリング/ライブ分離）

## 概要

ユーザー指摘（2026-07-19・自己判断委任）:
1. スマホが cameraId を自分で名乗り discovery が IP を自動追従する今、**Web UI で IP を決める必要はない**
   （過去に「Web UI が正で変えられない」逆転現象も起きた）
2. 「周×カメラの表」は『2 周目の B で仕掛けたい』という思考と合わない → **動画編集風の横長タイムライン**へ
3. 体験者が試しに後ろのカメラへ戻る等での**周回誤カウントを防ぐ堅牢化**

調査で確定した追加の穴（例に引っ張られない本質）:
- **切替の出どころ無視**: LapCounter は `registry.ActiveChanged` を無差別購読 → スタッフ手動 A/B・Web cameraOverride
  でも周回が進み cue が誤発火しうる（逆走・行き来は既に前進しない実装 = LapCounterLogic の進行ポインタ）
- **ラン（体験者 1 人分）の概念が無い**: 周回カウントはアプリ起動から永続。体験者交代で 2 人目が N 周目から始まる。
  once 発火済みフラグも残る → **ランリセットが必須**

## 設計契約

### A. 切替ソースのタグ付け（Unity）

- `CameraSwitchDirector` の切替要求に **source（Zone / Manual / Override / External）** を付け、
  確定切替イベント `SwitchCommitted(int index, SwitchSource source)` を公開
- `LapCounter` は registry.ActiveChanged 直読みをやめ **Director の SwitchCommitted(source==Zone) のみ**で前進
  （手動・Web 固定・外部では周回もカメラ進入通知（cue）も動かさない。ただし CueScheduler への「現在カメラ」
  同期は維持 — ゾーン復帰時に正しく評価が再開すること）
- 既存の逆走・行き来・スキップ不算入（進行ポインタ）は不変。EditMode テストを source 込みに拡張

### B. ラン概念とリセット（Unity + Web）

- show.json `control.runEpoch: int`（既定 0）。**値が変わったら Unity は LapCounter.Reset（lap=1）+
  CueScheduler.ResetFired + 現在カメラで再シード**。端末キャッシュにも保存（PC 不在の再起動と整合）
- Web ライブ運用パネルに「▶ ラン開始（周回リセット）」ボタン（runEpoch++ を postState）
- PC 不在時: Staff モードに **左スティック押し込み = ランリセット** を追加（StaffPanel チートシート更新）
- heartbeat に現在 lap を追加（Web で「Lap 2 / cam B」が見える）

### C. IP の追放（Web・カメラカード再設計）

- カメラカード（A/B/C）は **発見ベース表示**へ: 発見された端末の {ip, version, lastSeen, conflict 警告} を
  読み取り専用表示。「IP を編集する UI」は**折りたたみ『🚨 緊急: 手動接続』内へ降格**
  （用途 = iPhone 等 discovery 非対応端末・障害時の最終手段。編集すると従来どおり pinned 化）
- show.json スキーマ・auto-follow・pinned・Quest 側は**不変**（UI の再配置のみ。契約は壊さない）

### D. タイムライン UI（Web・schedule.js 置換）

- 横長タイムライン: 区間 = 周×カメラ（layout.course.order 順に「1周目: A|B|C → 2周目: A|B|C → …」を
  横に連結）。各区間ブロックに割当 cue をクリップ風表示（無割当 = 「—」）
- 区間クリック → cue 選択（そのカメラの cue 一覧 + 「何もしない」）・delaySec・once の小編集ポップ
- 周回の追加/削除（末尾）・横スクロール。**データモデルは既存 schedule.entries {lap,camera,cueId,delaySec,once}
  のまま**（純 UI 再投影・Unity 変更なし・後方互換）
- 旧マトリクス UI は撤去（schedule.js の保存配線は流用）

### E. オーサリング / ライブ運用の分離（Web・index.html 再編）

- ページ上部にセクションナビを置き 2 系統へ再グループ:
  - **🎬 事前オーサリング**: カメラ別 cue 作成（マスク/素材/フェード）・フロアマップ（ゾーン/コース/位置合わせ点）・
    タイムライン・📦 ビルド用エクスポート
  - **🚨 ライブ運用**: 発見済み端末 + 疎通診断・ラン状態（Lap/cam/モード・heartbeat 由来）・
    ▶ ラン開始・演出 手動 ON/OFF・カメラ手動固定（ゾーン自律へ戻す含む）・緊急手動接続
- 既存機能の削除はしない（再配置と降格のみ）。プロンプト管理・録画等は現状位置で可

## フェーズ

### Phase L: Unity 堅牢化（opus 委譲）
- [x] SwitchSource タグ + SwitchCommitted イベント（Director）
- [x] LapCounter を source==Zone 駆動へ（テスト拡張: 手動/Override で前進しない・ゾーン復帰で再開）
- [x] runEpoch パース + リセット動作 + キャッシュ整合 + heartbeat に lap
- [x] Staff モード左スティック押込 = ランリセット（StaffPanel 更新）

### Phase T: Web 再構成（opus 委譲・並列可）
- [x] カメラカードの発見ベース化 + IP 編集を緊急折りたたみへ降格
- [x] タイムライン UI（schedule.js 置換・データモデル不変）
- [x] オーサリング/ライブのセクション再編 + ナビ
- [x] ▶ ラン開始ボタン（runEpoch++）+ ラン状態表示（Lap/cam/モード）
- [x] capture-server: control.runEpoch 契約コメント（フィールドは shallow 置換で通る）

### Phase V: 検証（シュビー本体）
- [x] コンパイル + EditMode テスト + Setup 再実行
- [x] Web 実操作（タイムライン編集→保存→sim・ラン開始→heartbeat 反映）
- [ ] Quest ビルド + インストール
- [x] ドキュメント同期（streaming.md 契約・README・web_compositor memory・入力早見表）

## 境界・注意

- show.json スキーマは **runEpoch 追加のみ**（他は不変・後方互換）。Quest⇄Web の既存契約を壊さない
- TableDuo・ProjectSettings・共有 .asset 不変。実装報告は git 物証照合
- コンテキスト逼迫時はこの計画ファイルが引き継ぎの正（ユーザー了承済み）

## 自律改善ログ

- 2026-07-19 Phase L/T 実装完了（opus 2 並列・git 物証照合）。テスト 141/141 pass（LapCounter source シナリオ 3 件追加）
- 設計判断: 非 Zone 切替は LapCounter に完全透過（SyncCurrent 方式は「手動+ゾーン再主張の誤カウント」を生むため不採用）。稀な取りこぼしは安全側（誤発火なし）
- Web は既存 show.json を汚さず検証（タイムライン編集・ラン開始はコード経路検証）。runEpoch は _default_show に既定 0 を明示
- 実機未検証: runEpoch リセット・左スティックランリセット・タイムラインからの実発火
