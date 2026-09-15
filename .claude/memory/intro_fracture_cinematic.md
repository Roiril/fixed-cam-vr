---
name: intro-fracture-cinematic
description: 2026-09-15（0221 / 0222）。現実が割れる遷移を映画の速度変化（一撃 → 引き延ばした時間 → 密度の上がる集結）へ書き直した。段 4 は 5.0 秒。時計は画・音・IntroLogic の 3 か所が同じ表から出ている。形は起点からの網、質感はガラスの縁の光。触るときの対の関係と計器の罠。
metadata: 
  node_type: memory
  type: project
  originSessionId: 87e0f4ff-efaa-4154-9f88-dae7f1de0a58
  modified: 2026-09-15T03:35:20.022Z
---

# 現実が割れる遷移（0221 / 0222 の版）

**時計の表は 1 つ。** `Assets/Art/Shaders/Intro/IntroFracture.shader` の先頭の表（p = 段 4 の進み 0..1、段は 5.0 秒）が正で、
音（`tools/ingest-sounds.py` の `SWARM_*`）と `IntroLogic` の `frame` / `live` はその写し。

| p | 秒 | 何 | 画 | 音（クリップ秒 ＝ 映像上 − 0.135） |
|---|---|---|---|---|
| .000–.060 | 0.00–0.30 | 予兆 | 亀裂が起点から外へ光って走る（`glowFront`）。位置は保つ | 軋み 3 粒 0.00–0.13 |
| .060–.100 | 0.30–0.50 | 一撃 | 全域が同時に閃く（`shock`）。破断の波が起点から 0.2 秒で全域へ（`breakAt` ＝ `macro.z` の順）。行程の 8 割を時定数 120ms で飛ぶ | 同じ一撃を音程 0.30 / 0.42 / 0.62 で重ねる 0.17 |
| .100–.520 | 0.50–2.60 | スロー | 引き延ばした時間 `u = 0.8(1−e^(−x/.024)) + 0.6x`。漂い約 8cm/s・順回転数°/s。**止めない** | 高く小さい粒 5 つ（0.78〜2.13） |
| .520–.900 | 2.60–4.50 | 集結 | 1 片ずつ 90ms（大片 125ms）で直線に戻る。着地時刻は `order^0.30`（密度が時間の 2.3 乗）。5% が先駆け、枠を閉じる 3 片が最後 | 44 粒を同じ分布で 2.47–4.16。大片 3 打 4.17–4.30 |
| .900–.940 | 4.50–4.70 | 実景の面 | `frame = SmoothStep(.84, .90)` で矩形確定 | 静けさ |
| .940–.990 | 4.70–4.95 | 混合 | `live = SmoothStep(.94, .99)` | ScreenOn は live ≥ .999（約 4.95 秒） |

## 対で動かすもの（片方だけ動かすと沈黙して食い違う）

- 段 4 の尺 **5.0 秒**は 5 か所: `IntroTiming.Default` / `ShowIntroDef` / 卓 `intro-model.js` の `INTRO_DEFAULT` /
  `capture-server.py` の `_default_show` / **卓の `show.json`（`run.intro.frameSec`・git 外・サーバのメモリ経由で更新した rev 1207）**。
  導入の合計は 10.2 → **12.7 秒**（`IntroLogicTests.TotalSec…` と `intro-model.test.mjs` が固定）
- 集結の始まりと終わり: シェーダ `SnapBegin .52` / `SnapEnd .90` ↔ `SWARM_GATHER_START 2.47` / `SWARM_GATHER_END 4.16`
- 混合の終わり: `live = SmoothStep(.94, .99)` ↔ `SoundCueLogic.ScreenOnAt .999` ↔ `sound-preview.py` の `SCREEN_ON_AT` ↔ `SWARM_SEC 4.42`
- 起点: シェーダ tan(−0.16, 0.12) ↔ `IntroFractureMesh.ImpactX/Y`（角度空間 −0.143, 0.108）。網の中心・破断の波の起点・破片の飛ぶ向きの 3 つがここ

## 形（`IntroFractureMesh.BuildFracturePoints`）

15 本の放射線 × 10 本の同心の輪の節（角度 ±36% の区画・半径 ±22%・2 本目の輪から 12% を欠かす）＋
放射線に沿う細長い片（5 本）＋ 境界の点 ＋ 大区分 24 点 → Delaunay → 隣接三角の 1/4 を凸四角へ。
⚠ 節を揃えすぎると升目が長方形になって `IsRectangleLike` に落ち、`Only N valid fracture quads` で Build が落ちる。
揺らぎを減らすときはそこを見る。

## 質感（fragment）

稜線（1 画素）＋ 縁に沿う光の帯（`exp(−d/0.0011)`・約 1cm）＋ 斜めの艶（grazing）＋ 鏡面の閃き（`pow(n·h, 36)`・スロー中だけ）＋
遠い片を沈める fog。色は生成り寄りの白 (0.96, 0.90, 0.78)。**飛んでいる間だけ**（`detail = crack × (1 − travel)`）掛かるので、
着地した面は素の写真へ戻る。写真の明暗は残す。

## 計器の罠（このセッションで踏んだもの）

- **短い窓の LUFS はゲートで落ちて −70 を返す。** 0.4 秒の窓で「雪崩 −70 LUFS」と出て無音に見えたが、
  0.1 秒ごとの尖頭を並べると −16〜−21dB で鳴っていた。区間の有無は尖頭 dB で見る（`ingest-sounds.py` の要約もそう直した）
- **高く速い粒は再標本化で半分の長さになる。** 10ms を音程 2.1 で切ると 5ms、フェードに食われて尖頭 −48dB ＝ 存在しない。
  20ms 前後で切って −14〜−18dB
- **3 つの一様乱数の平均は 0.1 を下回らない。** `order^0.30` と組むと最初の粒が 3.3 秒まで出ず「徐々に」が消える。
  画は 5% の片、音は先頭 3 粒を先駆けに置いた
- `menu intro -Set frames=1` の探針は「止まるはず」（着地後 .91→.93）と「動くはず」（スロー .30→.32 が 0 でない・
  一撃 .07→.09 がその 5 倍）の両方を流す（`motion-proof.json`）。旧版の「静止区間が動いていない」探針は
  スローが止まらない設計と矛盾するので置き換えた

関連: [[presentation_redesign]]（0207〜0220 の経緯）/ [[sound_pipeline]] / [[hud_font_and_preview]]
