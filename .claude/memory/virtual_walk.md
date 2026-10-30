---
name: virtual_walk
description: 2026-10-14。PC の仮想体験（NPC が歩き、実プレート＋CG 合成の映像が実シェーダ・実 Director を通る）で演出を前後比較する手順と、その計器の限界
metadata:
  type: project
---

2026-10-14 ユーザー指示「動作検証は、PCで仮想的な体験環境を作って、その中でNPCを歩かせてその視点から見てチェックするように」で作った。

**回し方**: `.\tools\unity.ps1 menu virtualwalk -Set laps=1,tag=before`（PowerShell では `-Set` を**引用しない**。
`"a=1,b=2"` と引用すると 1 本の文字列になり、`laps` が読めず既定に落ちる）→ `py -3.11 tools/virtualwalk-video.py <dir>`
→ 変えたら `tag=after` で同じく → `--compare <before> <after>`。

**歩行の契約**: `WalkNavigation` が `layout.room.walls` の厚みと `props` の回転・高さを読み、体半径＋壁からの余白を
全移動線分で検査する。候補経路の半径は立位の全メッシュ頂点から測り、1cm単位で切り上げる。
壁からの余白の既定は 0.04m。`radius` で立位の実寸より小さい値を指定すると失敗する。
歩行時は別に、現在の全身をCPUスキニングしてXZ凸包を取り、前後の姿勢を囲む凸包と壁・備品・境界の距離も90Hzで検査する。
この全身の掃引に「指定した余白＋5mm」がなければ走行を失敗させる。旋回前には足を揃える時間を取る。
到達不能なら失敗させる（直行で代用しない）。
開始 A も停止点に含む。角では減速し、体が進行方向へ向いてから歩く。`route=forward` は順方向、
`route=reverse` は逆方向、`route=backtrack` は A→B→A→C→A の引き返し。`route=all` は3経路を1回のEditor起動で独立して走らせる。
身長は `Renderer.bounds` で決めず `VisitorAvatar.MeasurePoseHeight` の現在CPU頂点で合わせ、立位1.65mとの差5mm以内を検査する。

**歩行範囲**: grid はカメラ割当であり障害物境界ではない。検証専用に `padding=0.5`（各辺へ 0.5m）の外縁を
足し、体半径ぶん内側へ制限する。現場の 1.8m 四方だけでは体幅を取った外回りが成立しない。
`padding=0` はその条件で失敗することを確認する検証。会場の通行可能性を保証する値ではない。
`radius`・`margin`・`padding` は明示設定でき、使用値を `navigation.json` に保存する。

**色と歩行**: 検証専用 `VisitorAvatar` が Remy の埋込み色画像を使い、保存済みの Mixamo Walking を
基準姿勢と骨長の差を補正して再生する。位相は移動距離から決める。本番の Remy prefab・材質は変えない。
抽出元と生成手順は `Assets/Editor/VirtualWalkAssets/`。本番 APK へ検証専用資産を同梱しない。

