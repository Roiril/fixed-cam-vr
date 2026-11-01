---
status: done
created: 2026-11-01
updated: 2026-11-01
slug: live-screen-profile
---

# Webで調整した画面の本編採用と位置ノイズ

## 現状と採用元

ユーザーが現在のWeb調整を本編採用と指定（LEDGER 0303）。DOMの全23数値と実GPU画像を保存した。
保存先: `G:/マイドライブ/shubie/reports/2026-11-01_screen-profile-adoption/`。
`web-selection.json`が採用元、`web-selected-A.png`が加工再現の比較対象。
既存の通常Monoは初周に0→0.5。今回の指示では初期から採用値0.5へ固定する。
音のProgressと人形イベントの加工は別責務なので維持する。

## 設計

- 固定加工の正本はCrtSignalProfile。show.jsonの現場設定を編集せず、実行時の本編採用値として適用する。
- CameraFeelFxが最終feel uniformを書く。プロセッサのProfile再適用で動的ノイズを潰さない。
- 初期Monoは0.5、CoarseBlocksはBlocksFor(0.5)。位置や滞在によって増加させない。
- 暗部ノイズだけ、確定したsegmentLapとCourseOrder内位置から0.05→0.2へ線形にする。
- ABC順では1A=.05、1B=.075、1C=.1、2A=.125、2B=.15、2C=.175、3A=.2。逆走は同じ位置の値へ戻す。
- 人形視点/人形化でMono=1になる既存イベントは維持。人形化後のノイズは.2を保持する。
- 解除時は既存ReleaseKで初期のMono=.5/ノイズ=.05へ戻す。次の体験者でも初期値へ戻す。
- Webの基準値も新採用値へ同期。ユーザーのブラウザ保存値は保持する。
- ユーザーの追加指示で、無線ADBも使えなければ今回はPC内の検証で進める。接続0台・登録済みQuest無線endpointなし。mDNSは`daemon unavailable`で探索自体を使えない。PC成功を実機合格とは呼ばない。

## フェーズ

- [x] 現在の23値・GPU PNG・画面を保存。
- [x] 旧位置進行とProfile再適用の責務を調査。独立design-criticの指摘を実コードで照合。
- [x] 本編の固定値とノイズ位置進行・観測を実装。
- [x] EditMode2308件とGPU11枚で初期再現・全7位置・逆走・停滞・人形化・解除・リセットを確認。解析回帰45件も通過。
- [x] ユーザー編集のbyte保持を照合。検証後のソース変更は古いコメントの訂正のみ。
- [x] 無線ADBも確認。利用不可のため、追加指示に従い今回はPC検証を完了条件にする。
- [x] canon/referenceを現行実装へ置換。コミット・pushの対象は本件の明示ファイルだけ。

## 未解決

実機表示・Android上の性能は未確認として残す。今回は追加指示に基づき接続待ちにしない。
