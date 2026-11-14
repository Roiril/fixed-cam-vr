# 2 周目 A：左の幕のしみと、右の幕の裏の人形（静止画から）

`.claude/canon/LEDGER.md` 0330（左のしみ）と 0376・0378（右の幕）。動画生成は使わない。素材は全部 Codex の画像生成の静止画から作る。

| 段 | いつ | 画 | cue |
|---|---|---|---|
| 0 待機 | 2-A に入った瞬間から、体が伸び出しの顔に重ならない所（線 `s2a_rightstain`）を越えるまで | 左の幕に顔のように見えるしみ。長押しの間は顔の所が盛り上がる。報告すると左のしみだけが消え、演出は続く（step の `markClear`） | `cue_stain_A`（盛り上がりは take の `markStartCueId` = `stain_A_press_bulge`） |
| 1 右のしみ | 0 の後、その 0.12m 先（線 `s2a_leftend`）に届くか、報告の長押しを始めるまで（step の `markStartAdvance`） | 右の幕に顔のしみが 1 秒で滲む（step の `fadeInSec`）。左のしみが残っていれば残る | `cue_stain_A_right_hold`（掛け算）＋ 2 枚目の層 `cue_stain_A_left_hold` |
| 2 右で笑う | 1 の後 | 次のコマで顔が布ごと左へ画面の半分まで伸び切った画に替わる（途中は無い）。右の幕の縁の裏で人形が笑う。笑い声 1 回 | `stain_A_right_burst`（動画）＋ 2 枚目の層 |
| 3 隠れて見る | 2 の動画が終わってから、壁の端を回る（線 `s2a_walk`）まで | 人形は布の縁の裏へ身を引き、黙ってこちらを見ている | `stain_A_right_watch` ＋ 2 枚目の層 |
| 4 ついてくる | 3 の後、区間を出るまで | 乱れで CG の人形（0.8m）に替わり、幕の裏の同じ所から歩いた跡をついてくる。右のしみは残る | `cue_stain_A_right_hold` ＋ 2 枚目の層 ＋ CG `doll_mid`（trail） |

段 0 で左のしみを消していれば、以後の段の 2 枚目の層は出ない。段 1 の報告は押し始めで段 2 へ進み、押し切ると段 2 で畳まれる。
段 2〜4 の報告は演出ごと畳む（左右のしみも人形も消える）。そのとき 2 周目 B の演出 `L2C1#0` は出ない（take の `skipIfDismissed`）。
台本へ当てるのは `tools/show-apply-stain-right.py`。エンジンの契約は `.claude/rules/mark-advance.md`。

## 右の幕（0376）

| 生成 | 入力 | プロンプト |
|---|---|---|
| `stain.png` | 待機の 2 倍 | `prompt-right-stain.md` |
| `bulge.png` | `stain.png` | `prompt-right-bulge.md` |
| `doll_laugh.png` | `stain.png` と資料 `tools/doll-ref/out/gen_standing.png` | `prompt-right-doll-laugh.md` |
| `doll_watch.png` | `doll_laugh.png` | `prompt-right-doll-watch.md` |

- 順に流すのは `tools/gen-plate/stain3/run_chain_right.ps1 -Name real|twin`（入力の `base2x.png` と出力は `Logs/gen-plate/stain2A_right_20261115/<Name>/`）
- 組むのは `py -3.11 tools/gen-plate/stain3/build_right_burst.py real|twin`。差の塊だけを待機の上へ戻し、
  伸び出しは動かさない（0377）。シミだけの画 → 伸び切った `bulge.png` → シミだけの画を、どれも 1 コマで替える（`T_BULGE`）。人形は笑っている画から始まり、1.9 秒で 1 コマで見ている画へ替わる（重ねると 2 体に見える）。全体 2.375 秒
- 人形の大きさは床の点からの投影で決めてプロンプトに書いた（笑う: (0.5, 0.65)・見る: (0.44, 0.62)・背丈 0.8m）。
  CG の人形は見ている点から出る（台本の `trailFromX` / `trailFromZ`）。組んだ後 `*_right_burst_*.json` の `dollWatchBox` で位置を確かめる
- 確かめる画は同じフォルダの `sheet_frames.png`（8 コマ）と `sheet_masks.png`（赤 = 右のしみ・緑 = 伸び出し・青 = 人形）
- 右のしみだけの掛け算の比の画 `stain_A_right_ratio_<TAG>.png`（ツインは `twin_stainA_right_ratio.png`）とその枠のマスクも同じ組み立てが書く（右のしみの塊を 4 画素膨らませて 3 画素ぼかした範囲）

## 素材

| 場所 | 待機 | 盛り上がり | 人形 |
|---|---|---|---|
| 本番（9/24 の部屋）`tools/web-compositor/static-inputs/` | `stain_A_idle_20261112.png` | `stain_A_bulge_20261112.png` | `stain_A_doll_20261112.png` |
| ツイン `tools/twin/assets/` | `twin_stainA_idle.png` | `twin_stainA_bulge.png` | `twin_stainA_doll.png` |

- 本番の待機は `captures/gen_stainA_still_20260924.png` と同じ画（0249 の動画の先頭コマの元）。しみの姿は変えていない
- 3 枚は同じ 1 枚（待機）から作り、変えた所の外は待機と同じ画素。合成範囲は従来の左半分マスク `stain_A_press_leftmask_20260925.png`
- 生成の生画像は `Logs/gen-plate/stain3_20261112/`

## 作り直す（部屋が変わったとき）

1. 待機の画を用意する（その日の無人写真に `anomaly/face-stain.md` でしみを足す。640×480）
2. 待機を 2 倍（1280×960）にして、`prompt-bulge.md` と `prompt-doll.md` を 1 本ずつ流す。人形は資料 `tools/doll-ref/out/gen_standing.png` を 2 枚目に渡す

   ```powershell
   & "$env:USERPROFILE\.claude\scripts\codex-run.ps1" -PromptFile tools\gen-plate\stain3\prompt-bulge.md -Mode image -Images <待機の2倍.png> -OutImage <出力>\bulge.png
   & "$env:USERPROFILE\.claude\scripts\codex-run.ps1" -PromptFile tools\gen-plate\stain3\prompt-doll.md -Mode image -Images <待機の2倍.png>,tools\doll-ref\out\gen_standing.png -OutImage <出力>\doll.png
   ```

3. 生成は部屋を少し描き直し、寸法も変わる。変えた所だけ待機の上へ戻す

   ```powershell
   py -3.11 tools/gen-plate/keep_base.py <待機.png> <出力>\bulge.png <置き場>\stain_A_bulge_<日付>.png --box 0,0,330,480 --sheet <確認用.jpg>
   py -3.11 tools/gen-plate/keep_base.py <待機.png> <出力>\doll.png  <置き場>\stain_A_doll_<日付>.png  --box 0,0,330,480 --sheet <確認用.jpg>
   ```

   `--box` はマスクの中（左半分）。確認用の画は 元・生成・結果・残した範囲 の順に並ぶ。必ず開いて見る
4. 卓の cue の `sourceUrl` を新しい名前へ替える（卓サーバが動いていれば `GET /state` → `POST /state`。`memory/show_json_is_live_config.md`）

## 罠

- 別々の生成を同時に走らせない（結果が入れ替わる。`memory/codex_image_pipeline.md` §4）
- 人形は布の裏に立たせる。幕の縁が体を手前から隠していないと、貼り付いて見える
- 待機のしみは 0143 で「顔ははっきり見えないけど怖い方がいい」と判定されている。盛り上がりの画に合わせて待機を濃くしない
