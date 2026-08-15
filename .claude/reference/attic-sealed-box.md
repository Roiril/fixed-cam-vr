# 封印の箱の退避場所と、戻し方

**2026-08-15 に体験から外した。** 設定が「隔離された壁」から「**回収されて会場に在る壁**」へ変わり
（`canon/LEDGER.md` 0040 / 0044）、体験エリアを隠す必要がなくなったため。

**消していない。** 実装・シェーダ・版・プレビュー・テストは全部そのまま残っていて、
`Attic/` という名前のフォルダへ移してある（`.meta` ごと移したので GUID は変わっていない
＝ シーンの参照も Always Included の登録も生きている）。

⚠ この文書は**明示的に読むまで載らない**（`reference/`）。読むのは箱を戻すときだけ。

---

## いまどうなっているか

| 何 | 状態 |
|---|---|
| 重み `IntroWeights.sealBox` | **全段 0**。`IntroLogicTests.SealedBox_NeverAppears_InAnyStage` が固定する |
| `IntroDirector` | 毎フレーム `sealedBox?.SetHidden()` を呼ぶ（`Apply(w)` ではない ＝ 意図の明示） |
| シーンの `[SealBox]` / `[SealBoxShadow]` | **まだ在る**（`MainDemoSceneSetup` が作る）。描画は上の 2 つで止まっている |
| 唸り `bed_seal` | `SoundBedLogic.Target` が `g.seal = 0` を返す（音源は残っている） |
| テレメトリ `box=` / `boxBuilt=` | 外した（常に 0 が並ぶと誤検出の材料になる） |
| `menu sealedbox` / `menu shatter` | **動く**。体験に出ないだけで、箱の絵は今でも焼ける |

⚠ **シーンからはまだ消していない。** 2026-08-15 の時点で `Main.unity` と
`MainDemoSceneSetup.cs` は別のシュビーが編集中だったため触っていない
（`rules/parallel-projects.md` §7）。消すなら下の「シーンから外す」を通す。

## 退避したファイル

| 移動元 | 移動先 |
|---|---|
| `Assets/Scripts/Streaming/SealedBox.cs` | `Assets/Scripts/Streaming/Attic/` |
| `Assets/Scripts/Streaming/SealedBoxShadowLogic.cs` | 同上 |
| `Assets/Scripts/Streaming/SealedBoxShatterMesh.cs` | 同上 |
| `Assets/Scripts/Streaming/Editor/SealedBoxPreview.cs` | `Assets/Scripts/Streaming/Editor/Attic/` |
| `Assets/Scripts/Streaming/Editor/SealBoxWearImporter.cs` | 同上 |
| `Assets/Scripts/Streaming/Editor/IntroShatterPreview.cs` | 同上（箱の破片を見る絵） |
| `Assets/Art/Shaders/Intro/SealedBox.shader` | `Assets/Art/Shaders/Intro/Attic/` |
| `Assets/Art/Shaders/Intro/SealedBoxShadow.shader` | 同上 |
| `Assets/Tests/Streaming/SealedBoxShadowLogicTests.cs` | `Assets/Tests/Streaming/Attic/` |

⚠ **動かしていないもの**（動かすと壊れる / 他と共有している）:

- `Assets/Resources/Intro/SealBoxWear.png` — `Resources.Load("Intro/SealBoxWear")` が**パスで引く**
- `Assets/Resources/Sound/bed_seal.wav` — 音源は `SoundCueLogic.ResourceName` がパスで引く
- `Assets/Art/Shaders/Intro/IntroShatter.hlsl` — **覆いと共用**（破砕は覆い側が使い続ける）
- `tools/make-sealbox-tex.py` — 版を焼くスクリプト（`tools/` は Unity の外）
- `IntroShatterCurve.BoxShatter` / `PushBox` / `BoxSpin` / `BoxDrift` / `BoxKeepFar` —
  `SealedBox.cs` が呼ぶので同じ場所に残してある

## 箱が居たころの決めごと（戻すなら全部読む）

`canon/LEDGER.md` の 0003（黒い箱・六角の模様）/ 0004（動くのは光だけ）/ 0005（中を見せない）/
0006（模様の継ぎ目）/ 0010（光を赤へ）/ 0011（使い込まれた金属・熾）/ 0016（赤を減らす）/
0017（歩ける範囲と同じ大きさへ）/ 0024（床に影）/ 0028（段 1 で薄くしない）/ 0029（光をもう少し強く）。

技術の罠は `.claude/memory/sealbox_surface.md`（版の 4ch の意味・オクターブノイズ・タイルの周期）。

## 戻し方

1. `git log --oneline --all -- Assets/Scripts/Streaming/Attic/SealedBox.cs` で退避前の版を確かめる。
   **最後に箱が体験に出ていたコミットは `7323660`**（2026-08-15 の退避はその次）
2. ファイルを `Attic/` から元の場所へ戻す（`git mv`。`.meta` も一緒に）
3. `IntroLogic.Weights` に `sealBox` を戻す。**段 1 で薄くしない**（LEDGER 0028）——
   外に居るあいだ `sealBox = 1`、中に入ったら `shell = 1 / sealBox = 0`
4. `IntroDirector` の `sealedBox?.SetHidden()` を `sealedBox?.Apply(w)` へ戻す
5. `SoundBedLogic.Target` の `g.seal = 0` を段ごとの式へ戻す
6. `ShowTelemetryHost` に `box=` / `boxBuilt=` を戻し、`analyze-xp-log.py` と**対で**直す
7. 破砕を箱にも配るなら `IntroShatterCurve.VeilShatter` を `shatter / VeilPhaseEnd` へ戻す
   （いまは進みをまるごと覆いへ渡している）
8. テストを戻す — `IntroLogicTests.SealedBox_NeverAppears_InAnyStage` /
   `SoundBedLogicTests.SealedBox_NeverHums_InAnyStage` /
   `ContainmentShellLogicTests.Intro_StaysOnThePassthrough_UntilTheSwap` が**箱の不在を固定している**
9. `menu sealedbox` で絵を焼いて開く（シェーダの誤りは `unity.ps1 test` に 1 件も出ない）

## シーンから外す（まだやっていない）

1. `MainDemoSceneSetup.cs` の `[SealBox]` / `[SealBoxShadow]` の生成を落とす
2. `.\tools\unity.ps1 menu scene` を実行して `Main.unity` を焼き直す
3. `IntroDirector` の `sealedBox` SerializeField を消す（**シーンの参照が先に消えていること**）
