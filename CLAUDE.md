# fixed-cam-vr

Meta Quest 3 の作品。固定視点カメラ（バイオハザード風）の無線映像を VR 内スクリーンへ出し、
実写に CG を合成して見せるホラー体験。**実装は終わっている。ここから先は作りこみの段。**

## ⚠ 企画書は読まない（2026-08-08 ユーザー宣言）

**`docs/proposal/` と `docs/archive/` はどちらも開かない。**

> 「ベースの実装はできたので、この後は作りこみの段階なので企画書を読まないでほしい。企画書はあくまで
> 企画段階で、今後は企画書を超えた、体験としての世界観、物語性、企画書に無い演出などを入れて
> 作りこむ必要があるので、企画書を読むのはコンテキスト汚染になる」

**骨格の制約は機械が守っている**ので、文書を読み直す必要が無い — 導入 → 3 区間を 3 周 → 終幕、
3 分以内、1 周目の録画を 3 周目の背景に、100ms の管理。これらは実装済みで、
`tools/analyze-xp-log.py` が `show.json` を期待値として突き合わせる。

**骨格は機械が守る。人は絵と物語を作る。** 思想は `.claude/reference/why.md`（先に読むのはこれ 1 本）。

## 作りこみの回し方

動画生成用の画像は元の画角・縦横比で生成した後、コードで左右に黒い余白を追加して16:9にする（最初・最後のフレームも毎回）。画像生成AIには黒帯を描かせない。切り抜きや縦横比を変える引き伸ばしは禁止。黒帯付き版を主リンクで渡し、寸法と両側の黒帯を実測する。詳細は [memory/codex_image_pipeline.md](.claude/memory/codex_image_pipeline.md) の「動画生成用の入力画像」。

| やること | 入口 |
|---|---|
| 1 周回す（予測 → 安い門 → 焼く → 見る → 台帳） | `skills/work-round` |
| ユーザーに見せて赤入れをもらう | `skills/direction-round` |
| 生成 AI で差し替え素材を作る（人形・染み・手形） | [tools/gen-plate/](tools/gen-plate/README.md)（**先に [memory/codex_image_pipeline.md](.claude/memory/codex_image_pipeline.md)**） |

| 音を触る（BGM・効果音・環境音・遷移） | `rules/sound-design.md`（**先に読む**） |

**世界観の正本は `.claude/canon/`。**

- `LEDGER.md` — ユーザーが口にした判定。**逐語**。要約したら値が消える
- `OPEN.md` — まだ聞いていないこと（既定つき）と、**シュビーの案（未判定）**
- `ROUNDS.md` — 焼く前の賭けと予測、焼いた後の差。初見の配給

⚠ **発案は自由。昇格は禁止。** シュビーの世界観の案を `LEDGER.md` へ入れてよいのは、
ユーザーが口にしたときだけ。沈黙は同意ではない。規律は `rules/canon-boundary.md`。

## ⚠ 同居 3 アプリ — 作業前に必ずどれか確定する

1 プロジェクトに**独立した 3 アプリ**が同居している。ユーザーは下の呼称で指す。
**正式名だけでは導けない**（「ハンド」が 2 つある）。

| ユーザー呼称 | 正式名 | namespace / asmdef | コード root | シーン |
|---|---|---|---|---|
| **fixedcam** / 廻リ視 / 本体 / カメラ | 廻リ視（FixedCam） | `FixedCamVr.*` | `Assets/Scripts/` | `Main.unity` |
| **ハンド** / 手 / テーブル / TableDuo | TableDuo（手アバター調査） | `TableDuoVr.*` | `Assets/TableDuo/` | `TableDuoMain.unity` |
| **ロボットハンド** / mycobot / テレオペ | MyCobotHand | `MyCobotHandVr` | `Assets/MyCobotHand/` | `HandTeleop.unity` |

**作りこみの対象は廻リ視だけ。** 他 2 つは別の目的で動いている。

分離規約（asmdef 相互参照禁止・共有資源・ビルド逐次・並列化可否）は
**[rules/parallel-projects.md](.claude/rules/parallel-projects.md) が正本**（Unity を触る前に読む）。

## Unity は CLI で操作する

**[tools/unity.ps1](tools/unity.ps1) が `unity.exe` を叩く唯一の場所。** 他所で直接呼ばない。

```powershell
.\tools\unity.ps1 doctor          # 前提が揃っているか（最初にこれ）
.\tools\unity.ps1 build fixedcam  # → Builds/mawarimi.apk
.\tools\unity.ps1 test
.\tools\unity.ps1 menu            # Editor の機能を CLI から呼ぶ（引数なしで一覧）
.\tools\unity.ps1 menu scene      # 例: Setup Main Demo Scene（ビルド前に必須）
```

- **Editor のメニューは `menu` から全部呼べる**（2026-08-09〜）。Setup 6 種・プレビュー / 検証 18 種。
  値を渡すものは `-Set key=value`（例 `menu actor -Set actor=Ichimatsu`）。
  一覧に無いものは `menu raw:<完全修飾名>`
- **GUI でしか動かないのは 5 つだけ**（シーンを開く / テストを開く / フォルダを開く / Scene ビューを動かす /
  ウィンドウ配置）。どれも batchmode に画面が無いためで、代わりの経路は `menu` の一覧に出る
