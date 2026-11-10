---
name: glossary
description: ユーザーの言葉・文書の言葉・コード名の対応。ユーザーの発話を読むとき、ユーザーへ書くとき、識別子に名前を付けるときに使う
---

# 用語の対応

出どころは `reports/2026-11-03_naming-consistency.html`（LEDGER の逐語・文書・コードの突き合わせ）。
ユーザー本人の認識はレポートの空欄にまだ書かれていない。書かれたらこの表を直す。

## 1 語が複数を指す。文脈で決まらなければ、どれかを書き添えて返す

| ユーザーの言葉 | 指すもの（LEDGER） | コード |
|---|---|---|
| 周・N周目・N-X | 体験者向けは 3 周。内部は 4-A まで数える | 進行 `ProgressLap` / 区間 `SegmentLap`。ログは `plap=` / `lap=` |
| カメラ | 配信スマホ（0293）・映像（0045）・区間の切り替わり（0057）・作品名・絵の中の視線（0135） | `camera`（0〜2）/ `cameraId`（A〜C）/ `Phone01`〜 |
| A / B / C | 区間（0247）・カメラ（0050）・右コントローラの A ボタン（0007）・案の記号（0180） | 区間は `camera` 番号。take id `L1C1` の C はカメラ番号（0 始まり）で 1-B |
| 位置合わせ | 床の登録（0184）・録画と凍結点を揃える（0050）・卓のカメラ校正（0120） | `CourseRegistration*` / 3-A の位置合わせ版 / `ShowCameraCalibDef` |
| 記録・録画 | 報告が残る・2-A のコマを残す・押した回数（0247）／体験中の録画・実機キャプチャ（0236） | `VisitorMark*` / `SegmentRecorder` / `quest-record.py` |
| webUI | 卓（0120）・笑い声の調整（0118）・博士タブレット（0193）・加工の調整（0300） | `web-compositor` / `VisitorPortal` ほか |
| ノイズ・マスク | 黒い覆い（0089・0103）／音・加工の値（0030・0303）／合成範囲（0137） | `IntroVeil`・`SwapDiffAt` / マスク画像 |
| 最後・終了・4-A | 帰りの A（0167・0247） | `ShowRunDefaults.IsClosingSegment` |

## 同じものを複数の言葉で呼ぶ

| もの | ユーザーの言葉 | 文書・コード |
|---|---|---|
| 作品 | 廻り視（り はひらがな）・固定カメラプロジェクト・まわりみ | 廻リ視 / `FixedCamVr` / `mawarimi` / build 対象 `fixedcam` |
| 報告の対象 | 異変・異常・怪異 | `Anomaly*` |
| 異変を消す | 解除（地の文）。台詞は取り除きました（0284） | `Released` / `ScreenDecay` / ログ `coarseRel` |
| 人を隠す覆い | 黒いの・黒いマスク・黒いノイズ | 覆い / `IntroVeil`（導入）・`SwapDiffAt`（すり替わり） |
| 終わり方 | 帰還エンディング・人形から戻れなくなってしまったEND | 帰還End・人形End・中断 / `Released`・`Trapped`・`Interrupted` |
| ずっと流す曲 | HorrorBGM（0088）。周ごとの曲は背景BGM | `HorrBGM` |

## AI が持ち込んだ言葉

区間・ラン・覆い・プレート・take・カット・締め・劇伴・帰還End・人形End は、ユーザーの逐語にほぼ無い。
ユーザーへ書くときは上の表のユーザーの言葉を使う。どうしても使うなら初出で何のことかを書く。

## コードで守ること

- `CurrentLap` という名前は使わない。進行は `ProgressLap`、区間は `SegmentLap`
- `ShowControlClient.LapCountReset` は周の数え直しで、導入から本編へ移るときにも発火する。体験者が替わった合図に使わない
- 結果画面の報告数は押した回数（`VisitorMarkCount`・0247）。`ReportedAnomalyCount` は体験者の結果画面には出さない集計（スタッフの `StatusHud` には出る）
