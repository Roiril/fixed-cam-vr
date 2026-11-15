# 2 周目 A：左の幕のしみと、右の幕の顔と人形

`.claude/canon/LEDGER.md` 0330（左のしみ）・0376・0378（右の幕の流れ）・0389（右の幕は動画生成）・0390（左の膨らみは無し）。
左のしみは Codex の静止画から、右の幕は動画生成（Flow）の動画から作る。

| 段 | いつ | 画 | cue |
|---|---|---|---|
| 0 待機 | 2-A に入った瞬間から、体が右の幕の顔に重ならない所（線 `s2a_rightstain`）を越えるまで | 左の幕に顔のように見えるしみ。長押ししても膨らまない（0390）。報告すると左のしみだけが消え、演出は続く（step の `markClear`） | `cue_stain_A` |
| 1 右のしみ | 0 の後、その 0.12m 先（線 `s2a_leftend`）に届くか、報告の長押しを始めるまで（step の `markStartAdvance`） | 何も無い右の幕に顔のしみが 0.25 秒のディゾルブで出る（step の `fadeInSec`）。左のしみが残っていれば残る | `cue_stain_A_right_hold`（掛け算）＋ 2 枚目の層 `cue_stain_A_left_hold` |
| 2 膨らんで覗く | 1 の後 | しみが案 1 の笑う顔に膨らみ、右の幕の縁から人形が覗く（2.29 秒の動画）。笑い声 1 回 | `stain_A_right_burst`（動画）＋ 2 枚目の層 |
| 3 止め | 2 の動画が終わってから、壁の端を回る（線 `s2a_walk`）まで | 最後のコマで止まる。顔と覗く人形 | `stain_A_right_watch` ＋ 2 枚目の層 |
| 4 ついてくる | 3 の後、区間を出て 3.5 秒まで（区間を出てもこの秒まではカメラ A の画のまま。take の `exitHoldSec`） | 乱れで CG の人形（0.8m）に替わり、幕の裏の同じ所から歩いた跡をついてくる。幕には顔だけが残る | `cue_stain_A_right_face` ＋ 2 枚目の層 ＋ CG `doll_mid`（trail） |

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

## 動画生成で試す（2026-11-15）

何もない所から人形ともっこりが出てくる所を動画生成で作る試し（未判定）。左のしみは別の層なので、入力には左に何も無い画を使う。

- 首尾フレームは `py -3.11 tools/gen-plate/stain3/video_inputs.py` が組む（生成ではない）。無人写真 × 右のしみの比の画の上に、`bulge.png` / `doll_laugh.png` / `doll_watch.png` の変えた所だけを戻す。
  出力は `Logs/gen-plate/stain2A_right_video_20261115/` の `A_stain`（右のしみだけ）・`B_burst`（伸び切った顔と笑う人形）・`C_watch`（隠れて見る人形）と、それぞれの黒帯つき 16:9（2560×1440）
- 1 本目は Flow の Omni 1.2 Flash・360p・6 秒、A → B。返った動画は `flow_v1_A_to_B.mp4`、4:3 に戻した版は `flow_v1_A_to_B_4x3.mp4`（写真の幅は 490px・x=75 から）
- 2 本目で決まった形（LEDGER 0389）: 最初のフレームは何も無い無人写真 `P_plain_16x9.png`、最後は案 1 の顔（裂けた笑み・丸い目）と笑う人形 `faces/grin_v3_last_16x9.png`。
  顔は Codex で `faces/p_common.md` ＋ `faces/p_face_grin.md`、入力は右のしみだけの画の 2 倍 `faces/in_stain2x.png` と参考 `faces/ref_grin.png`（`faces/run_faces.ps1 grin`）。
  前の伸び出しの顔（`bulge.png`）は「なんかリアルで変」。せり出しは、角まで出る版（`faces/v1/`）は出すぎ、浅く浮く版（`faces/v2/`）は控えめすぎ
- 返った動画 `flow_v2_plain_to_grin_4x3.mp4` に緩急を付けるのは `py -3.11 tools/gen-plate/stain3/retime_flow.py C_dissolve_pop`。
  しみが滲む途中を切り、何も無い画から膨らむ直前のしみ（元の 2.0 秒）へディゾルブで移り、顔を加速する。半分の速さには落とさない
- 本編の素材にするのは `py -3.11 tools/gen-plate/stain3/build_flow_right.py`（`--dry` で確認の画だけ）。最初のコマを無人写真へ 1 回だけ
  位置合わせし、全部のコマへ同じ写し方と色の合わせを掛けて、段 1 のしみ（比の画）・段 2 の動画・段 3 の止め・段 4 の顔だけの画を
  `static-inputs/stain_A_right_flow_*_<写真の日時>.*` に書く。いま台本が使う版の名前は `static-inputs/stain_A_right_flow.json`。
  確認の画は `Logs/gen-plate/stain2A_right_video_20261115/build/sheet.png`。
  台本へは `tools/show-apply-stain-right.py`。ツインの部屋の版は作っていない（ツインは前の版の素材を出す）

