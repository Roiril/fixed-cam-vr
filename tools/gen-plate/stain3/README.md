# 2 周目 A：しみの 3 段（静止画だけ）

2026-11-12・`.claude/canon/LEDGER.md` 0330。動画生成は使わない。3 枚とも Codex の画像生成で作る。

| 段 | いつ | 画 | cue |
|---|---|---|---|
| 1 待機 | 2-A に入った瞬間 | 幕に顔のように見えるしみ | `cue_stain_A` |
| 2 押し始め | 長押しを始めた瞬間（切り替え）。押している間だけ | しみの所の布が、裏から顔を押し当てられて盛り上がる | `stain_A_press_bulge`（take の `markStartCueId`） |
| 3 報告の直後 | 報告が成立した瞬間から 0.4 秒 | しみは消え、幕の外側の縁から人の背丈の市松人形が半身で覗く | `stain_A_done_doll`（take の `markDoneCueId`・`markDoneSec`） |

報告が成立する前に離すと 1 へ戻る。3 のあとは乱れでライブへ戻り、その区間では二度と出ない。
エンジンの契約は `.claude/rules/show-run.md`（`markStartCueId` / `markDoneCueId`）。

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
