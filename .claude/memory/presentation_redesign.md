# 導入と通信画面の再構築

2026-09-13。依頼は `canon/LEDGER.md` 0207 と 0208。具体的な見た目は `canon/OPEN.md` の案であり未判定。

## 変更範囲

導入の描画と通信画面の表示を再実装する。開始条件と通知の時系列は変更しない。共有設定には触れない。

導入は 0208 で割れる演出へ戻した。基底の Quad が本編と同じ矩形を透かす。周囲の現実は別の破片メッシュが透かす。四辺から閉じるマスクで先に破片を切り落とさない。両方を本編の面と同じ距離へ置く。

`IntroFractureMesh` は角度の空間で 24 枚の大面を分割する。さらに 40×40 の小面で切る。隣り合う境界を共有するため、開く前に細かな隙間が出ない。メッシュ生成の乱数は固定種の System.Random。Unity の乱数へ影響しない。

`IntroFracture.shader` は左上寄りの起点から順に大面を開く。進み .22〜.48 前後で内部を分離する。.32〜.82 に浅い曲線で近いスクリーン辺へ寄せる。移動中に短く細くなり、.76〜.84 で縁へ収まる。スクリーン内の破片は fragment ごとに覆いから除外する。

映像への混合は .72〜.88。2.5 秒の段なら 1.80〜2.20 秒。音の `Shatter` は .02 で発火する。`ScreenOn` は live が .999 へ達した時に発火する。既存の 1.70 秒の割れる音の後に約 .44 秒の間がある。音源は既存提供素材から広がり→短粒化→中心収束へ作り直した。

`shatter` は音と画の共通の進み。実際に描画へ配った最大値は `ShatterPeak`。組めた破片数は `ShatterPieces`。テレメトリは `shatStyle=art` で旧形式と区別する。新しいシェーダは Always Included Shaders に明示登録する。

通信面は元の文字列で配置を確定する。欠落と位置ずれは文字メッシュに適用する。ラテン文字を全角の文字化けへ置き換えて折り返しが変わる経路を避ける。`CorruptionFor` の回復と解除は既存の値を読む。

本文と赤い残像の GameObject は active のまま保つ。表示だけを MeshRenderer.enabled で切り替える。TMP の OnEnable が頂点の濃度を上書きして全文を出す経路を作らない。

## 検証の入口

`tools/unity.ps1 menu intro` と `menu comms-preview` は本体と同じ描画を使う。比較前の画像は `Logs/design-rebuild-20260913/before/`。日本語のほか英語とフランス語も `menu text-audit -Set lang=...` で確認する。

Editor の代理パススルーは実機の見えを保証しない。Quest の装着時の立体感と現実との合成は実機で別途確認する。

`menu intro -Set frames=1` は各コマの実状態を `clean/frames/frames.tsv` へ保存する。実 `SoundCueLogic` に通して得た発火は同じ場所の `audio-cues.tsv` へ保存する。動画の音はその判定時刻へ DSP の 35ms を足して重ねる。実機の 3D 音響と毎回の音程の揺らぎはこの確認用動画には含めない。

### 前案 a9b4633 の検証記録

以下は四辺の開口だった前案の記録。0208 の破片の検証結果として流用しない。

導入の Frame〜Swap が 123 コマ / 4.1 秒。通信の出入りと最大侵食と回復は 285 コマ / 9.5 秒。導入の代理静止画は 640×480。16:9 のスクリーンへ収めるため映像の左右には黒帯が出る。

`Logs/design-rebuild-20260913/calibrate_analyzer.py` で観測器を校正。正常描画と未描画と不正なメッシュ数を区別する。旧形式のログと途中で終わるログを含む 6 ケースが通過。

最終 EditMode は `--filter FixedCamVr` で 1612 件通過。`menu text-audit` の ja / en / fr はすべて規定内。通信本文は 1.20 度。行幅 0.438m / 枠幅 0.466m。根拠 XML とログは同じ Logs ディレクトリ。変更前後と動画を含む手元の資料は `reports/2026-09-13_presentation-redesign.html`。

### 0208 の検証記録

EditMode の `FixedCamVr` は 1618 件通過。実メッシュは 2017 片。Frame〜Swap は
123 コマ / 4.1 秒を描画した。基底窓との境界に出ていた細い黒線を除いた。
大面の回転を ±0.30 度へ抑えた。移動中の小片の面積も減らし、破片が重なる幕状の見えを減らした。

30fps の実音判定は Frame 頭から Shatter=.066667 秒 / ScreenOn=2.200000 秒 /
Bell=3.666667 秒。確認用動画では各時刻へ既存 DSP 待ちの .035 秒を加えた。
割れる音の終端と ScreenOn の間は .433333 秒。新しい音源は頭 -24.70 LUFS /
後半 -35.22 LUFS / true peak -6.77dB。波形から測った左右の幅は後半に約 17dB 小さくなる。

根拠は `Logs/intro-fracture-20260913/`。`tests-final.xml` / `frames.tsv` /
`audio-cues.tsv` / `preview-evidence.json` / `audio-evidence.json` を保存した。
音付き動画と段階ごとの絵は `reports/2026-09-13_intro-fracture.html`。
Quest の装着と描画負荷は未検証。HTML のブラウザー自動確認は file URL 制限により未実施。
