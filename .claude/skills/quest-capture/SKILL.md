---
name: quest-capture
description: Quest 実機の「見ている絵」を動画・静止画で取り出す。見た目に関わる変更（演出・シェーダ・パススルー・HUD・合成）を実機で確かめたい時、「実機でどう見えるか見せて」「動画で見たい」と言われた時、ログでは OK なのに体験が正しくない疑いがある時に呼ぶ。HMD を被らずに撮れる。
---

# Quest の実機画面を動画で見る

**ログは「段が進んだ」を出すが「画に何か出た」は出さない。** 2026-07-31 に、テレメトリで
FAIL ゼロ・演出 7 本 OK と判定された走行の画を録って見たら、**導入演出が 1 段も出ていなかった**
（30 秒ずっと素のパススルー）。原因 3 件はいずれも「見た目の実体が実機で死んでいた」で、
論理は正常に進んでいた。**見た目を変えたら画を録る。**

## 使う

```bash
python tools/quest-record.py --sec 45 --walk    # 自動走行させながら録る（導入演出の確認）
python tools/quest-record.py --sec 30           # 通常起動で録る
python tools/quest-record.py --raw <file.mp4>   # 既にある録画を変換するだけ
```

機を指定しなければ `quest-fleet.py pick` が選ぶ。**HMD を被らずに導入から本編まで通して撮れる**。
**走行 1 回で次が全部そろう**（`--no-log` / `--no-evidence` で外せる）:

| 出るもの | 何 |
|---|---|
| `logs/capture/<日時>_eye.mp4` | 片眼・H.264（変換前の生録画も残る） |
| `logs/capture/<日時>_meta.json` | 録画開始の壁時計とカメラの健康。**これがあると画とログの対応が推定でなく実測になる** |
| `logs/capture/<日時>_xp.log` | 今回の走行ぶんだけに絞った logcat |
| `logs/capture/<日時>-report.md` | `analyze-xp-log.py` の判定 |
| `logs/evidence/<日時>/` | 各段・各演出の**フル解像度 PNG** + `index.md` |

最後に 1 画面のまとめが出る（判定の FAIL/WARN/OK 件数・各出力のパス・**走行前後のカメラの
`/health` 比較**）。ISO が 2 倍以上動いていたら「部屋の明るさが変わった」と言うので、
映像が暗くなった原因を自分の修正と取り違えずに済む。

1 枚だけ欲しいなら静止画の方が速い（`exec-out` は VR モード中でも通る）:

```bash
adb -s <serial> exec-out screencap -p > shot.png
```

## なぜ変換が要るか

`adb shell screenrecord` が撮るのは**コンポジタが出力した最終フレーム**:

- 両眼が横に並ぶ（左半分＝左眼 / 右半分＝右眼）
- 各眼の視野が **40 度ほど回転した台形**（レンズ用の逆歪みが掛かった状態）。
  HMD ではレンズがこれを打ち消すので、生のままでは「見ている絵」にならない

## ⚠⚠ 被っていない間は、アプリが描いたものしか撮れない（2026-08-01 実害・40 分溶かした）

**HMD が頭に乗っていないと（`mProximityPositive=false`）、`screencap` / `screenrecord` は
アプリ自身のアイバッファしか拾わない。システムの絵もパススルーも黒で返る。**

確かめ方は 10 秒: アプリを止めて**ホーム画面**を撮る。

```bash
adb -s <serial> shell am force-stop <pkg>; sleep 5; adb -s <serial> exec-out screencap -p > home.png
```

ホームは必ず何か表示しているので、これが**真っ黒なら撮影系が死んでいる**（実測: 910 万画素すべて 0 /
`mScreenState=ON` なのに）。この状態の録画から見た目を語ってはいけない。

- 近接センサーを止める旧来の手（`am broadcast -a com.oculus.vrpowermanager.prox_close`）は
  **Quest 3 では効かない**（`result=0` で状態が変わらない）
- 「以前はパススルーが映った」録画があっても根拠にならない。2026-07-31 に映っていたのは
  **カメラ背景が alpha 0 でアプリのバッファが素通しだった**ため、システムのパススルーが
  アプリのアイバッファ経由で入っていたもの。背景を不透明へ戻した現構成ではその経路が無い
- **判定できないことを「出ていない」と読むと、実在しない不具合を追うことになる**（この日それをやった）

### 被らずにパススルー絡みの見た目を測る（VeilAlignmentProbe）

現実の上に何が重なるかは撮れないが、**アプリが alpha をどう書いたかは撮れる**。
[`VeilAlignmentProbe`](../../../Assets/Scripts/Diagnostics/VeilAlignmentProbe.cs) は
カメラ背景を明るい灰（alpha は 1 のまま）にするので、**覆いが alpha 0 にした領域が
「明るい面に空いた黒い矩形」として写る**。そこへスクリーンの外周を線で重ねれば、
枠とスクリーンのずれが 1 枚の画で測れる。あわせてリグのヨーを台形波で振り、
`頭の向き − 画面の向き` の残差を 0.25 秒ごとに吐く（追従が正面で止まるかの判定）。

```bash
adb -s <serial> shell am start -e veilprobe 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity
# 段 4（枠が閉じ切って passthrough=1 のまま）を狙って撮る
adb -s <serial> exec-out screencap -p > frame.png
adb -s <serial> logcat -d | grep "VeilProbe"
```

フラグが無ければ何もしない（Development ビルド専用）。`-e xpwalk 1` と併用すると導入まで自動で進む。

片眼を切り出し、台形の 4 隅を矩形へ戻す（ホモグラフィ）。中央はほぼ見たまま、周辺は樽型歪みが残る。