## 素材

本番は 2026-11-15 12:50 の無人写真 `captures/plate_A_20261115_125052.jpg` に合わせてある（`tools/web-compositor/plate-dependents.json` の `plate`）。

| 場所 | 待機 | 左のしみ（掛け算） | 盛り上がり（0390 から台本で使っていない） | 右の幕 |
|---|---|---|---|---|
| 本番 `tools/web-compositor/static-inputs/` | `stain_A_idle_20261115_1250.png` | `stain_A_ratio_20261113.png`・順の画 `stain_A_reveal_20261113.png` | `stain_A_bulge_20261115_1250.png`・press マスク `stain_A_pressmask_20261115_1250.png` | 動画生成の版 `stain_A_right_flow_*_20261115_125052.*`（0389。いまの版は `stain_A_right_flow.json`。前の版の `stain_A_right_*_20261115_1250*` は台本から外れた） |
| ツイン `tools/twin/assets/` | `twin_stainA_idle.png` | `twin_stainA_ratio.png` | `twin_stainA_bulge.png` | `twin_stainA_right_*` |

- 左のしみの姿（0143 で判定済み）は比の画が持っている。待機は無人写真にその比の画を掛けた画（生成ではない）
- 比の画 2 枚（左・右）は 9/24 の部屋で作ったものを 11-15 の写真にもそのまま使っている。11-15 の写真は手すりが 3〜5px 上がり、
  左のしみの垂れの先が x=277〜283 の 7 列で手すりに最大 6px 掛かる（比 0.96 以上の薄い所・暗い手すりの上なので見えない）。右は掛からない
- 盛り上がり・右の幕の伸び出し・笑う人形・見ている人形は、待機から生成し、変えた所の外は待機と同じ画素。合成範囲は左が `stain_A_press_leftmask_20260925.png`、右が `stain_A_right_mask_*`
- 報告直後の人形 `stain_A_done_doll`（`stain_A_doll_20261112.png`）は台本から外れているので 9/24 の部屋のまま
- 生成の生画像は `Logs/gen-plate/stainA_refit_20261115/`（11-15）・`Logs/gen-plate/stain3_20261112/`（9/24）

## 作り直す（部屋が変わったとき）

しみの形・人形の顔と着物・伸び出しの形は判定済みなので変えない。比の画はそのまま使い、部屋の画素を含む素材だけを
その日の写真から生成し直す。生成には前の版を参照に渡して姿を揃える。

1. 比の画をその日の写真に掛けて、しみが幕からはみ出さないか見る（手すり・幕の縁・合わせ目との距離を画素で測る）。
   はみ出すなら比の画を作り直す（`anomaly/face-stain.md`）。収まるなら、写真 × 左の比の画を待機（640×480）、
   さらに右の比の画を掛けた画を 2 倍（1280×960）にして作業フォルダの `stain.png` にする。待機の 2 倍は `idle2x.png`
2. 前の版の生成の生画像を作業フォルダへ `ref_left_bulge.png` / `ref_right_bulge.png` / `ref_doll_laugh.png` / `ref_doll_watch.png` として置き、
   プロンプトを書いて 4 本を 1 本ずつ流す（同時に流さない）

   ```powershell
   py -3.11 tools/gen-plate/stain3/refit_prompts.py <作業フォルダ> --doll-dx <右の幕の縁が動いた画素×2>
   & tools\gen-plate\stain3\run_refit.ps1 -Work <作業フォルダ>
   ```

   `refit_prompts.py` は元のプロンプトの後ろに `prompt-refit.md`（前の版に揃える段）を足す。`--doll-dx` は笑う人形の左右の位置
   （11-15 は右の幕の自由な縁が 9/24 より約 20px 右なので 40）
3. 左の盛り上がりは変えた所だけ待機の上へ戻し、press マスクを作る

   ```powershell
   py -3.11 tools/gen-plate/keep_base.py <待機.png> <作業フォルダ>\left_bulge.png <置き場>\stain_A_bulge_<日付>.png --box 0,0,330,480 --sheet <確認用.jpg>
   py -3.11 tools/gen-plate/bleed_maps.py --recipe tools/gen-plate/mask-recipes/bleed-stain-a-<日付>.json
   ```

   `--box` はマスクの中（左半分）。確認用の画は 元・生成・結果・残した範囲 の順に並ぶ。必ず開いて見る。
   レシピは比の画と順の画を作らず `reuse` に前の名前を書く（`show-apply-bleed.py` と `bleed_preview.py` がそこから読む）。
   膨らみの見え方は `py -3.11 tools/gen-plate/bleed_preview.py --only press`