- Editor を GUI で開いて手で触る前提の手順を既定にしない
- **MCP for Unity は退避路**。CLI が届かないのは「起動中の Editor をライブ操作する」場合だけ
  （Pipeline は Unity 6.0 以上で、2022.3 のこのプロジェクトには入らない）。
  現状この PC に MCP は未登録。要るときはグローバルの `unity-editor-status` スキルから入り、
  このプロジェクト固有の罠は [.claude/reference/mcp-unity.md](.claude/reference/mcp-unity.md)
- 一般則は `~/.claude/reference/unity-cli-ops.md`

## 検証

**3 経路を突き合わせる。どれか 1 つでは切り分けられない。**

| 経路 | 何を見る | 出どころ |
|---|---|---|
| A 自己申告 | 体験が著作どおり起きたか | `[XP]` テレメトリ → `analyze-xp-log.py` |
| B 外部の実測 | 画に何が出ていたか | `quest-record.py --walk` → `xp-evidence.py` の PNG |
| C 例外 | Unity の例外・警告 | 実機 logcat |

- **`py -3.11 tools/quest-record.py --sec 45 --walk` の 1 回で A・B・C が揃う**（HMD 不要）。
  依存は `tools/requirements.txt`
- ⚠⚠ **機を指定するなら `--serial`。`ANDROID_SERIAL` は効かない**（走行は自分で機を選び直す）。
  焼いた APK を**両機に入れてから**走らせるのがいちばん安い — 古い APK の機に当たると
  「実装が効いていない」の顔で出る（`memory/quest_fleet_two_devices.md`）
- ⚠ **計器は「状態が進んだ」ではなく「効果が出た」を出す。** 2026-07-31、判定が「FAIL ゼロ・演出 7 本 OK」と
  出した走行の画に、導入演出が 1 段も出ていなかった。以後 `veil` / `wire` / `cg` / `bgm` / `font` を判定に入れる
- ⚠ **観測項目は C# の `ShowTelemetryHost` と `analyze-xp-log.py` を対で直す**（片方だけだと沈黙して食い違う）
- ⚠ **数値が PASS でも PNG は必ず 1 度開く。** 縮小したコンタクトシートは索引にすぎない（1cm の線が消える）
- ⚠ **音は録画に映らない。** 画は `quest-record.py` が撮れるが、音は実機で聴く以外に確かめる手段が無い。
  だから `ev=sfx` / `sndBuilt` / `sndAud` / `sndLpf` が唯一の証拠になる → `rules/sound-design.md` §7。
  人に聴かせるのは `py -3.11 tools/sound-preview.py` の 9 本
- **被らないと分からないもの**（立体感・スケール・酔い・怖さの強度）は溜めて 1 回にまとめる →
  `rules/visual-verification.md`
- ⚠ **検証出力は放っておくと 1 日で数 GB 積む**（1 走行の mp4 が 300〜450MB）。
  2026-08-23 に C: の空きが 0 になり、走行が落ちた。刈るのは
  `py -3.11 tools/prune-logs.py`（既定は dry-run・`--apply` で実削除）。
  走行の数値証拠（`_xp.log` / `_logcat.log` / `_meta.json` / `-report.md`）と
  `Logs/gen-plate` / `Logs/sound/ingest` は日付に関わらず残る

## スタック

Unity 2022.3.62f2 LTS / URP / Quest 3（Android・IL2CPP・ARM64）/ Meta XR All-in-One SDK + XRI + OpenXR。
映像は**スマホ → MJPEG over Wi-Fi → Unity 内デコード**（配信側は姉妹リポ
[Roiril/fixed-cam-streamer](https://github.com/Roiril/fixed-cam-streamer)。仕様は `rules/streaming.md`）。

オペレータ卓とブラウザ合成検証は `tools/web-compositor/`（`serve.ps1` → `http://localhost:8099/`）。
詳細は [memory/web_compositor.md](.claude/memory/web_compositor.md)。
TableDuo の全体像は [docs/table-duo/remaining-tasks.md](docs/table-duo/remaining-tasks.md)。

## 禁止・注意

- `ProjectSettings/` を理由なく変更しない（特に Graphics / Quality。3 アプリの共有資源）
- パスワード・API キーをスクリプトに書かない（`StreamingAssets/secrets.json` は gitignore）
- `Library/` `Builds/` はコミットしない。`show.json` と `*.asset` の `host` はローカル値
- 依存の追加・削除、履歴の破壊、外部公開は
  **fable アドバイザー手続き**（`~/.claude/rules/fable-advisor.md`）で自分で決めて事後報告する。
  ユーザーへの許可要求はしない

## 知見をどこに置くか

| 何 | 置き場 |
|---|---|
| Unity / Windows 一般・スキル | `~/.claude/`（**ここに複製しない**。同名スキルを両方に置くとどちらが載るか判別できない） |
| ユーザーが言った世界観の判定 | `.claude/canon/` |
| 技術の罠・実装の経緯 | `.claude/memory/`（`MEMORY.md` が索引） |
| 領域ごとの規約 | `.claude/rules/`（scope は **`paths:`**。`globs:` は Claude Code が認識しない） |
| 滅多に使わない手順・退避路 | `.claude/reference/`（**明示的に読むまで載らない**。広い `paths:` の rule に混ぜない） |
| 実装の段の記録 | `.claude/plans/`（索引は [plans/README.md](.claude/plans/README.md)。いま進めるものは `canon/`） |

**機能を変えたらドキュメントも同じコミットで直す** → `rules/doc-sync.md`。

## 応答スタイル

端的・論理的・必要最低限。結論から書く。前置き・総括・差分の自己解説は書かない。
