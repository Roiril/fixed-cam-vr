---
name: experience-shots
description: 左グリップで体験中の画を 4 種類（スクリーン・生映像・合成のみ・体験者の視界）端末へ PNG 保存する機能。ON/OFF・取り出し・罠・検証状況
metadata:
  node_type: memory
  type: project
  originSessionId: 42a6be08-93ab-478f-bd73-44aef863545c
  modified: 2026-09-30T04:39:54.863Z
---

# 体験中の撮影（左グリップ）— 2026-09-30

ユーザー依頼: 「説明資料に乗せるように、体験中のメインスクリーン映像を撮影したい。左コントローラーの
グリップに撮影機能を割り当て、押したときの ①表示されているスクリーン映像 ②加工前の生映像
③（合成しているものがあれば）合成しているものだけ ④クエストから体験者が見ているもの を画像として保存」。

## 何が出るか（1 回の押下 = 1 フォルダ）

`Android/data/com.roiril.mawarimi/files/shots/<日時>_<通し番号>/`

| ファイル | 中身 |
|---|---|
| `1_screen.png` | ① スクリーンの最終合成（ライブ・素材・CG・加工・CRT の面の中で完結するもの）。1280x720 |
| `2_raw.png` | ② 画面へ出ているカメラの**受信フレームそのもの**（加工なし・実寸 640x480 など） |
| `3_layers.png` | ③ 合成しているものだけ。**画面の材質を複製してライブだけ黒**にし、同じ加工で描く。素材・CG・マスクが無い瞬間は撮らない |
| `3_cg_alpha.png` | ③ の CG 層そのもの（透過 PNG・premultiplied を割り戻す）。CG が出ている瞬間だけ |
| `4_hmd.png` | ④ 体験者の視界。**左眼の投影行列**を使った単眼カメラの描画（1600 幅・縦は投影の縦横比から） |
| `info.json` | 相・周回・画面のカメラ・演出 id・合成の強さ・**撮れなかったものの理由** |

## 実体

| 何 | どこ |
|---|---|
| 押下の検知 | `OvrControllerBridge`（`RawButton.LHandTrigger`。前フレームは**生の値**で持つ＝再接続をまたいで握られていたグリップが撃たれない）。SerializeField は足していない（prefab YAML の罠） |
| 撮影の本体 | `Streaming/ExperienceShotCapture.cs`（実行時に `Ensure()` で生やす。シーンには焼かない＝`menu scene` 不要） |
| 判断・名前・有効化 | `Streaming/ExperienceShotLogic.cs`（純ロジック・テストあり） |
| PNG | `Streaming/ShotPngWriter.cs`（自前。`EncodeToPNG` はメインスレッド専用で Quest では数十 ms 止まる） |
| CG 層の RT | `ShowCgLayer.Layer`（読み取り専用の口を 1 つ足した） |
| 取り出し・ON/OFF | `tools/quest-shots.py`（`pull` / `status` / `on` / `off` / `clear`） |

## 体験を止めない作り

GPU 側の写し（Blit・カメラ描画）は `WaitForEndOfFrame` の 1 フレームで終え、**CPU への読み出しは 1 枚ずつ
別のフレーム**、PNG の圧縮と書き出しは背景スレッド。まとめて読むと GPU 待ちが 1 フレームに積み上がって
体験者の視界が止まる。受理の手応えは `LeftMark`（左の 1 発）を使い回している。

## ⚠ 有効化 — 展示本番の機は OFF

**左グリップは体験者が握り込むと普通に押される**（`memory/controller_input_final.md` の握り込みの話と同じ）。
ON のままだと握り直すたびに撮影が走る。

- 既定: **Development ビルドは ON / Release ビルドは OFF**（`ExperienceShotLogic.IsEnabled`）
- 端末の印で上書き: `shots/OFF` は常に止める・`shots/ON` は Release でも許す。**OFF が勝つ**
- ⚠ **`build fixedcam` の既定は Development なので、展示用 APK も既定では ON。** 展示本番の機は
  `py -3.11 tools/quest-shots.py off`（`docs/onsite-checklist.md` の前日までの項に入れた）
- ⚠ 印はアプリの起動時に **1 度だけ**読む（static にキャッシュ）。変えたらアプリを再起動する
- 暴走の頭打ち: 受理の間隔 2 秒（`CooldownSec`）・1 起動 60 回（`MaxSetsPerLaunch`）。書き出し中の再押下は黙って捨てる

## ⚠ 限界と罠

- ⚠⚠ **④ にパススルー（現実）は入らない。** パススルーは OS が合成するのでアプリの描画バッファに存在しない。
  本編は背景が黒なので実害は小さいが、**導入（現実が見えている段）では現実の所が黒**になる。
  現実込みで撮りたいなら `quest-record.py`（`screenrecord` はパススルーを拾う）から切り出す。
  ⚠ `PassthroughCameraAccess`（`IntroPassthroughCapture` が使っている）は**カメラの生の画**で、
  「体験者が見ている合成」ではない。使うなら姿勢と画角の合わせが要るので入れていない
- ⚠⚠ **描いた直後に一時カメラを壊すと、描いた絵が RT から消える**（EditMode で実測: 壊す前に読めば赤 / 壊した後は黒）。
  `RenderView` はカメラを `out` で返し、**読み出しが終わってから**壊す。`Job.owner` がそれ
- ⚠ **① は CRT の曲面・角の丸みの外側は写らない**（スクリーンの material を描いた面の絵で、メッシュの曲面は入らない）。
  HMD 内の見え方は ④ で撮る
- ⚠ **③ は「ライブを黒に置き換えた」絵**で、素材の縁の抜け（透過）は無い。透過が要るのは CG 層だけ
  （`3_cg_alpha.png`）。素材の透過が要れば素材の元ファイルとマスクを別に使う
- ⚠ 分割（`splitFlip` / `splitFreeze`）や入れ替わりの覆いは**ライブ由来の効果**なので ③ には入らない
  （`_OverlayStrength` / `_Overlay2Strength` / `_CgStrength` のどれかが立っているときだけ ③ を撮る）
- ⚠ `adb pull` は使えない（scoped storage で 0 バイトになる）。`tools/quest-shots.py pull` は `exec-out cat`
- ⚠ 2 台の Quest が繋がっていると `--serial` を要求する（黙って選ぶと別の機の写真を読ませる）

## 検証状況（2026-09-30）

- EditMode 1995/1995 緑。撮影のために足したのは 20 本: PNG の往復（向き・alpha・大きい画・premultiplied）8 /
  ロジック 11 / **GPU を実際に回すもの 3**（RT → CPU → PNG が向きを保つ・ライブを黒に置き換えた合成が
  素材を残してライブだけ消す・単眼カメラの描画が背景色を返す）。`tools/test_quest_shots.py` 8 本
- ⚠ **実機（Quest）では未検証。** 実機で確かめること: ①左グリップで 1 発震えてフォルダが 1 つ増える
  ②`pull` で 5 枚前後が取れて 4 枚とも中身が違う ③④ が体験者の視界と合っている（画角・向き）
  ④撮影の瞬間に視界が止まらない（`[Shot]` のログと目視）⑤`3_layers` が合成の瞬間にだけ出る
- ログは `[Shot]` タグ。受けなかった理由（Disabled / CapReached）・撮れなかったもの・保存先が残る

関連: [[controller-input-final]] / [[onsite-day-ops]] / [[ending-roll]]（同じ `EndingFrameCapture.CaptureComposite` を使っている）
