# fixed-cam-vr — エージェント向けブリーフ（Codex 等）

Meta Quest 3 の作品「廻リ視」。固定視点カメラ（バイオハザード風）の無線映像を VR 内スクリーンへ出し、
実写に CG を合成して見せるホラー体験。**実装は終わっていて、いまは作りこみ（世界観・演出）の段。**

**このリポジトリの正本は `CLAUDE.md` と `.claude/` 配下。** このファイルは入口だけを持つ。
詳細をここへコピーしない（過去にコピーが古くなって嘘をついた）。正本を読むこと:

- プロジェクト全体の規約: [CLAUDE.md](CLAUDE.md)
- 領域別ルール: `.claude/rules/`（streaming / show-design / sound-design / unity-vr / meta-xr / git-workflow ほか）
- 技術の罠・実装の経緯: `.claude/memory/`（索引は `MEMORY.md`）
- 世界観の判定: `.claude/canon/`（`LEDGER.md` = ユーザーの逐語。**エージェントの案を勝手に足さない**）

## アプリ

| ユーザー呼称 | 正式名 | namespace / asmdef | コード root | シーン | パッケージ ID |
|---|---|---|---|---|---|
| **fixedcam** / 廻リ視 / 本体 / カメラ | 廻リ視（FixedCam） | `FixedCamVr.*` | `Assets/Scripts/` | `Main.unity` | `com.roiril.mawarimi` |

同居していた手のアプリ 2 つ（「ハンド」= TableDuo / 「ロボットハンド」= MyCobotHand）は
2026-09-28 に [Roiril/table-duo-vr](https://github.com/Roiril/table-duo-vr) へ分離した。このリポジトリには無い。

## Unity は CLI で操作する

[tools/unity.ps1](tools/unity.ps1) が `unity.exe` を叩く唯一の場所。GUI 前提の手順を既定にしない。

```powershell
.\tools\unity.ps1 doctor          # 前提が揃っているか（最初にこれ）
.\tools\unity.ps1 test            # EditMode テスト（コンパイル込み）
.\tools\unity.ps1 build fixedcam  # → Builds/mawarimi.apk
.\tools\unity.ps1 menu            # Editor の機能を CLI から呼ぶ（引数なしで一覧）
```

MCP for Unity（`.codex/config.toml` に登録済み）は「起動中の Editor をライブ操作する」ときだけの退避路。

## 禁止・注意

- **`docs/proposal/` と `docs/archive/`（企画書）は開かない**（2026-08-08 ユーザー宣言。コンテキスト汚染になる）
- `ProjectSettings/` を理由なく変更しない（特に Graphics / Quality）
- `show.json` と `Assets/Settings/*.asset` の `host` はローカル値。コミットに混ぜない
- `Library/` `Builds/` はコミットしない。パスワード・API キーをスクリプトに書かない
- 機能を変えたらドキュメントも同じコミットで直す（[.claude/rules/doc-sync.md](.claude/rules/doc-sync.md)）

## 画像生成（Codex の主用途）

動画生成用の画像は元の画角・縦横比で生成した後、コードで左右に黒い余白を追加して16:9にする（最初・最後のフレームも毎回）。画像生成AIには黒帯を描かせない。切り抜きや縦横比を変える引き伸ばしは禁止。黒帯付き版を主リンクで渡し、寸法と両側の黒帯を実測する。詳細は [.claude/memory/codex_image_pipeline.md](.claude/memory/codex_image_pipeline.md) の「動画生成用の入力画像」。

差し替え素材（人形・染み・手形）の生成は [tools/gen-plate/](tools/gen-plate/README.md)。
作法の正本は [.claude/memory/codex_image_pipeline.md](.claude/memory/codex_image_pipeline.md)。
合成マスクの改善と再生成は [.claude/skills/composite-mask/SKILL.md](.claude/skills/composite-mask/SKILL.md)。
人形の線画依頼は [tools/doll-ref/PROMPT.md](tools/doll-ref/PROMPT.md)。

## 補助ディレクトリ

- `.codex/config.toml` — この PC の Codex 用 MCP 登録（マシン固有・git 管理外）
- `.agents/skills/` — `.claude/skills/` への junction（実体はそちら。二重管理しない）