## ⚠ 画の見方を間違えると「出ている」ものを「出ていない」と読む（2026-07-31 実害）

同じ日に **2 回**「画に出ていない」を踏み、2 回目は見つけるまでに次を全部踏んだ。

- **一覧グリッドに縮小したら消えた。** 1cm 幅の線が 2m 先にあると視角 0.3 度で、
  縮小すると 1〜2 画素になって潰れる。グリッドでは「0 本」に見えていたが、実際は出ていた回もある。
  → **判定はフル解像度でする。**グリッドは「どこを見るか」を決めるためだけに使う
- **段が短い。** 構造の線が出るはずの段は実測 **1.1 秒**しかなく、数秒刻みでは素通りする。
  → **まずログで段の長さを確かめ**（`[XP] ev=intro stage=` の時刻差）、その中を 0.2 秒刻みで切る
- **録画時刻とログ時刻がずれる。** `[XP] t=` はアプリ起動からの経過、録画はその数秒後に始まる。
  実測で **約 2.8 秒**ずれていた。目分量で合わせると別の段を見てしまう
- **1 回直して録って満足した。** 1 回目の修正後の録画にも 2 回目の不具合は写っていた

→ これらは [`tools/xp-evidence.py`](../../../tools/xp-evidence.py) が機械化してある。
ログの壁時計と録画開始時刻から**オフセットを計算**し、各段をフル解像度で切り出す。
`quest-record.py` が走行後に自動で呼ぶので、**`logs/evidence/<stamp>/index.md` の一覧を見て、
気になる段はフル解像度の PNG を開く**。

**警告が出ていないことは「出ている」の証拠にならない。** 2 回目の不具合はマテリアル解決も配置も
成功していて、実機ログに 1 行も警告が出なかった。`[XP]` の `veil=` / `wire=` / `pt=` / `bg=` は
これを機械で捕まえるために足したもの（[onsite_experience_test](../../memory/onsite_experience_test.md)）。
**厳密なレンズ補正ではない**が、演出の流れ・色・枠の閉じ方・タイミングを見るには足りる。

## ⚠ 罠（全部 2026-07-31 に実際に踏んだ）

| 罠 | どうなるか | 対処 |
|---|---|---|
| **録画を先に始めてからアプリを起動する** | VR モードへ入るときの画面モード変更で**録画が黙って止まり 0 バイト**。エラーも出ない | **アプリを先に起動**し、VR モードが安定してから録り始める（スクリプトは 5 秒待つ） |
| **H.264 は黒を 0 にしない**（16 前後） | 輪郭検出の閾値が低いと画面全体を拾い、**視野が黒枠の中に小さく収まった動画**になる | 閾値 28（`DARK_LEVEL`）。screencap（無圧縮）と screenrecord で最適値が違う |
| **角度ソートの開始点は視野の回転量で変わる** | 同じコードで **90 度回った動画**ができる | 「元画像でいちばん左上の隅」を出力の左上に固定する |
| **導入は真っ黒から始まる** | 先頭フレームで 4 隅を検出すると失敗する | いちばん明るいフレームで検出し、全フレームに使い回す（視野は動かない） |
| **`adb pull` が scoped storage で 0 バイト**（`/sdcard/Android/data/<pkg>/`） | 黙って空ファイルができる | `adb exec-out cat <path>` で取る。`/sdcard/` 直下は pull できる |
| **Git Bash が `/sdcard/...` を Windows パスへ変換する** | `failed to stat remote object 'C:/Program Files/Git/sdcard/...'` | `MSYS_NO_PATHCONV=1` を付ける（Python の subprocess から直接 exec するなら不要） |
| **PreToolUse hook が `adb shell rm -f /sdcard/xxx` をルート削除と誤検知** | ブロックされる | そもそも screenrecord は上書きするので削除は要らない。消したいなら別の書き方を探す |

## 内蔵録画は adb から起動できない

Quest のシステム録画（`/sdcard/Oculus/VideoShots/`）は **1920x1080・片眼・正立・歪み補正済み**で、
見た目としてはこちらが理想。ただし **adb からは起動できなかった**（システム権限が要る）:

- `am broadcast -a com.oculus.vrshell.intent.action.START_SPATIAL_CAPTURE` → result=0
- `am broadcast ... LAUNCH -e uri systemux://capture` → result=0
- `input keyevent 130`（MEDIA_RECORD）→ 無反応

**人が被って手で録るならそちらが上。自動で撮るならこのスキル。**

## 撮った後

`[XP]` だけでなく**他のタグの警告も読む**。今回の 3 件はすべて実機ログに出ていたのに、
テレメトリだけ見ていて気づけなかった:

```bash
grep -iE "見つかりません|Failed to|Error|Exception" <log>
```

画と時刻を突き合わせるには、録画と `logcat` を同時に取り、`ev=intro` などの壁時計で対応づける
（Unity の `t=` はアプリ起動からの秒数で、録画開始とはずれる）。

## 関連

- [.claude/memory/onsite_experience_test.md](../../memory/onsite_experience_test.md) — 体験そのものの検証（[XP] テレメトリ / 自動走行 / 判定）
- [.claude/memory/quest_fleet_two_devices.md](../../memory/quest_fleet_two_devices.md) — 2 台運用・機ごとに違う 4 つ
- [adb-logcat](../adb-logcat/SKILL.md) — ログの層別取得
- [quest-build](../quest-build/SKILL.md) — ビルドと配布
- [.claude/rules/visual-verification.md](../../rules/visual-verification.md) — 「壊れている」と判断する前に多角度で確かめる作法
