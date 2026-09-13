# 導入と通信画面の再構築

2026-09-13。依頼は `canon/LEDGER.md` 0207〜0209。具体的な見た目は `canon/OPEN.md` の案であり未判定。

## 変更範囲

導入の描画と通信画面の表示を再実装する。開始条件と通知の時系列は変更しない。共有設定には触れない。

導入は 0208 で割れる演出へ戻した。0209 では中央を無傷で残していた窓を廃止した。
破砕中の基底 Quad は現実を透かさない。中央を含む全域を同じ破片メッシュが透かす。
四辺から閉じるマスクで先に破片を切り落とさない。
破砕中の隙間は黒にする。基底 Quad は alpha と RGB を別々に掛ける。
映像混合が始まるまでは先描きされた管の燐光も消す。燐光が残ると中央の隙間だけが茶色になり、
完成前からスクリーンの矩形が見える。混合開始後は RGB を維持し、混合量は破片だけへ渡す。

`IntroFractureMesh` は角度の空間で 24 枚の大面を分割する。さらに 40×40 の小面で切る。隣り合う境界を共有するため、開く前に細かな隙間が出ない。メッシュ生成の乱数は固定種の System.Random。Unity の乱数へ影響しない。

`IntroFracture.shader` は左上寄りの起点から順に大面を開く。中央も内部の小片へ分離する。
.30〜.82 で全頂点を元の角度座標からスクリーン内の対応点へ移す。スクリーンの実平面の奥行きも使う。
局所変形は中間だけに加える。終端では共有頂点が一致し、同じ片が矩形全体を隙間なく敷き詰める。
中央を除外する fragment の分岐は持たない。.82〜.88 は完成した実景の面を見せる。

映像への混合は .88〜.98。2.5 秒の段なら 2.20〜2.45 秒。音の `Shatter` は .02 で発火する。
`ScreenOn` は live が .999 へ達した時に発火する。1.70 秒の割れる音の後に約 .70 秒の間がある。
音源は0208で作った広がり→短粒化→中心収束を継続する。

`shatter` は音と画の共通の進み。実際に描画へ配った最大値は `ShatterPeak`。組めた破片数は `ShatterPieces`。テレメトリは `shatStyle=art` で旧形式と区別する。新しいシェーダは Always Included Shaders に明示登録する。

通信面は元の文字列で配置を確定する。欠落と位置ずれは文字メッシュに適用する。ラテン文字を全角の文字化けへ置き換えて折り返しが変わる経路を避ける。`CorruptionFor` の回復と解除は既存の値を読む。

本文と赤い残像の GameObject は active のまま保つ。表示だけを MeshRenderer.enabled で切り替える。TMP の OnEnable が頂点の濃度を上書きして全文を出す経路を作らない。

## 検証の入口

`tools/unity.ps1 menu intro` と `menu comms-preview` は本体と同じ描画を使う。比較前の画像は `Logs/design-rebuild-20260913/before/`。日本語のほか英語とフランス語も `menu text-audit -Set lang=...` で確認する。

Editor の代理パススルーは実機の見えを保証しない。Quest の装着時の立体感と現実との合成は実機で別途確認する。

`menu intro -Set frames=1` は各コマの実状態を `clean/frames/frames.tsv` へ保存する。実 `SoundCueLogic` に通して得た発火は同じ場所の `audio-cues.tsv` へ保存する。動画の音はその判定時刻へ DSP の 35ms を足して重ねる。実機の 3D 音響と毎回の音程の揺らぎはこの確認用動画には含めない。

### 0209の検証記録

EditMode の `FixedCamVr` は 1618 件通過。実メッシュは 2017 片。
Frame〜Swap は 123 コマ / 4.1 秒を再描画した。音の余韻を含む確認動画は 7 秒。
中央を保護した前案は `Logs/intro-full-field-20260913/before/` へ保存した。

合成前の alpha は `Assets/Screenshots/intro/alpha/` に保存する。
描画された終端矩形の内側を8画素縮めた範囲で測ると、p=.50 の中央の欠けは 27.178%。
p=0 の欠けは 0%。p=.84 の矩形内部の連続面は 100%。外側にも破片は残らない。
p=.999 では画面全体の alpha が閉じており、カメラ映像の後に現実が漏れない。

最初の修正では中央の隙間に管の燐光が残った。RGB最大値は 61/255。
修正後は 0/255。alphaだけでは検出できなかったため、RGBの検査も加えた。

30fps の実音判定は Frame 頭から Shatter=.066667 秒 / ScreenOn=2.466667 秒 /
Bell=3.666667 秒。各時刻へ DSP の .035 秒を加えて確認動画へ重ねた。
割れる音の終端から ScreenOn まで .700000 秒。音源そのものは0208のものを継続する。

根拠は `Logs/intro-full-field-20260913/` の `tests-final.xml` / `frames.tsv` /
`audio-cues.tsv` / `alpha-evidence.json` / `preview-evidence.json`。
HTML は `reports/2026-09-13_intro-fracture.html` を更新した。
Quest の装着と描画負荷は未検証。HTML のブラウザー自動確認は file URL 制限により未実施。

### 前案 a9b4633 の検証記録

以下は四辺の開口だった前案の記録。0208 の破片の検証結果として流用しない。

導入の Frame〜Swap が 123 コマ / 4.1 秒。通信の出入りと最大侵食と回復は 285 コマ / 9.5 秒。導入の代理静止画は 640×480。16:9 のスクリーンへ収めるため映像の左右には黒帯が出る。

`Logs/design-rebuild-20260913/calibrate_analyzer.py` で観測器を校正。正常描画と未描画と不正なメッシュ数を区別する。旧形式のログと途中で終わるログを含む 6 ケースが通過。

最終 EditMode は `--filter FixedCamVr` で 1612 件通過。`menu text-audit` の ja / en / fr はすべて規定内。通信本文は 1.20 度。行幅 0.438m / 枠幅 0.466m。根拠 XML とログは同じ Logs ディレクトリ。変更前後と動画を含む手元の資料は `reports/2026-09-13_presentation-redesign.html`。

### 前案0208の検証記録

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
