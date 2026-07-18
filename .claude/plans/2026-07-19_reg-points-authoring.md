---
status: done
created: 2026-07-19
updated: 2026-07-19
slug: reg-points-authoring
---

# 位置合わせ基準点の Web オーサリング化（N 点・順序つき・床マーカー方式）

## 概要

HMD 位置合わせのタッチ基準点が「L 字壁の外角・北腕東端」の 2 点ハードコードで、
①ドット塗りレイアウトの自由度と不整合 ②壁にコントローラをめり込ませられない物理矛盾
（RightHandAnchor は先端でもなく、系統誤差 数 cm〜10cm 超）があった。

**ユーザー提案（2026-07-19）採用**: 焼き込む Web UI（フロアマップ）上で「どこに・何点（2〜5）・どの順番で」
タッチするかを指定できるようにする。点は床の触れる位置に置ける（床×印テープ運用）→ めり込み問題消滅。
高さは元々未使用（XZ 2D 解法）なので「床マーカーの真上にコントローラをかざして A ホールド」で成立。

## スキーマ契約（show.json layout 拡張・後方互換）

```jsonc
"layout": {
  // ...既存 grid/course/wall/floor...
  "regPoints": [                       // 順序 = タッチ順。2〜5 点
    { "x": -0.5, "z": 0.5, "label": "スタート角" },
    { "x":  0.5, "z": 0.5, "label": "北東の×印" }
  ]
}
```

- course 座標系（既存 regPoint1/2 と同じ。原点=床中心・+Z=北）
- **regPoints 不在 → 従来の既定 2 点（(-0.5,0.5)/(0.5,0.5)）にフォールバック**（後方互換・既存動作不変）
- label は HMD ガイダンスに表示（空なら「点 1」「点 2」…）
- 焼き込みエクスポートは layout 丸ごとコピーなので自動で APK に乗る。端末キャッシュ（CachedConfig.layout）も同様

## 解法契約（N 点剛体フィット・XZ 2D）

- authored 点 c_i と実測点 w_i（XZ）から yaw θ + 平行移動 t を最小二乗で解く（2D Procrustes 閉形式）:
  重心 c̄,w̄、d_i=c_i−c̄、e_i=w_i−w̄ → θ = atan2(Σ cross(d_i,e_i), Σ dot(d_i,e_i))、t = w̄ − R(θ)c̄
- **N=2 のとき現行の 2 点解と数学的に一致すること**（既存テストで保証）
- 合否ゲート: フィット後の**点毎残差**を計算し、max 残差 ≤ maxResidualM（既定 0.12m・SerializeField）で合格。
  不合格時はどの点の残差が大きいかをガイダンス表示（タッチミスの特定）
- タッチ中のライブ表示は「直前の点との実測距離 vs authored 距離の誤差 %」（現行ステップ 2 表示の一般化）

## フェーズ

### Phase W: Web 卓（tools/web-compositor/ — opus 委譲・並列可）

- [x] floormap.js: フロアマップ canvas に**登録点エディタ**を追加 — 番号付きマーカー（①②③…）を
      クリック配置・ドラッグ移動・右クリック or ボタンで削除、順序入替（最小 2・最大 5）。
      既存の grid/course 編集パターン（パレット・dirty・saveLayout 同乗）を踏襲
- [x] `_default_show()` の layout 契約コメントに regPoints 追記（capture-server.py）
- [x] sim.html に regPoints 表示（最小）

### Phase U: Unity（Assets/Scripts/ — opus 委譲・並列可）

- [x] ShowLayoutDef に `ShowRegPointDef[] regPoints` パース追加（JsonUtility・後方互換）
- [x] CourseRegistrationController: 固定 2 点フロー → **N 点シーケンス**へ一般化
      （A ホールド 0.5s ×N・ガイダンスに「点 k/N: <label> の真上にかざす」・文言を床マーカー前提に変更）
- [x] 解法: 2D Procrustes（純ロジック `RigidFit2D` 分離 + テスト。N=2 で現行解と一致・N=3 残差検出）
- [x] 合否: max 残差ゲート + 点毎残差のガイダンス表示。ライブ誤差 % は「直前点との距離」に一般化
- [x] regPoints の供給: ShowControlClient → CourseRegistrationController（ZoneLayoutApplier の Func 注入
      パターンに倣い asmdef 方向を保つ）。不在時は既定 2 点フォールバック
- [x] ワイヤーフレーム検証表示に登録点マーカー（床に小さな×）も描く

### Phase V: 検証（シュビー本体）

- [x] コンパイル + EditMode テスト + Setup 再実行
- [x] Web 卓: 点の配置/移動/削除/順序 → show.json 保存 → sim 表示確認
- [x] エクスポート → 焼き込み show.json に regPoints が乗ることを確認
- [ ] Quest ビルド + インストール（実タッチ検証はユーザー）
- [x] ドキュメント同期（unity-vr.md 登録リチュアル表・README・streaming.md layout 契約・onsite-checklist）

## 境界・注意

- TableDuo・ProjectSettings・共有 .asset 不変。既存の Run/Staff モード動線・0.5s ホールド平均・
  nudge・ワイヤーフレーム・registration.json 永続は不変（点の出どころと数だけ変わる）
- 実装報告は git 物証照合

## 自律改善ログ

- 2026-07-19 Phase W/U 実装完了（opus 2 並列・git 物証照合）。テスト 138/138 pass（RigidFit2D 7 件追加）
- 検証中に MCP ブリッジ wedge → Unity 再起動で復旧（既知パターン・memory 記載どおり）
- 逸脱採用: regPoints 供給は Func 注入でなく showControl 直読み（既存 wall/floor と同作法・asmdef 方向は不変）。distanceTolerance は表示目安に用途変更し合否は maxResidualM 単独
- 実タッチ検証（床×印 → N 点シーケンス → 残差ゲート）は実機でユーザー確認待ち