4. 右の幕は下の「当日に作り直す（右の幕）」
5. cue の URL を新しい名前へ替える（卓サーバが動いていれば `GET /state` → `POST /state`。準備モードで弾かれたら卓を止めて
   `show.json` の `cues` だけ直し、起動し直す。`memory/show_json_is_live_config.md`）。`plate_A` / `plate_A_right` も同じ写真へ替え、
   `plate-dependents.json` の `plate` を書き換える

## 当日に作り直す（右の幕）

右の幕（しみ・膨らむ顔と人形の動画・止め・顔だけ）は Flow の動画 1 本から組む。写真を替えたら、まず位置合わせだけで組み直す。

1. **合わせ直す**（1 分ほど・外の回線は要らない）。卓の「素材 → 無人の写真」で、カメラ A の「2 周目 A の右の幕」の
   「この写真に合わせ直す」を押す。卓は採用中の写真で次を走らせ、台本の cue と `plate-dependents.json` の `plate` を新しい名前へ替える

   ```powershell
   py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate tools/web-compositor/captures/<採用中の写真>
   ```

   卓を使わずに走らせたら、台本へは `py -3.11 tools/show-apply-stain-right.py --apply`（卓を止めてから・`memory/show_json_is_live_config.md`）、
   `plate-dependents.json` の `stainARight` の行の `plate` は手で替える
2. **断られたら生成し直す**（終了コード 3。動画の最初のコマか元の写真が、採用中の写真と突き合わない。点 150 以上・残差 1px 以下が要る）。
   カメラの位置か幕の並びが変わっている。卓は断った時点で首尾フレームを
   `Logs/gen-plate/stain2A_right_video_20261115/build/flow_inputs_<写真の日時>/` に書く（`--flow-inputs` と同じ）
   - `start_16x9.png`（採用中の写真）と `end_16x9.png`（前の版の最後のフレームを新しい写真へ写したもの）を Flow に渡す。
     Omni 1.2 Flash・360p・6 秒、最初と最後のフレームを指定。中身は 0389 の C と同じ（何も無い幕にしみが滲み、顔が膨らみ、縁から人形が覗く）
   - `end_16x9.png` が無いときは、元の写真と突き合わないほど部屋が変わっている。最後のフレームを Codex で描き直す（上の「動画生成で試す」の顔の作り方。入力は `start.png`）
   - 返った動画（16:9 のまま）で組む。最初は `--dry` で確認の画 `build/sheet.png` を見る。段 1 のしみが出きっていなければ `--stain-at <秒>` を替える
     （元の動画では 2.0 秒。段 2 の区切りも同じだけずれる）

     ```powershell
     py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate tools/web-compositor/captures/<採用中の写真> --from-flow <返った mp4> --dry
     py -3.11 tools/gen-plate/stain3/build_flow_right.py --plate tools/web-compositor/captures/<採用中の写真> --from-flow <返った mp4>
     ```

     4:3 に切り出した動画は `flow/flow_<写真の日時>_4x3.mp4` に置かれ、`stain_A_right_flow.json` の `source` に残る。以後の合わせ直しはその動画から組む
   - 台本へは、卓でもう一度「この写真に合わせ直す」を押す（新しい動画から組み直し、cue と台帳を替える）
3. どちらでも、最後に焼き直す（`.\tools\unity.ps1 build fixedcam` → `py -3.11 tools\quest-fleet.py sync`）

## 罠

- 別々の生成を同時に走らせない（結果が入れ替わる。`memory/codex_image_pipeline.md` §4）
- 前の版を参照に渡すと、生成が部屋を縮めて描き直すことがある（11-15 の右の幕 3 枚: 下へ行くほど上へずれ、床で約 38px）。
  そのまま差の塊を戻すと手すりが 2 本に見える。`build_right_burst.py` は変えていない所の正方形を突き合わせて 3 次の多項式で
  待機の座標へ引き戻してから組む（`register`。残差の中央値が出力に出る。1〜2px なら合っている）。伸び出しは手すりの上端で切り、
  手すりとそこから下は待機の画のまま（`above_rail`）。組んだら手すりの所を 4 倍に拡大して 1 本か見る
- 人形は布の裏に立たせる。幕の縁が体を手前から隠していないと、貼り付いて見える
- 待機のしみは 0143 で「顔ははっきり見えないけど怖い方がいい」と判定されている。盛り上がりの画に合わせて待機を濃くしない