**証拠の置き場**: `G:\マイドライブ\shubie\renders\virtualwalk\<日時>_<tag>\` へ直接保存（`out` で変更可）。
従来の体験者視点 `walk.mp4` に加え、立体の `observer.mp4` と真上の `overhead.mp4` を残す。
水色の円は体半径、線は経路。黄の短い線は体の前方、外周線は指定した検証範囲。
`frames.tsv` に速度・進行方向との差・立位包絡と障害物の距離・全身掃引の余白・外縁距離・アニメ時刻も保存する。
`virtualwalk-video.py` は画像の全コマの実在を確かめ、壁との余白と移動方向を数値で判定してから動画化する。

**何が本物か**: 背景は各カメラの実プレート（`StreamingAssets/show/assets/plate_*.jpg`）、人は Remy を本番の CG 層
（較正カメラ・壁の遮蔽・contain 枠・レンズ）で重ねる。ScreenComposite・CameraSwitchDirector・GlitchFx・ZonePickLogic・
YawFollowLogic・OutroLogic・OutroReport は実物。刻みは 90Hz（0.07s の暗転は 30fps だと 2 コマで緩急が写らない）。

**限界**: 圧縮・伝送の痩せ・遅延・両眼視差・実機ディスプレイは入らない。シェーダの `_Time.y`（砂嵐・点灯）は進まない。
0236 でユーザーは「Editor はすごくいい、Quest の生録画は微妙」と判定している — **見た目の合否は実機で出す**。
ここで見るのは時刻・順序・曲線・重なり（why.md「代理を厚くしない」）。

**計器の罠**:
- `Renderer.bounds` で1.65mに合わせると、実CPU立位では約8%小さいモデルになった。見かけの境界にはアニメ用の余白があり、縮尺の計器にはしない。
- Remy の歩行元モデルは、アニメ付きで Instantiate すると初期姿勢が最初の歩行フレームになる。
  そこから相対回転を取ると腕下げが消え、Tポーズで歩く。`WalkingBind.fbx` は同じFBXを `importAnimation: 0` で読み、真の静的基準にする。
- EditMode の描画前の `BakeMesh` は古い骨行列を測り得る。足底・半径・凸包は元頂点/weights/bindposesと現在のbone行列からCPUで測る。
  Tポーズの大きな半径と立位の半径が違うことを校正し、歩行中のフル解像度PNGも見る。
- 保存写真の可視候補は実頂点を投影する。全身のworld AABBの空の四隅や、足先の長さを頭にも当てた円柱で拒否しない。
  候補がゼロなら位置だけを動かし続けず、実形状・向き・歩行位相と条件を照合する。絵のラベルを別の姿勢へ付け替えない。
- 終幕の電源断を試した後は `_ScreenPower` / `_ScreenCollapse` / `_ScreenNoise` を復元する。
  カメラごとに元無人写真と描画結果の輝度相関・非黒を先に測る。GPUマスクの画素数だけでは背景が真っ黒でも成功に見える。
- `OutroLogic.ScreenCollapse` は潰れ終わると 0 に戻る（電力 0 が隠す設計）。記録をそのまま見ると「逆戻り」に見える。
  記録は段を過ぎたら 1 にしてある
- 1 周目のゾーン切替の乱れは強さ 0.113（0.25 × 段階の倍率）で、乱れの音の閾値 0.15 に届かない＝鳴らない。設計どおり
- 乱れの見た目を変えるなら `glitchstills=1` で強さ 0.32 / 0.6 を揃えた静止画を前後で撮る（0055 の育ち方を崩さないか見る）

**エージェントスクリーン（`comms=1`・2026-10-14 追加）**: `-Set laps=1,dwell=6,comms=1,takeover=17,tag=x`。
実物の `CommsPanel` を Awake して `Apply` を毎刻呼ぶ。台本は ①（1 行）→ 2s 読ませた終わりに①b（3 行・枠の高さが変わる組）
→ ①b を読ませている途中で報告を押す（Guide）→ 1 秒で②→ 17s で乗っ取り。`comms.tsv`（90Hz の濃さ・字数・文面の頭 4 字・
枠の高さ）が出る。`py -3.11 tools/virtualwalk-comms.py [--compare <before> <after>]` が差し替えと押した瞬間の
見た目の落ち込み・見えたままの入れ替わり・枠の跳ねを判定し、前後のシートを焼く。
- ⚠ 修正前のコードで撮り直すときは、編集中の本体とテストを退避して HEAD に戻す（新しい API を使うテストが残ると
  コンパイルが落ちて走らない）。終わったら必ず戻す
- ⚠ 押した瞬間は NPC の向きで面が画の外に出ることがある。前後で押す時刻がずれると画で比べられないので、数値で比べる
- ⚠ 乗っ取り中の主画面上部の白い砂粒は**実行ごとに変わる**（同じコードの 2 回でも違う）。前後の画素比較では
  その領域を除くと一致する（2026-10-14: 数値 676 行は完全一致）

関連: [[sound_pipeline]] / [[sim_device_divergence]]

**保存写真の合成・終幕検証**: `.\tools\unity.ps1 menu plate-validation -Set stills=1`。
`G:\マイドライブ\shubie\renders\plate-validation\<日時>\` に、A〜Cそれぞれの中央/左右の立位・実歩行の追加例・壁遮蔽・最後の対面を出す。
元JPEG、壁/床wire、入力と描画の相関、CPU全身の画内/床/壁/相互距離、補正前の人形足底、GPU可視率を `meta.json` とPNGで対にする。
通常の可視例と意図的遮蔽例は混同しない。最後は本番の配置/姿勢/撮影GPU処理・解除表示・電源断・写真入りOutroReportを再利用する。
写真の成功後の30fps連番は `ending-animation/`。退場中の実位置・両距離・顔のdot・全身壁余白・実速度も `ending-gates.tsv` に残す。
解除表示の代表コマは報告前だけで終えず、`MarkCleared`への本番同様の切替後も保存する。
`recovery.tsv` のreleased=True・residue=0・tear=0と、そのコマの画像を照合する。

**2026-10-30の歩行証拠**: `G:\マイドライブ\shubie\renders\virtualwalk\20261030_222807_verified-height_{forward,reverse,backtrack}`。
CPU立位身長1.650m、立位半径.385m、経路半径.39m、壁余白.04m、実全身掃引の最小余白.054/.050/.052m。
前の8%小さいRenderer.bounds基準の出力は合格証拠に用いない。
最終の絵付き報告: `G:\マイドライブ\shubie\reports\2026-10-30_virtualwalk-ending\validation.html`（画像/動画を内包）。
保存写真と終幕の合格出力: `G:\マイドライブ\shubie\renders\plate-validation\20261030_224438_945`。
`20261030_220646_973`以前は解除後の代表静止画が欠けるため、報告の主証拠はこの最終出力を使う。
