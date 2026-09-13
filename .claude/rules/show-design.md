---
name: show-design
description: 廻リ視の体験そのものの仕様と経緯（ゾーン / 位置合わせ / 入力 / 導入・終幕 / 周回 / HMD 文言）。この領域の .cs とシーンを触る前に読む
paths:
  - "Assets/Scripts/Streaming/**"
  - "Assets/Scripts/Tracking/**"
  - "Assets/Scripts/Diagnostics/**"
  - "Assets/Scripts/Input/**"
  - "Assets/Scripts/OvrBridge/**"
  - "Assets/Scenes/**"
---

# 廻リ視の体験仕様

## 2026-09-13 の表示実装について

`canon/LEDGER.md` 0207 により導入とエージェント画面を再構築した。
具体的な見た目は `canon/OPEN.md` の案であり採否は未判定。
現行の実装は [presentation_redesign.md](../memory/presentation_redesign.md) を参照する。

0208 により「割れる演出」を継続する。四辺の開口だけで閉じる案は取り下げた。
0209 により中央も含めて全体を割る。中央の四角形を無傷で残してはいけない。
全体が割れた後に、同じ破片が集まって中央の四角い面を作る。その後にカメラ映像へ移る。
終端の四角形を早い段階から別の窓として描く実装は、この順番を壊す。
0211 により、割れる瞬間の実景を静止画へ保持し、その像を破片と一緒に運ぶ。
実景が見える穴だけを動かす描画は主経路へ戻さない。取得に失敗した場合の代替動作として区別する。
0212 の参考は尖った大小の破片が離れて飛ぶ見え。細かな網目を保った一枚の縮小を
「自由に割れる」と判定しない。参考を使うときは輪郭と寸法差を先に比較する。
中央が空く途中の画像と、同じ破片が終端の面へ戻る画像を両方見る。
参考の発光量と装飾を、そのまま作品の要件へ移さない。
検証は入力画像を変えても凍結後の破片が変わらず、再取得すると変わることを実描画で測る。
静止画の経路では alpha は閉じたまま。中央が割れた形と収束後の連続面は RGB で確認する。
RGB も確認する。中央の隙間だけに既存スクリーンの燐光が残ると、完成前から四角形が見えてしまう。
以下に残る「4096 セル」は旧実装の記録。個数や均等格子は要件ではない。
「赤とシアンの全文複製」「文字列を全角の化け字へ置き換える」
「面の横幅を伸縮させる」も旧実装の記録。現在の表示要件として復元しない。
開始条件と案内本文は継続する。周回に伴う侵食と帰りの回復も継続する。
`IntroWeights.shatter` は破片と音の共通の進み。描画の確認には `IntroVeil.ShatterPeak` と
`ShatterPieces` を使う。進みの値だけを描画の証拠にしない。

**書いてあるのは「いま何がどう決まっているか」と「なぜそうなったか」。**
Unity の書き方・ビルド・描画の罠は [unity-vr.md](unity-vr.md)、配信と show.json の契約は
[streaming.md](streaming.md) が正本。

## 座標駆動の体験設計（PlayerZone / カメラ自動切替）

体験設計で **HMD 座標は最重要の入力**。Quest 3 の標準ガーディアンを前提に以下を守る：

### 前提
- **実プレイレンジは ±1.3m 程度**（4 畳半 / 2.5–3m 四方の典型値）。これを超える前提でゾーンを置かない。
- HMD 高さは ≈1.6m。`PlayerZone.centerOffset` を 0 のまま運用するなら、ゾーン Transform.y を 1.0–1.2m に置いて `halfExtents.y=2` で全身を含める。
- **ゾーン形状は推測で決めず、実機で `[HudDump]` / `[HmdTrace]` を取って `pos` の min/max からプレイレンジを逆算してから決める**（[`HmdTrajectoryRecorder`](../../Assets/Scripts/Diagnostics/HmdTrajectoryRecorder.cs) 参照）。

### Pick() の挙動を踏まえた設計
[`PlayerZoneTracker.Pick`](../../Assets/Scripts/Tracking/PlayerZoneTracker.cs) は次の順で評価する：
1. 直近ゾーンが `shrink` 後 AABB に含まれるなら維持（再評価スキップ）
2. それ以外は全ゾーンで full AABB 判定 → `priority` 大が勝ち、同値なら **配列先頭が勝つ**

これを踏まえると：

- **隣接ゾーンを 0.05–0.10m オーバーラップさせる** — `keepLastWhenOutside` に頼って間を埋めるとデッドゾーンで前ゾーン張り付きが起き、ユーザーが「切り替わらない」と感じる
- **Center を配列の先頭に置く** — オーバーラップ帯では Center が勝ち、中央寄りに自然に張り付く
- **`hysteresisShrink` は halfExtents の 20–30% を目安**（hx=0.5m なら 0.10–0.15m）。これより小さいと境界 jitter、大きいと「shrink から出ても full では現ゾーンが勝つ」状態が続いて切替が鈍くなる
- ヒステリシス幅 ≒ `hx_overlap` + `shrink` の効き方の組み合わせで決まる。**手で計算するか、Pick の事前条件をテストで固定する**（[`PlayerZoneSelectionTests`](../../Assets/Tests/Tracking/PlayerZoneSelectionTests.cs) を増やす）

### 既定ゾーン（廻リ視 周回経路・**推測配置**、`MainDemoSceneSetup` 自動配置）

L 字壁（西の腕 + 北の腕、中央）を時計回りに 南→東→北→西 と周回する想定（企画書 図4）。
カメラ C は AABB が矩形のみなので北 + 西の 2 ゾーンに分割（同 cameraIndex=2）。

```
A:South: (0,    1, -0.8)  hx=(1.4,  2, 0.55)  z ∈ [-1.35, -0.25]  cam 0
B:East:  (+0.8, 1, +0.2)  hx=(0.55, 2, 1.2)   x ∈ [+0.25, +1.35]  cam 1
C:North: (-0.2, 1, +0.8)  hx=(1.2,  2, 0.55)  z ∈ [+0.25, +1.35]  cam 2
C:West:  (-0.8, 1,  0)    hx=(0.55, 2, 1.0)   x ∈ [-1.35, -0.25]  cam 2
```

**⚠ 2026-06-11 時点で実測未校正**。パーテーションで L 字壁を組んだら `[HmdTrace]` を取り、
`MainDemoSceneSetup.cs` の値を上書きして `Setup Main Demo Scene` を再実行（冪等）。
再実行は [Tracker] を作り直すが、**`ShowControlClient.zoneTrackerToDisable` は Setup が新 Tracker へ自動で再配線する**
（`MainDemoSceneSetup` 2.7 節。手でアサインし直す必要はない。見つからなければ警告を出す）。

**⚠ 導入演出・終了の暗転を実機で試すなら Setup の再実行が必須**（2026-07-30 実害）。
`IntroDirector` / `IntroVeil` / `IntroStructureWire` / `ShowEndingFader` / `OVRPassthroughLayer` は
**シーンに焼かれた GameObject** なので、コードを実装しただけでは APK に入らない。
シーンの保存日がコードの実装日より古いときは、**ビルド前に必ず焼き直す**
（`grep "m_Name: Intro" Assets/Scenes/Main.unity` で 1 行も出なければ未配線）。

```powershell
.\tools\unity.ps1 menu scene
```

**⚠ ただし「コンポーネントが付いているか」は GameObject 名では確認できない**（2026-07-31 に誤診した）。
`IntroStructureWire` は `IntroDirector` と同じ GameObject に `AddComponent` されるので
`m_Name: IntroStructureWire` は**存在しないのが正常**。`OVRPassthroughLayer` も同様で、シーン YAML には
型名ではなく script guid で記録されるため `grep OVRPassthroughLayer` は 0 件になる。
これを「未配線」と読むと、実際には別にある原因（今回はパススルーの初期化失敗とシェーダの
ビルド剥がれ）を見落とす。**確実なのは実機ログを読むこと** — 今回の 3 件はすべてログに出ていた。

**⚠⚠ 角括弧の名前は YAML がクォートするので、素朴な grep は必ず空振りする**（2026-08-23）。
このプロジェクトの GameObject は `[Sound]` / `[Tracker]` / `[Comms]` / `[WalkGuide]` のように
角括弧付きの名前が多いが、シーン YAML では **`m_Name: '[Sound]'`** と書かれる。

```bash
grep -c "m_Name: \[Sound\]"  Assets/Scenes/Main.unity   # → 0（[] が文字クラスとして解釈される）
grep -cF "m_Name: [Sound]"   Assets/Scenes/Main.unity   # → 0（クォートを含まないので一致しない）
grep -cF "m_Name: '[Sound]'" Assets/Scenes/Main.unity   # → 1 ✓
```

⚠ **数えるなら名前ではなく script の guid で数える**（`<型>.cs.meta` の guid をシーンで grep）。
名前は表示のためのもので、参照の実体は guid。2026-08-23 に「`[Sound]` がシーンに無い」と
危うく誤報しかけた（実際は正しく焼かれていて、**空振りしたのは grep の書き方**だった）
— `~/.claude/rules/work-style.md` §2「道具が空を返すことは、対象が無いことの証拠にならない」。

### ゾーン校正の再設計（2026-07-16〜・形状は PC / 位置合わせは HMD 2 点登録）

**旧 ZoneCalibrator（ゾーンを HMD 内でドラッグ・リサイズ・回転）は廃止**。校正を 2 つに分解した（設計 [.claude/plans/2026-07-16_zone-authoring-redesign.md](../plans/2026-07-16_zone-authoring-redesign.md)）：

1. **形状・カメラ割当 = 純データ**（物理レイアウトは固定）。PC の Web オペレータ卓が show.json `layout` を配り、[`ZoneLayoutSolver`](../../Assets/Scripts/Tracking/ZoneLayoutSolver.cs) が矩形 OBB へ決定的に展開 → [`ZoneLayoutApplier`](../../Assets/Scripts/Tracking/ZoneLayoutApplier.cs) が [GeneratedZones] へ生成し tracker.zones を差し替える。**HMD では形状を一切いじらない**。
   - **v2: タイルペイント（grid）モデル**（2026-07-16〜・既定の編集モデル）: フロアを正方タイル（`layout.grid`: tileM/cols/rows/cells）に分割し、各タイルをカメラ index で塗る。Solver は**カメラ毎のタイル集合を貪欲矩形分解**（行方向マージ→行間マージ・決定的）→ 各矩形を `overlapM/2` 全方向拡張して隣接カメラ境界で計 `overlapM` 重ねる。cells は rows 本の文字列（row0=北端 z=+d/2 / col0=西端 x=-w/2、`'0'..'8'`=カメラ / `'.'`=未割当）。不正 cells は警告 + 未割当扱いで例外にしない。
   - **v1: cuts（ループ切れ目）モデル**は後方互換で残す（`grid` があれば grid 優先。端末キャッシュに古い cuts しか無くても動く）。選択は `ZoneLayoutSolver.ChooseSource(hasGrid, hasCuts)`（grid 優先）。show.json layout present 判定は `ShowLayoutDef.HasData()`（grid か cuts）。
2. **位置合わせ = 剛体 3 DOF**（XZ 平行移動 + yaw）だけを [`CourseFrame`](../../Assets/Scripts/Tracking/CourseFrame.cs) が持ち、[`CourseRegistrationController`](../../Assets/Scripts/Tracking/CourseRegistrationController.cs) の **HMD 2 点登録**で解く（[Tracker] 上、`Setup Main Demo Scene` が自動配線）。

**フロアマップ（卓）の 7 モード**: 🖌 塗る（ゾーン）/ 📍 位置合わせ点（`layout.regPoints`）/ 🚶 歩かせる（シミュレータ）/
📐 カメラ姿勢（`cameras[].pose`・CG 人形の視点）/ **📏 通過ライン（`layout.lines` = 演出の発火点となる床の線分。
2026-07-27〜・契約は [streaming.md](streaming.md) の「開始規則『このラインを通過したら』」）** /
**🧱 部屋（`layout.room` = 壁・箱の 3D プロキシ。CG のオクルーダ／影の落ち先／較正参照を 1 幾何で兼ねる。
2026-07-27〜）** / **🎬 開始位置（`layout.startSpot` = 体験者がここに 0.5 秒留まると導入演出が始まる床の円。
1 点だけ。2026-07-30〜・契約は [streaming.md](streaming.md) の「導入演出」）**。
⚠ **導入の開始は「📏 通過ライン」を選ぶ方が既定**（`run.intro.startLineId`・⚙ 欄の「始まる合図」）。
線なら歩いて入ってくる動きのまま始まり、開始位置に立ち止まって待つ手順が要らない。
円は `startLineId` が空のときだけ使われる。どちらも**被っていること**が前提。
加えてモードに紐づかない **💡 CG 照明**パネル（`layout.room.light`）が常時出る。
ラインは**担当カメラの色**で描かれる（どの区間のものかが目で分かる）。
どのモードでもライン・カメラ印・開始位置は薄く描かれる（塗りながら位置関係が見える）。

⚠ **点を置く面が 4 つある**（位置合わせ点 / 較正の印 / 通過ライン / 開始位置）。**どれも別の集合**で、
コピーし合わない。兼用すると片方を動かしたときにもう片方が壊れる — 制約が違うため
（位置合わせ点は HMD で手が届く必要があり順序に意味がある / 較正の印は映像に写る必要がある /
通過ラインは事象 / 開始位置は立って待つ場所）。

#### 入力モデル（右コントローラ 4 入力のみ・2026-07-20〜）

体験者はコントローラを持たないため封印モード（旧 Run/Staff）を廃止。右手の A / B / グリップ / トリガー
だけで全操作を賄う（[`ControllerModeLogic`](../../Assets/Scripts/Input/ControllerModeLogic.cs) の 2 状態
Normal / Registration）。左手・スティック・cue 試射・操作チートシート（旧 StaffPanel）は撤去。

| 状態 | 入力 | 機能 |
|---|---|---|
| Normal | A（右）短押し | **タイトル画面を閉じて体験を始める**（2026-08-12〜）。タイトルが出ていなければ何も起きない |
| Normal | B（右）短押し | ステータス表示トグル（StatusHud） |
| Normal | 右グリップ 2 秒長押し | ランリセット（周回リセット + cue 発火済みクリア・体験者交代） |
| Normal | 右トリガー 2 秒長押し | 位置合わせ（Registration）入場 |
| Registration | A（右） | 点サンプル（0.5s ホールド平均）/ Verify 中: やり直し |
| Registration | B（右） | Verify で確定・保存・退場 |
| Registration | 右トリガー 2 秒長押し | キャンセル退場（入場と対称） |
| **Normal のあいだ** | **左のどれかのボタンを 1 秒長押し** | **体験者が異変を報告する**（2026-08-15〜。**2026-08-16 に 2 秒から半分へ**・`canon/LEDGER.md` 0059）。紙の「気になるものが見えたら、手元のボタンを押してください」 |
| **注意書きが出ているあいだ** | **左のどれかのボタンを単押し**（**離した時**・**0.5 秒以内**） | **言語を次へ**（日本語 → English → Français → 日本語。2026-09-03・`canon/LEDGER.md` 0127）。⚠⚠ **単押しは 0.5 秒以内に離した押下だけ**（`ShowLanguage.TapMaxSec`・2026-09-05・0159）。それより長く握った回は**離しても何も起きない**。⚠ このあいだ**報告のゲージは進めない** — 進めると言語を選んだだけで異変の報告が 1 件立つ |
| **注意書きが出ているあいだ** | **左のどれかのボタンを 1.5 秒長押し** | **ホラー軽減モードの出入り**（2026-09-05・`canon/LEDGER.md` 0154）。既存の音が半分になり、陽気な曲が流れる。**画・尺・進行は 1 ビットも変わらない**。⚠ 長押しの進み具合は面に**輪 10 個のゲージ**で出る（0155）。**出るのは単押しの窓（0.5 秒）を過ぎてから**（0159）＝ **ゲージが出たら、離しても言語は変わらない** |

⚠⚠ **左はどのボタンでも同じ**（2026-09-03・`canon/LEDGER.md` 0128・ユーザー指定「体験者が押すボタン、
X/Y だけじゃなく、どのボタンを押してもいいようにしてほしい。これは、言語選択も報告も全部含めて」）。
**右（スタッフ）と左（体験者）は完全に分かれている。**
**どれを押しても同じ**なのは、被った体験者に手元が見えないから（`canon/LEDGER.md` 0050）。

| 読むもの（`OvrControllerBridge.LeftAnyButtons`） | X / Y / 人差し指のトリガー / 握り / スティックの**押し込み**（5 つ） |
|---|---|
| **読まないもの ①** | **スティックの倒し** — ボタンではなく、歩いているあいだ親指が乗っているだけで倒れる。1 秒握れば報告になる以上、倒しっぱなしが報告に化ける |
| **読まないもの ②** | **左のメニュー（`RawButton.Start`）** — `docs/onsite-checklist.md` が現場の事実として「Quest 側の予約で、押されると体験が中断する」と書いている。届くかは未確認だが、予約なら入れても無意味、部分的に届くなら**言語が変わると同時にシステムの面が出る**（いちばん悪い）|

- ⚠⚠ **注意書きの中では、決着は「離した時」**（2026-09-05・0154）。同じボタンが短押しと長押しで
  2 つの意味を持つので、**押した瞬間に言語を巡らせると長押しの途中で必ず言語が 1 つ進む**
  （Français を選んだ人が軽減モードへ入るたび日本語へ戻る）。離れたかは
  **まとめたマスクの level 読み**（`OVRInput.Get` は OR）で見る — `GetUp` を 1 つずつ聞くと、
  握りに指を掛けたまま X を離した回が「離した」になる
- ⚠⚠ **離した時なら何でも良いわけではない。単押し（0.5 秒以内）だけ**（2026-09-05・0159・
  ユーザー指定「長押しした後に離すと言語が変わるのが面倒なので、単押し以外で言語は変わらないように」）。
  成立した長押しは入力側が食べているが、**成立する手前でやめた回**（1.4 秒など）は
  単押しと区別が付かず、軽減モードを狙って押すたびに言語が 1 つ進んでいた。
  ⭐ 窓の終わりは**ゲージが出始める所と同じ値**（`ShowLanguage.TapMaxSec`）＝
  **ゲージが出たら、離しても言語は変わらない**。押し方と画が 1 つの値で動く
- ⚠ **押していないあいだ、ゲージは 1 文字も出さない**（0159・ユーザー報告「最初から○○○○が
  出ていると文字化けしているのか心配になる」）。待ち受けに同じ字を 10 個並べると
  **フォントが壊れたと読まれる**。行そのものは全角空白で残す（消すと塊が上下に跳ねる）
- ⚠⚠ **まとめたマスクで `GetDown` を呼ばない。** `OVRInput` は前フレームにマスクのどれかが押されていたら
  問答無用で false を返すので、**握りに指を掛けたまま X を押す持ち方だと言語が一度も切り替わらない**
  （しかも握っている人にだけ起きる）。押した瞬間へ戻すことがあれば**必ず 1 つずつ聞く**
- ⚠ **言語を選んだ手を、そのまま報告として数えない。** 押したまま注意書きが閉じられると、
  本編の 1 秒後に身に覚えの無い報告が 1 件立つ。**一度離すまで数えない**（`_markNeedsRelease`）
- ⭐ **手で握って押せるものは 5 つで尽きている**ので、メニューを外しても
  「どれを押してもいい」は体験者から見て成立する

⚠⚠ **`Button.Three` / `Button.Four` を `Controller.LTouch` と組み合わせない。**
LTouch の仮想マップは `Three = None` / `Four = None` なので（`OVRInput.cs` の `OVRControllerLTouch`）、
**押しても永遠に false**。Three=X が生きているのは左右をまとめた `Controller.Touch` のマップだけで、
`ShouldResolveController` は LTouch 指定のとき Touch を弾く。
2026-08-15 まで記録ボタンはこの形で、**実機で一度も発火していなかった**（`[XP] ev=mark` が 1 行も無い）。
⇒ **物理ボタンを名指しする `RawButton.X | RawButton.Y` を使う**（X / Y は左にしか無い）。

- ⚠⚠ **報告は「異常を消す」ようになった**（2026-08-17・`canon/LEDGER.md` 0050 のユーザー逐語
  「左半分に人形が大量にいて、それを異変だと思って**報告したらそれらが消え**」
  「**推したら乱れたのちに元に戻って**終幕で」）。4 周目 A だけの作り込みだったものを仕組みへ一般化した。
  契約の正本は `rules/streaming.md` の「報告で異常が消える」節
  - **消えるのは、著作者が `dismissible` と宣言した演出だけ**（既定 false）。旗を立てていない演出は
    従来どおり 1 ビットも変わらない
  - ⚠ **押さなくても体験は同じように進む**（消える演出が消えないだけ）。ここは変わっていない —
    判定に使うと押さなかった人が失敗した気になる
  - ⚠⚠ **驚かせる引き金にはしない**（0050 4 回目「報告ボタンは演出の引き金にはしない。
    驚かせるのは2周目Cの全画面にしよう」）。**報告で現れてよいのは現実（ライブ映像）だけ**で、
    消えた跡に別の素材を出さない
  - ⚠⚠ **3 周目（1 周目の録画 ＝ 映像の中の自分）には旗を立てない。** 紙が「気になるものが見えたら
    押してください」と言っている以上ほぼ全員が押すので、立てると**作品の核をほぼ全員が見ずに終わる**
  - ⚠⚠ **効くか効かないかを実行しているのは AIエージェント**（2026-08-17・`canon/LEDGER.md` 0082）。
    1〜2 周目は通り、3 周目は侵食されていて通らず、帰りの A で復帰して通る。
    体験者は異変を**指す**だけ
  - ⚠⚠ **帰りの A で通った報告だけが「呪いを解く」**（同 0083）。そこで**視界の劣化も元へ戻り**
    （`ScreenDecayLogic.Release`）、**AIエージェントの侵食も 0 まで消える**（0129）。
    契約は `rules/streaming.md`「呪いが解けると視界が戻る」。
    ⚠ 戻らないのは**音だけ** — 装置の声の痩せは同じ進みを読んでいるので、
    そこまで戻すと「直った」を音で宣言することになる。**装置は使い込まれたまま、
    呪いに由来するものだけが消える**
- ⚠⚠ **正誤は返す**（2026-08-16 に反転・`canon/LEDGER.md` 0054）。それまで
  「返すと答え合わせになり、装置が『何が異変か』を判定してしまう」として禁じていたが、
  ユーザーが**連絡の面でそれを返すこと**を指定した（当時の呼び名は「上司からの連絡」。
  送り主は 2026-08-17 に AIエージェントへ変わった — `canon/LEDGER.md` 0067）:
  演出が走っていれば「異常が記録されました」、走っていなければ「異常は検出されませんでした」。
  返りは**左コントローラの短い振動 ＋ 手元の面 ＋ 連絡の面**（[`CommsPanel`](../../Assets/Scripts/Diagnostics/CommsPanel.cs)）
- ⚠⚠ **分岐の材料は「解除が通ったか」**（2026-08-17・`canon/LEDGER.md` 0082）。
  `ShowControlClient.LastMarkResolved` ＝ `TimelineDirector.NotifyVisitorMark` の**戻り値**。
  - **「演出が走っていたか」ではない。** 2026-08-17 まではそれを見ていたので、
    **3 周目の入れ替わり（消えない）に押しても「異常が記録されました」と返っていた** ＝
    消えていないのに装置が認めた顔をする。いまは「異常は検出されませんでした」で、
    **装置は本当に検出できていない**（解除を実行するエージェントが侵食されている周）
  - ⭐ **凍らせる順序の問題が消えた。** 旧実装は中継の前に `ActiveTakeId` を見る必要があり
    （締めのカットが報告でその場で畳まれるため）、1 行動かすと 4 周目 A の連絡が真逆になった。
    **畳んだ本人が戻り値で答える**ので、その依存が構造的に無い
  - `analyze-xp-log.py` が `ev=mark res=` と `ev=comms id=` の食い違いを FAIL にする。
    **解除が 1 度も通らない走行は WARN**（台本に `dismissible` が 1 つも立っていない疑い）
- **短押しでは通さない。** 歩きながら握り込むので、押した瞬間に決まると「触れただけ」が報告になる。
  判定は [`VisitorMarkHoldLogic`](../../Assets/Scripts/Input/VisitorMarkHoldLogic.cs)（純ロジック・
  テスト 6 本）。1 回の押しで 1 回だけ・離すとゲージは 0 へ戻る・**dt は 0.25 秒で切る**
  （復帰直後の飛びで握った瞬間に発火するのを防ぐ）
- **押し方は手元の面が出す** → [`VisitorMarkPanel`](../../Assets/Scripts/Diagnostics/VisitorMarkPanel.cs)（下の表）
- ⭐ **押した時刻が残ると、3 周目の反転に気づいたかが訊かずに分かる**（初見は消耗品なので、
  誘導せずに取れる観測の価値が高い）。観測は `ev=mark n= lap= cam=`
- ⚠ **握った手はハンドトラッキングされない** → 映像の中の人形の左腕は体側で止まる（仕様）
- ⚠ **左の ≡ と Oculus ボタンは Quest 側の予約**で、押されると体験が中断する。アプリからは奪えない

**スタッフ（右）の**長押し閾値は 2 秒固定（const `LongPressSec`。SerializeField にすると既存シーン
YAML で 0 に読まれる罠を避ける）。

⚠⚠ **体験者（左）の報告は 1 秒**（`VisitorMarkHoldLogic.DefaultHoldSec`・2026-08-16 に半分へ）。
**もう揃っていないので、片方を見てもう片方を推測しない。** 分かれている理由は役割が違うから —
右は誤操作すると体験が壊れる操作（ランリセット・位置合わせ）、左は押さなくても体験が進む記録。
⚠ 左を 0.5 秒より短くしない（歩きながら握り込んだだけが報告になる）。

⚠ **A のカメラ手動送りは 2026-08-12 に撤去した**（ユーザー宣言「カメラの手送り機能は要らないです」）。
連鎖して `OvrControllerBridge` の `registry` / `switchDirector` を削除し、`nextButton` を
**`primaryButton`** へ改名した（prefab YAML と `StreamingLogicPrefabFieldsTests` も同時に）。
**現場でカメラを覗く手は Web 卓の 📺 カメラ固定だけ**になった（Editor のキーボード
`CameraSwitchInput` の Tab / 1-9 は残っている）。タイトル画面そのものは
[plans/2026-08-12_title-screen.md](../plans/2026-08-12_title-screen.md) と [memory/title_screen.md](../memory/title_screen.md)。

#### HMD N 点登録リチュアル（約 10 秒）

| 操作 | 機能 |
|---|---|
| **右トリガー 2 秒長押し** | 登録モード開始（2026-07-20〜。Normal からの唯一の入場）。**有効な登録が既にあれば Review（確認）フェーズに着地**、未登録なら点 1 の Capture から始まる（2026-07-21〜） |
| Review: A（右） | 点 1 から再登録（Capture へ）。ワイヤーが実物に重ならない＝ズレている時に押す |
| Review: B（右） | 保存せず終了（既存登録は不変。`RegistrationConfirmed` は発火しない） |
| A（右）で点 1..N | **show.json `layout.regPoints` の点（2〜5・順序つき・Web 卓フロアマップの「📍 位置合わせ点」で配置）を順にタッチ**。床の×印テープの真上に先端をかざして A を 0.5 秒ホールド（位置サンプル平均・壁に触る必要なし）。**ホールド中は進捗バー `計測中 ███░░ 0.3/0.5s` + 触覚 HoldTick ランプ**（2026-07-21〜）。0.5 秒未満で離すと不成立＝触覚 Error。regPoints 不在の旧 show.json は従来既定 2 点 (-0.5,0.5)/(0.5,0.5) にフォールバック |
| （ライブ表示） | 2 点目以降は「直前の点との実測距離 vs authored 距離の誤差 %」を表示しながら当てられる |
| （自動チェック） | N 点の 2D 剛体フィット（`RigidFit2D`・2 点時は従来解と同一）後、**max 残差 > 0.12m（maxResidualM）なら「どの点のタッチが悪いか」を表示してやり直し**（触覚 Error）。通過したら Verify へ（触覚 Fire） |
| Verify: （表示） | 1 行目に **`最大残差 0.05m（合格 ≤0.12m）`** を表示（2026-07-21〜） |
| Verify: B（右） | **確定**（`persistentDataPath/registration.json` へ保存 + モード終了）。残差・点数・保存日時も焼き込む |
| Verify: A（右） | 最初からやり直し（ステップ1へ戻る） |
| Verify: 右トリガー 2 秒長押し | キャンセル退場 |

- **スティックナッジ（平行移動・yaw 微調整）は廃止**（2026-07-20）。N 点剛体フィット + 残差ガード 0.12m が精度を担保し、やり直しが約 10 秒で安いため。
- **登録プレビューはトランザクション**（2026-07-23 監査修正・テスト `CourseFramePreviewSessionTests`/`CourseRegistrationControllerTests`）:
  `CourseFrame` に `BeginPreviewSession`/`CommitPreviewSession`/`RollbackPreviewSession` を実装。登録モード入場で Begin、
  B 確定（SaveRegistration）で Commit、**キャンセル退場（トリガー長押し）で Rollback** — 変換・要再登録フラグ・品質メタを
  コミット済み値へ復元し Changed を発火（ダーティ時のみ。Review-B の無変更退場では発火しない）。旧実装は残差ガード通過時の
  `SetRegistration(save:false)` プレビューがキャンセル後も残り、未保存の誤フィットにゾーンが整列したままだった。
  ホールド平均計時は純ロジック [`HoldAverageSampler`](../../Assets/Scripts/Tracking/HoldAverageSampler.cs)（dt 注入・`RegInput.deltaTime` を Bridge が供給）。
- **prefab の SerializeField は機械監査**（2026-07-23 監査修正・テスト `StreamingLogicPrefabFieldsTests`）: StreamingLogic.prefab の
  `OvrControllerBridge` ブロックに `statusButton` キーが無く Button.None（0）で読まれ、**実機で B ボタン全死**（StatusHud トグル +
  登録 Verify の B 確定不能）だった。prefab YAML を現行フィールドへ全書換（statusButton:2 追加・stale キー掃除）し、
  SerializedObject の実効値 + 生 YAML キー実在の二本立てテストで再発（missing/stale 双方）を機械検出する。
  MonoBehaviour に [SerializeField] を足したら prefab YAML への反映と本テストの更新を対で行うこと（unity-prefab-fields スキル参照）。
- **先端位置は RightHandAnchor の position をそのまま使う**（先端オフセット補正なし。誤差 2〜3cm は 1m ベースライン + 40cm 回廊 + 8cm オーバーラップに対して許容）。SerializeField `rightHandTransform`、null なら headTransform にフォールバック。
- **床の高さも同じタッチから測る**（2026-08-02〜）。それまで登録は 3 DOF（XZ + yaw）で **y を捨てていた**ため、
  壁や床のワイヤー・ゾーンのタイル・CG 人形が実際の床より下に出ていた。トラッキング原点は既に FloorLevel
  設定（`Main.unity` の `_trackingOriginType = 1`）なので**設定では直らない** — 実測して合わせる。
  - `floorY = median(タッチ位置の y) − layout.regTouchHeightM`（[`FloorHeightSolver`](../../Assets/Scripts/Tracking/FloorHeightSolver.cs)）。
    **中央値**を使うのは、1 点だけ床に着け損ねた登録で床が引っ張られないため
  - `layout.regTouchHeightM` は「コントローラを床から何 m の高さに構えるか」。**既定 0 = 床に着ける**。
    空中でホバーすると XZ が確実にぶれて残差ゲート 0.12m を圧迫するので、精度としては 0 が最善
  - y のばらつき（max−min）が 6cm を超えたら**警告を出す**（不合格にはしない）。「床に着けていない点がある」を
    現地で気づける、無料の品質ゲート
  - **⚠ `CourseFrame.CourseToWorld` の第 2 引数の意味が変わった**（旧: ワールド y の直指定 → 新: **床からの高さ**）。
    呼び出し側が渡していた定数（ワイヤー 0.03 / タイル 0.015 / ゾーン中心 1.0 / CG 人形の 0）はすべて
    「床からの高さ」のつもりの値なので、意味の変更で全部が正しく持ち上がる。**新しく呼ぶときは床基準で渡すこと**
  - `registration.json` に `originY` / `floorSpreadM` / `regSchema`(=2) を追加。旧ファイルは `regSchema=0` で
    読まれ、**originY は捨てて 0 にする**（測っていない 0 を「床が一致している」と読むと、ずれたまま
    「合っている」と表示することになる）。StatusHud と Review 画面が「床の高さは未測定」と名指しする
  - 較正（`cameras[].calib`）は不変。course 空間で解かれ、人形・影・部屋プロキシも同じ `CourseToWorld` を
    通るので、床ごと一様に平行移動するだけ
- **登録直後にワイヤーフレーム検証表示**：壁ポリライン（L の 2 辺・高さ既定 1m）+ フロア外周を LineRenderer でゴースト表示。show.json layout に wall/floor があればそれを、無ければ内蔵既定（フロア 1.8×1.8・regPoint から導出）を描く。ワイヤーは毎フレーム CourseFrame 変換に追従。
- **視界内ガイダンス**：自前 TextMesh は持たず、各ステップの指示を `GuidanceText` / `GuidanceColor` として公開し、単一サーフェス [`StatusHud`](../../Assets/Scripts/Diagnostics/StatusHud.cs) が登録中に読み取って強制表示する（旧 [CourseRegGuidance] TextMesh は廃止・Tracking→Diagnostics の asmdef 依存を作らないプロバイダ方式）。save は B 確定でのみ registration.json を書く。文言のフォーマットは純関数 [`RegistrationGuidance`](../../Assets/Scripts/Tracking/RegistrationGuidance.cs)（進捗バー・残差行・Review ヘッダ）に切り出し EditMode テスト（`RegistrationGuidanceTests`）で固定。
- **確認（Review）フェーズ**（2026-07-21〜）：確定後でも位置ズレを見直せる道。登録開始時に `CourseFrame.HasRegistration`（json ロード済み or 今セッション確定済み）なら Capture でなく Review に着地し、既存登録のワイヤーフレーム + ゾーン床フットプリント + ヘッダ `登録済みの位置合わせを表示中（保存: <日時> / 残差 <X.XXm> / <N>点）` を出す。A=点 1 から再登録 / B=保存せず終了。recenter フラグが立っていれば橙で「⚠トラッキング原点が変わっています — 再登録を推奨」を追加。
- **登録品質・鮮度の永続化**（2026-07-21〜）：`registration.json` に `maxResidualM` / `pointCount` / `savedAtIso`（ローカル時刻 ISO）を追加（JsonUtility 欠損 default で後方互換・旧ファイルは「記録なし」表示）。`CourseFrame` が `HasRegistration` / `MaxResidualM` / `PointCount` / `SavedAtIso` を公開し、Review 表示と StatusHud のステータス行バッジ（`⚠未登録` / `登録済(残差0.05m)`・`⚠要再登録` 優先）に使う。
- **登録モード中はゾーン床フットプリント表示**（Capture/Verify/Review 全フェーズ）。show.json `layout.grid` があれば Web 卓フロアマップで塗った**生タイル**をカメラ別色で床に表示（[`ZoneGridFootprint`](../../Assets/Scripts/Tracking/ZoneGridFootprint.cs)＝単一メッシュ・頂点色・`CourseFrame.CourseToWorld` で焼き込み・Changed/LayoutChanged 駆動）。grid 不在（v1 cuts / 既定ゾーン）時は従来どおり現存 `PlayerZone` の OBB を Quad で床投影する。色は共有パレット [`ZonePalette`](../../Assets/Scripts/Tracking/ZonePalette.cs)。セル→course 座標は `ZoneLayoutSolver.CellRect`（SolveGrid とフットプリントが同一式を共有＝生タイルとゾーンが一致）。通常のボタン操作（カメラ切替・ステータス）は抑止される。**⚠ 実機未検証（2026-07-22）**。
- **OS recenter 検知**（Oculus ボタン長押し等でトラッキング原点が変わる）：OvrControllerBridge が `OVRManager.display.RecenteredPose` を購読 → `CourseFrame.MarkNeedsReRegistration()` で「要再登録」フラグ + 警告ログ + 視界警告を出す。**ゾーン動作は継続**（黙ってズレたまま動かさない、が目的）。再登録すればフラグは降りる。
- **PlayerZone は OBB（向き付きボックス）**: `Contains` はワールド差分をゾーンローカル軸（`transform.rotation`）へ射影して判定する。rotation が identity のときは従来 AABB と完全一致（既存テストもそのまま pass）。CourseFrame の yaw が各生成ゾーンの向きに乗る。
- 保存先は**端末ローカル**（Quest なら `/sdcard/Android/data/com.roiril.mawarimi/files/registration.json`）。1 変換（originXZ + yawDeg）のみを持つ。旧 `zone_calibration.json`（ゾーン個別の形状保存）は**廃止**。
- 入力は OvrBridge → `CourseRegistrationController.Feed()` 転送（Tracking asmdef は OVRInput 非依存のまま）。
- **⚠ Phase 3（HMD 登録）は実装済み・実機未検証**（2026-07-16、フィードバック改修 2026-07-21）。現地 L 壁で 2 点タッチ → ワイヤー重なり → 確定の一連、および再入場での Review 着地・サンプル進捗バー/触覚・残差表示を実機確認すること。show.json layout エディタ（Web 卓・Phase 2）は別作業。
- **登録ビュー（ZoneGridFootprint + ワイヤーフレーム）の検証ハーネス**（2026-07-22）: (1) `.\tools\unity.ps1 menu regviz`（Editor 多角度 PNG・Play 不要。show.json layout を注入し identity/登録後 × 真上/斜め/目線 を `Assets/Screenshots/regviz/` へ焼く。南北反転・変換ズレを机上で確認）、(2) `RegVizDebugDriver`（Development ビルド起動フック。`adb shell am start -e regviz 1 -n com.roiril.mawarimi/com.unity3d.player.UnityPlayerActivity` で起動 5 秒後に登録モードへ自動入場 → `adb exec-out screencap` で見た目確認。`[RegVizDriver]` タグでログ）。どちらも registration.json / show.json を書かない（`SetRegistration(save:false)` + `SetLayoutForPreview` 注入）。**2026-07-22 実機 screencap で grid タイルの両眼描画・配色を確認済み**（Editor プレビューでは配色/南北/変換追従も確認済み。立体視のスケール感・ワイヤーの重なり精度のみ現地の人間確認が残り）。`exec-out screencap` が connection reset する時は `shell screencap -p /sdcard/x.png` → `pull` 方式で。
  - **HMD 内テキスト StatusHud の位置・サイズ感も検証対象に追加**（2026-07-22）: 見た目構築は `MainDemoSceneSetup.CreateStatusHudVisual`（本番 `CreateStatusHud` と共有・数値定義は 1 箇所）。(1) の Editor プレビューは eye アングルのみ HUD を写して `regviz_<identity/registered>_eye_hud.png` を +2 枚焼き（既存 6 枚は不変）、パネル/1 行の見かけ角を `[RegVizPreview] hud angular: panel=…°x…° line=…°` でログ。Edit Mode は Update/LateUpdate が走らないので `SendMessage("Update"/"LateUpdate")` で内容解決・配置を手動駆動する。(2) の実機フックは **フェーズ 2**（`[RegVizDriver] viz ready` の 12 秒後）で登録退場 → ステータス表示を ON にし `[RegVizDriver] phase2 status hud shown` をログ（親がこのマーカーで 2 枚目の screencap を撮る）。Tracking→Diagnostics の asmdef 参照禁止を守るため実機フックは `GameObject.Find("StatusHud").SendMessage("SetVisible", true)` で ON にする。
  - **日本語フォント（このハーネスが即日拾った実バグと恒久修正・2026-07-22）**: TMP 既定 LiberationSans SDF に CJK グリフが無く、**登録ガイダンス・ステータス行の日本語が実機で全て豆腐**だった（過去の「実機で文字が出てる風」スクショは ASCII 断片だけが見えていた）。修正 = `Assets/Art/Fonts/SourceHanSansJP-Normal.otf`（SIL OFL・同梱可）から `.\tools\unity.ps1 menu hud-font` で **使用文字を事前ベイクした Static アトラス** `Assets/Resources/Fonts/JapaneseHud SDF.asset` を生成し、`JapaneseHudFont.TryGet()`（Diagnostics）が Resources から供給、`StatusHud.Awake` が差し替える。**HUD の文言（RegistrationGuidance / StatusHud / CourseRegistrationController）に新しい漢字・記号を足したらメニュー再実行**（文字集合はソースから自動収集）。実機 screencap でガイダンス・ステータスとも日本語可読を確認済み。罠 3 つ: ①可変フォント（NotoSansJP-VF 等）は CreateFontAsset が通ってもグリフラスタライズ全滅 → **静的フォント必須** ②この Unity/TMP 世代はランタイム OS フォント→CreateFontAsset が全滅（58 候補実測）→ 同梱ベイクが唯一の確実経路 ③Editor プレビューの HUD ミラー（TMP 3D）は Edit Mode 制約で CJK が豆腐のまま = **配置・サイズ検証専用**。可読性の一次証拠は実機 screencap で取る。

### コントローラ触覚（振動）フィードバック（2026-07-21〜）

HMD を体験者が被っている間はスタッフに視覚（StatusHud）が見えない。押下の受理・進行・発火・失敗を
**右コントローラの振動**で伝える（純ロジック [`HapticSequenceLogic`](../../Assets/Scripts/Input/HapticSequenceLogic.cs)
＝「経過時間→振幅」・EditMode テスト [`HapticSequenceLogicTests`](../../Assets/Tests/Input/HapticSequenceLogicTests.cs)、
MonoBehaviour [`ControllerHaptics`](../../Assets/Scripts/OvrBridge/ControllerHaptics.cs) が毎フレーム
`OVRInput.SetControllerVibration` を RTouch へ適用）。両アプリ共通仕様（TableDuo 側も同じボキャブラリ）:

| パターン | 意味 | 波形（freq 0.5 固定） |
|---|---|---|
| Ack | 監視入力のダウンエッジ受理（アクションに繋がらなくても鳴る） | 40ms・amp 0.25 |
| Action | 短押しアクション実行（カメラ Next / ステータストグル / 点サンプル確定） | 80ms・amp 0.5 |
| HoldTick | 長押しカウント進行 | 連続・amp 0.10→0.30 の progress 比例ランプ。⚠ **トリガー／グリップは頭 0.3 秒を鳴らさない**（`OvrControllerBridge.HoldTickDeadSec`・2026-09-11）。登録の A 0.5 秒ホールドは従来どおり 0 から鳴る |
| Fire | 長押し発火・モード遷移・確定保存 | 80ms×2（間 80ms）・amp 0.8 |
| Error | 失敗・拒否（登録の残差 NG やり直し） | 50ms×3（間 60ms）・amp 0.6 |

- **重畳優先度（固定仕様）**: 単発パターンは「ピーク振幅が厳密に大きい後着だけ差し替え、同ピーク・低ピークは再生中なら無視」
  （Ack→Action は昇格 / Fire 中の Ack は無視 / 確定保存の二重 Fire = RegistrationConfirmed と ModeChanged が 1 回に畳まれる）。
  HoldTick は連続の床として単発と **max** 合成。数値は SerializeField ではなく **const**（旧シーン YAML で 0 に読まれる罠回避）
- 配線: [`OvrControllerBridge`](../../Assets/Scripts/OvrBridge/OvrControllerBridge.cs) が down エッジ→Ack /
  Normal アクション→Action / 長押し進捗→SetHoldProgress / ModeChanged・RunReset→Fire。登録フローの節目は
  `CourseRegistrationController` の `PointCaptured`/`FitRejected`/`RegistrationConfirmed` イベントを Bridge が購読
  （Tracking asmdef は OVRInput 非依存を維持）。`haptics` 参照は null 許容（未配線でも全機能が従来通り動く）
- **接続表示**: StatusHud の接続行は**廃止**（2026-07-23）。コントローラ未接続の切り分けは ControllerGuidePanel が
  自動で消えることで行う（Bridge が `OVRInput.IsControllerConnected(RTouch)` を **ControllerGuidePanel へのみ** push。
  StatusHud への push は撤去）。**⚠ 2026-08-07〜はガイドパネル自体が「スタッフが見ているとき」しか出ない**ので、
  「押しても振動しない」の切り分けは**まず右 B を押す**（B で StatusHud とガイドパネルが同時に出る）。
  StatusHud 本文は 2026-07-23 に○×表記・cm 化へ改稿（カメラ健全性は `カメラ1○ カメラ2×`＝○届いてる/×届いてない・
  半角スペース 2 個区切り、位置残差は `位置合わせOK（ずれ 5cm）` の整数 cm。`[NORMAL]` 行頭・`信号 ●●○`・丸数字・接続行は廃止）。
  スタッフ向け操作早見表は右コントローラ追従の [`ControllerGuidePanel`](../../Assets/Scripts/Diagnostics/ControllerGuidePanel.cs)
  （Bridge が `PushModeLabel` で NORMAL/REG 本文を切替。位置合わせ中は門が自動で開くので REG 本文は従来どおり読める）。
  **インサート演出中に A を押すと手動切替は破棄**され、
  ⚠ ここに出していた赤 1 行（`ControllerGuidePanel.ShowTransient`）は 2026-08-16 に消した —
  唯一の呼び出し元だった A のカメラ手送りが 2026-08-12 に無くなっていたため（`canon/LEDGER.md` 0052）。
- **⚠ 実機未検証**（2026-07-21）。振幅・波形の体感、コントローラ未接続時のガイドパネル消灯は現場調整前提
- ⚠⚠ **A / B と重なったグリップ／トリガーの長押しは、離すまで数えない**（2026-09-11・
  `ControllerModeLogic.Frame.faceButtonHeld` / `FaceButtonQuietSec` 0.3 秒）。親指で A を押す手は
  中指で握り込むので、これを許すと**体験を始める A のたびに右の手元が震え続け、2 秒握っていれば
  ランリセットが撃たれる**（ユーザー報告「最初の A を押したとき右がしばらくバイブし続ける」）。
  経緯と切り分けは [memory/controller_input_final.md](../memory/controller_input_final.md)

### 体験の骨格（導入 → 3 周 → 終了）— 2026-07-29〜

企画書（学会論文版）の「3 区間を 3 周・導入を含め 3 分以内」を状態機械にしたもの。契約は
[streaming.md](streaming.md) の「体験の骨格」節が正本。Unity 側で押さえるべき点だけ:

- 相は `Intro` / `Run` / `Finished`。実行体 [`ShowRunDirector`](../../Assets/Scripts/Streaming/ShowRunDirector.cs) は
  `Setup Main Demo Scene` が [Tracker] に載せる（未配置なら ShowControlClient が実行時に自動生成する）。
- 導入・終了の間は **`CueScheduler.SetShowGate(false)`** で区間進行を止める。これ 1 点で演出・端末内録画・
  区間 post / BGM・実測滞在が全部止まる。**画面のカメラ切替は止めない**（導入では映像を出したいから）。
- 終了の黒は [`ShowEndingFader`](../../Assets/Scripts/Diagnostics/ShowEndingFader.cs)（CenterEyeAnchor 直下）。
  `StartupFader` は解除後に自分を Destroy するので再利用できない。
- StatusHud の 1 行目が相と経過を出す（`導入中 0:12` / `2周目/全3周 ・ 経過 1:05` / `体験おわり…`）。
  **HMD 内の文言を足したら `.\tools\unity.ps1 menu hud-font` を再実行**（忘れると実機で豆腐）。

#### 導入の中止は本当に中止になる（2026-08-06 修正）

トラッキング原点が変わると `IntroDirector.AbortIntro` が演出を畳むが、**それだけでは中止が中止に
ならなかった**。中止は `IntroLogic.Disable()` を通るので `Active`（`_stage != Off && != Done`）が
false へ落ち、`ShowRunDirector` が渡す `introPlaying` も false になる。`introMinSec` は起動から
数えていて設営でとうに過ぎており、体験者はスタート区間に居るので、**中止したその瞬間に
「時間経過 ＋ スタート区間に居る」が揃って本編へ飛んでいた**（ずれた座標で 3 周が始まり、
体験者は壁の位置が違う世界を手でたどる）。しかも警告は相で門を閉じていたので同時に消えていた。

- [`IntroDirector.Aborted`](../../Assets/Scripts/Streaming/IntroDirector.cs) を公開し、
  `ShowRunLogic.Tick(…, introAborted)` の `auto` 条件へ入れる。**明示操作（⏭）は通す**
  — 「ずれていても進めたい」は運営の判断で、コードが止める話ではない
- 視界は [`ShowRunDirector.ShouldBlackout`](../../Assets/Scripts/Streaming/ShowRunDirector.cs) →
  `ShowEndingFader` の黒が閉じる（終了 1.5s / 中止 0.4s）。新しい凍結ラッチは足していない
  （唯一のラッチは `_aborted` で、落ちるのは `BeginIntro` だけ）
- **復帰は 1 段**。`IntroDirector.TryRecoverFromAbort` が「位置合わせが確定し直された」を
  `CourseRegistrationStampProvider`（= `CourseFrame.SavedAtIso`）の変化で検出して導入をやり直す。
  ⚠ 要再登録フラグの false 化では代用できない（プレビュー `SetRegistration(save:false)` でも降りるので、
  B 確定の前に再開して登録ビューと演出が混ざる）

#### 位置合わせ中は「現実を隠すもの」を全部どける（2026-08-09）

位置合わせは**現実に線を重ねて合わせる作業**なのに、実機では現実が 1 画素も見えていなかった。
門は [`ShowControlClient.CourseRegistrationActive`](../../Assets/Scripts/Streaming/ShowControlClient.cs)
1 つで、配線は `OvrControllerBridge`（Streaming も Tracking も互いを参照しない規約なので、
両方を知っているそこが唯一の配線点）。立つのはコントローラを持つ人だけなので、体験の最中に勝手に開かない。

| どける対象 | どこで | なぜ |
|---|---|---|
| 導入演出の覆い | `IntroDirector.Update` が段送りごと凍結し `SetHidden` | 覆いは queue 4900 の `Blend Zero SrcAlpha`。走っているだけで**登録ワイヤーと文字（queue 3000）を黒へ潰す** |
| 中止・終了の黒 | `ShowRunDirector.ShouldBlackout` が false を返す | 中止の唯一の直し方が再登録なので、**黒が居座ると直す作業ごと隠れて詰む** |
| パススルーの電源と背景 alpha | `PassthroughStyler` | 演出の外では切られていた。現実は**カメラ背景 alpha=0** で出す（覆いは使わない → [meta-xr.md](meta-xr.md)） |

⚠ **位置合わせから抜けたら導入の開始合図を必ず武装し直す**（`IntroDirector._wasRegistering`）。
course 変換そのものが変わるので、**前の座標系で満たした合図は無効**。

#### 導入の開始は「入ってきた」で判定する（2026-08-09 に置き換え）

⚠ **円は状態、開始は事象。** 旧実装は「いま円の中に 0.5 秒居る」だけを見ていたので、
**起動直後に条件がたまたま揃うと演出が即座に走り出した**（ユーザー報告「体験者の位置を基準に
トリガーしてほしいが、今はすぐに起動してしまう」）。踏む経路は 2 つ:

- 頭のポーズがまだ来ておらず、course 原点付近が円の中にある
- 前の体験者が円の中に立ったまま、スタッフがランをやり直した（`BeginIntro` がラッチを落としていなかった）

→ 判断を [`StartSpotLogic`](../../Assets/Scripts/Streaming/StartSpotLogic.cs) へ出した
（純ロジック・テスト 9 本）。**外に居たことを観測してから、入ってきた滞在を数える**。
加えて `LineCrossLogic` と同じ 2 つの不連続ガード（dt が飛んだ / 1 フレームで 1m 超動いた）を持ち、
武装から 0.5 秒は軌跡を信用しない。

- **武装し直しは `IntroDirector.RearmStartSignal` 1 箇所だけ**（線の横断ラッチも円も接近もここで落とす）。
  呼ぶのは `BeginIntro`（ラン開始）と 位置合わせから抜けたとき と HMD を外したとき

##### ⚠⚠ 「近づいてきた」は距離の**縮み**で見る（2026-08-13・`canon/LEDGER.md` 0026）

体験エリアの footprint が解ける限り、開始の判定は**接近**（[`ApproachLogic`](../../Assets/Scripts/Streaming/ApproachLogic.cs)）で、
線も円も上書きされる。2026-08-13 まで武装の条件が「一度 `nearM + ArmMarginM`（1.35m）ぶん外へ出る」
**だけ**だったので、**体験者を立たせる場所が箱から 1.35m より内側にある現場では、導入が自動では
二度と始まらなかった**。しかも黙って待つので現場では「立っても始まらない」としか見えない。

いまは 2 つのどちらかで武装する:

| 武装 | 何 |
|---|---|
| 離れている | `outsideM > nearM + ArmMarginM`（従来どおり） |
| **近づいてきた** | 落ち着いてから観測した**いちばん遠い距離から `ApproachDeltaM`（0.35m）縮んだ** |

後者は**事象**なので、立ち止まっている人・エリア内に置いた HMD・前の体験者が立ったままのリセットでは
成立しない（防ぎたかった性質はそのまま）。頭の揺れ（数 cm）とは桁が違う。

不連続ガードは `LineCrossLogic` / `StartSpotLogic` と同じ 3 つ — **1 フレームで 1m 超動いた / dt が
0.5s 超飛んだ / 武装から 0.5 秒**。トラッキングの立ち上がり・recenter の飛びが「近づいてきた」に化ける。

⚠ **安全網: 外に居たことを見てから中に入ったら、段 0 は必ず抜ける**（`IntroLogic._sawOutsideBox`）。
段 0 でエリアの中に入ると重みは `shell=1 / sealBox=0` ＝ **真っ黒**に倒れるので、合図が成立して
いないと体験者は黒の中に立ったままになり、**スタッフの ⏭ 以外に出口が無い**。
状態（いま中に居る）ではなく事象（外から入ってきた）で判定するので、最初から中に居る人では走らない。

##### ⚠⚠ 自動の出口はすべて「人が始めた」でゲートする（2026-08-14）

段 0 の自動の出口（接近 / 安全網 / 下の救済）は **`IntroInput.startAuthorized`** を通る。
実体は **タイトルが画面を手放したか**（＝ スタッフが A を押して題字が焼け切ったか）で、
供給は `ShowControlClient.StartAuthorizedProvider`、配線は `OvrControllerBridge`。
**スタッフの ⏭（`RequestAdvance`）はゲートしない** — 人の判断そのもので、上書きする権利がある。

- **なぜ要るか**: タイトルが立って A を待っているあいだも段 0 は生きていた。
  **スタッフが HMD を持って体験エリアを横切るだけ**で安全網（外 → 中）が成立して段 1 へ進み、
  `TitleScreen` が「導入が段 0 を出た」を見て自分を強制終了する ＝ **題字が飛ぶ**
- **なぜ `UserPresentProvider` に混ぜないか**: 旧実装は 1 つの provider で
  `userPresent && !title.IsBlocking` を返していた。自動走行（`ShowWalkDebugDriver`）は
  **被り検知だけを外す目的で全体を true に上書きする**ので、走行ではタイトルを飛び越して
  導入が始まっていた ＝ **タイトル画面は自動走行で 1 度も検証されていなかった**。
  いまは走行側が `TitleScreen.RequestAdvance()` を送り、閉じ切るまで待つ（実機と同じ入り方）
- ⚠ **未配線なら true**（Editor・テスト・タイトルを持たない構成で体験が止まらない）。
  タイトルの実体を組めない現場でも `IsBlocking` が false になるので、
  **「タイトルが壊れると二度と始まらない」は構造的に作らない**

##### ⚠⚠ 箱の至近に立たされた体験者を救う（2026-08-14）

体験者を箱の面から `InsideEnterM`（0.55m）の内側に立たせると、段 0 の重みが真っ黒に倒れる一方で
**接近も安全網も原理的に成立しない**（接近は「0.35m 縮む」余地が無く、安全網は「0.85m 外に居た」を
要求する）。段 0 は `maxSec` の対象外なので時間でも抜けない ＝ **⏭ 以外に出口が無い**。
1.8m 四方の現場では体験者を箱の縁 0.5m に立たせるのが普通に起きる。

→ **人が始めた ＋ 位置が信用できる ＋ 黒が立ったまま `ConcealStartSec`（1.0 秒）** で段 1 へ。

- ⚠ **無条件の時間切れにしない。** 置いた HMD・前の体験者が立ったままのリセットで走り出す
  （`canon/LEDGER.md` 0005 が禁じた形）。防いでいるのは `startAuthorized`
- ⚠ **危険域は「最初から 0.55m 以内」だけではない。** 0.55〜0.9m から近づいて黒が立った所で
  止まっても同じ（縮み 0.35m に届かない）

##### ⚠⚠ 「中に居る」は 3 つの別々の意味を持っていた（2026-08-14 に分けた）

| 何 | 閾値 | 使い道 |
|---|---|---|
| `InsideBox`（黒の先行） | `InsideEnterM` 0.55m / `InsideExitM` 0.85m | 見え方（黒を箱の面より先に立てる。late-latch 対策） |
| **`PhysicallyInside`**（実際に面を越えた） | `DeepInsideEnterM` 0m / `DeepInsideExitM` 0.25m | **段 2 の進行条件**（中に入ってから管が点く） |
| **`IntroInput.outsideValid`**（位置が信用できる） | — | 中／外の判断を更新してよいか |

- 旧実装は 1 つの `InsideBox` が三役だったので、**箱の 0.55m 外に立っている人を「中に入った」と
  判定して管を点け始めていた**
- ⚠⚠ **`outsideBoxM = 0` を「解けない」の意味に使わない。** 旧実装は未登録・layout 未着でも 0 を
  返しており、ロジック側から「本当に中に居る」と区別できなかった ＝ **未登録の現場を中に居ると読む**。
  いまは `TryOutsideBoxM` が解けたかを戻り値で分け、解けないあいだは中／外の判断を更新しない

⚠ **HMD の合図は「実際に判定している方法」を言う**（`IntroDirector.PromptText`）。接近で始まる現場に
「床の印に立ってください」と出していた。近くで止まっていると 3 秒後に
「いちど下がってから、箱へ近づいてください」へ変わる。**文言を足したら `menu hud-font` を再実行**。
- `run.intro.startLineId` の線は **layout が届くまで毎フレーム引き直す**。旧実装は id 一致で
  二度と解かず、起動直後に `layout.lines` が未着だと**線が Undefined のまま固定**され、
  以後スタッフ操作でしか始められなくなっていた

#### 歩行誘導 — タイトルの直後、床の矢印と円で所定の位置まで歩かせる（2026-08-17）

世界観の逐語は `canon/LEDGER.md` 0079。**導入の段 0 の内側で回る別の層**で、
`ShowPhase` も `IntroStage` も増やしていない。

| | |
|---|---|
| 判断 | [`WalkGuideLogic`](../../Assets/Scripts/Streaming/WalkGuideLogic.cs)（純ロジック・テスト 15 本） |
| 道筋 | 同ファイルの `WalkGuidePath`（`layout.room` の壁の角 → 円と矢印の起点） |
| 実行体 | [`WalkGuide`](../../Assets/Scripts/Streaming/WalkGuide.cs)（シーンの `[WalkGuide]`・Logic 直下） |
| 形 | `Assets/Art/Shaders/Intro/WalkGuideArrow.shader` / `WalkGuideRing.shader`（queue 4960 / 4961） |
| 連絡 | `CommsCueLogic` の `Greeting` / `Walk`（**導入でも連絡の面が開くようになった**） |
| 見る | `.\tools\unity.ps1 menu walkguide`（`-Set show=<show.json>`） |
| 観測 | `ev=guide st=` ／ `ev=sum` の `guide=<組めたか>/<山形の数>/<矢印>/<輪>` |

段は **Trail（1.6s 山形が手前から 1 つずつ点く）→ SpotIn（1.2s 円が中心から回りながら開く）→
Hold（流れが回る）→ Arrive（0.7s 弧が閉じる）→ Out（0.6s）**。

##### ⚠⚠ 矢印は「エージェントが説明し始めた」から出る（2026-08-17・0079 の赤入れ 4）

出す順は**矢印 → 円**（それまでは円が先だった）。引き金は `WalkGuideInput.told` で、
供給は **`CommsPanel.Deliver` が ⓪b を出した瞬間**（`WalkGuide.NotifyExplaining`）。

- ⚠ **保険が要る。** 連絡の面が組めない現場（日本語フォントを解決できない・シェーダが剥がれた）では
  合図が永久に来ない。`TellTimeoutSec`（12 秒）待ったら誘導だけでも出す。
  ⚠ **7 秒より短くしない** — 通常経路（名乗り 0.45 ＋ 打つ 1.50 ＋ 読ませる 2.0 ＋ 引き 0.9
  ＋ 間 0.5）がおよそ 5.4 秒なので、保険が先に発火すると順序が崩れる
  （⚠ 読ませる尺は 2026-08-19 に 4.0 → 2.0 へ。0092）
- ⚠ **「1 つずつ」も「中心から」も、段の尺の配り方では 1 画素も変わらない。**
  実体はシェーダ側 — 矢印は `_Step`（山形 1 つぶんの `_Reveal` の刻み。`WalkGuide` が書く）を読んで
  「自分の番が来てから刻みの 55%」で立ち上がり、円は弧ごとに半径を 0 から広げながら 0.30 周だけ余分に回る
- ⚠ **`_Step` を渡す側と読む側は対。** 渡していても Properties に無ければ**黙って無視される**
  （実際 2026-08-17 に半日そうなっていて、画は旧来のなだらかな帯のままだった）
- ⚠ **円の開きは `ring` に線形で渡す**（緩急はシェーダが持つ）。両方で均すと二重に掛かる
- 観測は `ev=guide` の **`told=`**（0 なら保険で出た ＝ 説明と対になっていない）。
  ⚠ **画からは区別できない**のでここにしか手掛かりが無い

##### ⚠⚠ 円を描いたら、開始の判定もその円でなければならない

導入の既定の開始は体験エリアへの**接近**（境界の 1.0m 外・`ApproachLogic`）。
円を出したまま接近で始めると**指示された所より手前で演出が走る** ＝ 装置が出した指示が嘘になる。

⇒ 誘導が「行け」と言っているあいだ（`WalkGuideLogic.Directing`）、`IntroLogic` の
**接近・安全網（外 → 中）・救済（中に立ったまま 1 秒）は全部止まる**（`IntroInput.guidingToSpot`）。
段 0 の自動の出口は「円へ 0.5 秒留まった」1 つだけになる。

- ⚠ **スタッフの ⏭ はゲートしない。** 人の判断そのもので、上書きする権利がある（2026-08-14 の規約と同じ）
- ⚠ **止めたぶんの出口は誘導が持つ。** 30 秒（`WalkGuideLogic.HoldMaxSec`）着かなければ誘導を畳んで
  従来の判定へ戻す（`TimedOut`）。**ここを消すと「立っても始まらない」が復活する**
- ⚠ **円の判定は「一度外に居た」を要求しない**（`StartSpotLogic` と違う）。誘導は
  スタッフが A を押した後にしか出ないので、人の判断という門は既に通っている。
  要求すると、たまたま円の上に立たされた体験者が 30 秒置き去りになる

##### 道筋の解き方（捏造しない）

1. **床が無ければ何も出さない**（`layout.floor` → `room.floorW/D`）。実物に無い所へ歩かせない
2. 円は **`layout.startSpot` があればそれ**（卓の 🎬 開始位置。半径もその値）。無ければ
   **壁の角から 2 本の道（壁から `LaneOffsetM` = 0.42m）の交点**へ導出し、
   半径は `SpotRadiusM` = **0.245m**（2026-08-17 のユーザー赤入れで 0.35 の 0.7 倍へ）
   - ⚠⚠ **見た目だけ縮めない。** 判定の円を大きいままにすると輪の外で導入が始まり、
     「そこへ立て」という指示と食い違う。**描く輪と判定の円は 1 つの値**
3. 矢印は角の**東側の腕**（先の x が大きい方）の道に沿って、**床の縁の 0.55m 外**（`StartOutsideM`）まで
   - ⚠ **床の中で止めない。** 体験者は床の外に立っているので、起点が足元より先にあると
     「ここから行く」が画に出ない（実測で円のすぐ手前に山形が団子になった）
4. 山形は 1 メッシュ（quad を並べたもの）。いちばん手前は**輪のいちばん外の弧**
   （`RingOuterK` = 1.40 × 半径）から `RingGapM` 離す

##### 描くときの罠

- ⚠⚠ **alpha は 0 を返す**（加算合成）。1 を返すとパススルーが出ている段 0 で**現実に穴を塞ぐ**
  （`AnomalyEyes` と同じ）
- ⚠ **queue は覆い（`IntroVeil` 4900）より後・5000 以下。** 前に置くと `Blend Zero SrcAlpha` で
  rgb ごと 0 に潰され、5000 を超えると URP の透明パスに入らない（`rules/unity-vr.md`）
- ⚠ **シェーダの変数名に `line` を使わない**（HLSL の予約語）。頂点シェーダが構文エラーになり、
  **既定のマゼンタ**で出る。実測で 1 回踏んだ
- ⚠ **どちらのシェーダも実行時 `Shader.Find`** → `ProjectSettings/GraphicsSettings.asset` の
  Always Included に登録済み（外すと Editor では出て実機だけ剥がれる）
- ⚠ **`WalkGuide.QuadK` と `WalkGuideRing.shader` の `QUAD_K` は同じ値**（片方だけ直すと輪がずれる）
- ⚠ **着いた合図の波を quad の外まで走らせない。** 縁で切られて**角の丸い四角**が光る（実測）

##### 自動走行も円まで歩く

`ShowWalkDebugDriver` は接近の後に `WalkGuidePath.Solve` で円を解いて歩いていく。
**タイトルを `RequestAdvance` で閉じるのと同じ理屈** — 走行側が実機と同じ入り方をする。
入れないと、走行のたびに 30 秒の時間切れを待つことになる。

##### 連絡の 3 通（⓪a / ⓪b / ⓪c）

`CommsPanel` は**導入でも開く**ようになった（それまで本編だけ）。出るのは段 0 と、
段 0 を抜けた縁の⓪c まで。**タイトルが画面を持っているうちは 1 文字も出さない**。

⚠⚠ **文面は 2026-08-19 に全面改稿した**（`canon/LEDGER.md` 0096）。逐語は台帳。

- ⓪a「私は調査を支援する／エージェントです」 ← **17 文字なので 2 行へ割ってある**
  （帯に入るのは 14.8 文字。任せると語の途中で切れる）。2026-08-23 に
  「私は調査支援エージェントです」から替えた（0124）。
  ⚠⚠ **「AI」とは書かない**（0080）。⚠ **紙の依頼書と同じ語**
  （`docs/onsite/handout.html`「調査を支援するエージェント」）。片方だけ直すと別のものに見える。
  ⚠ **`LongestNoticeText` は⓪a ではなく①b**（この改稿で最長行が移った）
- ⓪b「開始ポイントを／マークしました。／矢印から向かってください。」
  （18 秒おきに**あと 2 回まで**出し直す）。
  ⚠⚠ **この連絡が床の矢印を出す**（`CommsPanel.Deliver` → `WalkGuide.NotifyExplaining`）。
  文面を出す所と矢印を出す所は**同じ 1 行**にしてある — 分けると片方だけ動いたときに黙って食い違う。
  ⚠ **⓪a を読ませ終わった縁で間を置かずに来る**（同じ面のまま文面だけ替わる）
- ⓪c「到着しました。／観測装置を起動します。」 ← **段 0 を抜けた縁**（＝ 導入演出が
  始まるのと同じフレーム）。⓪b が出ていれば**引かずに上書きする**。読ませるのは 2 秒 ＝ 割れる段に掛からない
- ①「異変を見つけたら／ボタンを長押ししてください／装置が解析して対処を試みます」
  →（読ませ終わった縁で）①b「調査を開始してください。」
  ⚠⚠ **表示を時間で分けてある**（0097）。押し方の説明と開始の合図を同時に読ませない。
  ⚠⚠ **順番は 2026-09-06 に入れ替えた**（`canon/LEDGER.md` **0174**・ユーザー指定
  「調査を開始してくださいと、異変を見つけたらボタンを長押ししてくださいの順番を逆にしよう」）。
  **enum の名前（`BeginHow` / `Begin`）は文面の名前なので入れ替えていない** —
  走行ログの `ev=comms id=` と解析器がその綴りで繋がっている。
  ⚠⚠ **①の 3 行目が「押すと何が起きるか」**。報告を「対処」へ繋ぐ唯一の説明で、
  ③の「排除してください」はこれを読んだ前提に立つ

⚠ **文面を足したら `menu hud-font` を再実行**（静的ベイク・忘れると実機で豆腐）。
⚠ **全部の文面を機械で測る**（`CommsNoticeTextTests`）。`LongestNoticeText` は**最長の行**を持つ文面で、
**最大の行数を持つ文面とは限らない**（この改訂でそれが別々になった）。

#### 隔離殻 — 会場を消して、壁と床と自分だけを残す（2026-08-10）

世界観の出どころは `.claude/canon/LEDGER.md` 0002（怪異調査員が、隔離された呪われた壁を調べに来る）。
実装は [`ContainmentShell`](../../Assets/Scripts/Streaming/ContainmentShell.cs) ＋
[`ContainmentShell.shader`](../../Assets/Art/Shaders/Intro/ContainmentShell.shader)。

**やることは 1 つだけ**: パススルーに写る会場（人・机・天井・他の展示・スタッフ）を黒で落とし、
**実物の壁と足元の床だけ**を残す。これで導入は「装置の前に立たされた」から
「隔離された部屋に入れられた」に変わる。設営の見苦しさが消えるという実利も同じくらい大きい。

⚠⚠ **パススルーには深度が無いので、「境界の向こうだけ隠す」は screen-space では原理的に書けない。**
手前の実物（壁）と奥の会場は同じ画素に重なっていて、区別する情報がフレームバッファに無い。
だから隠す / 残すは**著作した幾何**で決める。実装は 2 パス:

1. `ContainmentShellMask`（queue 4905）が「見てよいもの」の箱を描く。
   **色も深度も書かず、ステンシルに 1 を置くだけ**
2. `ContainmentShell`（queue 4910・全画面 1 パス）が、その外側だけを黒で塗る

| 箱 | 出どころ |
|---|---|
| 床の板 | `layout.floor`（＝ 歩ける範囲そのもの。**余白 `FloorMarginM` は 0**） |
| 実物の壁・箱 | `layout.room` を `ShowRoomProxyLogic.Build` で起こし `BoxInflateM` = 0.08m 膨らませたもの |

⚠⚠ **全画面で「視線 × 箱」の交差を解いてはいけない。** 最初そう書いて、除算が画素あたり数十回入り
**導入が 90fps → 39fps に落ちた**（実測 `logs/capture/20260810_161524`。同条件の 08-07 の走行は 90/88fps）。
箱をそのままラスタライズすれば同じ形がほぼ無料で出るうえ、**眼ごとの投影も縁の MSAA も
Unity 側が面倒を見る**。全画面 1 パスで幾何を解きたくなったら、まずこの実測を思い出すこと。

- **箱は `ShowRoomProxyLogic.Build` から取る**（CG のオクルーダ・段 3 の線と**同じ幾何**）。
  別経路で組み直すと、人形は壁の裏へ回れるのに隔離だけ壁を無視する、が起きる
- ⚠ **捏造しない。** 床も部屋も未著作なら殻ごと出さない。既定の 1.8m 四方で黒を落とすと、
  体験者は自分の足元が消えた状態で歩くことになる。同じ理由で**未登録のときも出さない**
  （course→world が identity へ落ち、現実の全然違う所を隠す）
- ⚠ **下ろした手は残るが、上げた手は消える。** 手そのものは幾何に入っていないので、
  「視線が手を越えて床へ抜ける」＝下ろした手・卓上の手だけが見える。仕様として知っておく
- ⚠ **黒の縁と実物の縁は、位置合わせの残差ぶんずれる。** 壁を膨らませてあるので、ずれは
  「壁が欠ける」ではなく「実物の周りに会場が細く覗く」へ倒してある

**閉じるのは線を越えた瞬間**（段 1 = `Real` の 1.5 秒で 0 → 1）。段 0（開始待ち）は現実のまま —
そこはスタッフが被せて誘導する区間で、会場が見えていないと運用が成立しない。
**開けるのは終幕の `Restore`**（色が戻るのと同じ速さ）。開けないと体験者は黒い箱の中に置き去りで終わる。

⚠ **殻はパススルーより濃くしない**（`IntroWeights.shell <= passthrough`）。殻は
「見えている現実のうち見せてはいけない所」を潰す層なので、映像へ移り切った後に残すと**画面の映像まで黒く塗る**。
`ContainmentShellLogicTests.Intro_ShellNeverOutlivesPassthrough` が全段で固定する。

⚠ **描画順は覆い (4900) の後・線と文字 (5000) の前 = 4910。** 覆いは `Blend Zero SrcAlpha`
（結果 = dst × srcAlpha）を全画面へ掛けるので、先に描くと黒ごと 0 に潰れて 1 画素も残らない。
**5000 を超えてもいけない**（URP の透明パスは [2501, 5000] しか描かない）。

⚠ **ステンシルは URP が使っていない**（`URP-*-Renderer.asset` の `overrideStencilState: 0`）。
ここを誰かが使い始めたら、印と黒の 2 パスが黙って壊れる。

#### 【**退避した**】封印の箱 — 外から見た隔離（2026-08-10 / 2026-08-15 に外した）

⚠⚠ **体験には出ない。** 設定が「隔離された壁」から**回収されて会場に在る壁**へ変わり
（`canon/LEDGER.md` 0040 / 0044）、体験エリアを隠す必要がなくなった。

**消していない。** 実装・シェーダ・版・プレビュー・テストは `Attic/` へ移してある
（`.meta` ごと動かしたので GUID は同じ ＝ シーンの参照も Always Included の登録も生きている）。
**場所と、戻すときに読むもの・直す 9 箇所は
[reference/attic-sealed-box.md](../reference/attic-sealed-box.md) が正本。**

- 重み `IntroWeights.sealBox` は**全段 0**。`IntroDirector` は毎フレーム `SetHidden()` を呼ぶ
- 唸り `bed_seal` も黙らせた（3D で箱の面に定位させていたので置く先が無い）
- テレメトリ `box=` / `boxBuilt=` は外した（常に 0 が並ぶと誤検出の材料になる）
- ⚠ **シーンの `[SealBox]` / `[SealBoxShadow]` はまだ在る**（2026-08-15 は `Main.unity` を
  別のシュビーが編集中だったため触っていない）。描画は上の 2 つで止まっている

#### 段 4 — 現実が割れてスクリーンへ入る（2026-08-12 / 2026-08-15 に**復帰**）

⚠⚠ **2026-08-13〜15 は眠っていた**（箱の中に入る運用では成立しなかったため）。
封印の箱を退避して構成が戻ったので、**また走る**（`canon/LEDGER.md` 0044）。

⚠ **割れるのは覆い（パススルー）だけ。** 箱が主役だったころは進みを覆い 0.35 / 箱 0.65 に
分けていたが、割る相手が無くなったので**進みをまるごと覆いへ渡す**
（`IntroShatterCurve.VeilShatter` が恒等）。箱の側の曲線とメッシュは Attic に残っている。

⚠ **箱が居たころに書いた「主役は封印の箱」以下の記述は、いま反転している** —
覆いが視界の全部なので、覆いだけ割れば画は十分に埋まる（`menu intro` の
`intro_4_frame_*.png` で確認済み）。

段 4 は「周縁から黒がなめらかに寄せる」だった。いまは**見えているものが細かなセルに割れて、
スクリーンの矩形へ吸い込まれる**。スクリーンは現実を映す窓ではなく、現実を飲む口になる。

- **割れるのは覆い（パススルーが覗く窓）。** 箱が居たころは視界のほぼ全部が箱で、
  覆いだけ割っても画にほとんど出なかった。箱が無くなったので、いまは覆いが視界の全部
- 箱は実体なので、破片は**模様を持ったまま飛ぶ**（シェーダが模様を**ホームの座標**で引く）。
  覆いのセルは「現実が覗く窓」で中身は動かないので、**回りは小さく**してある（±20°）——
  大きく回すと「マスクが回っている」と読まれる
- 数値の正本は [`IntroShatterCurve`](../../Assets/Scripts/Streaming/IntroShatterCurve.cs)、
  式は [`IntroShatter.hlsl`](../../Assets/Art/Shaders/Intro/IntroShatter.hlsl) **1 本**を
  覆いと箱が include する。マテリアルへ書くのも `PushVeil` / `PushBox` の 1 箇所だけ

⚠⚠ **順番が意味を持つ。パススルーを閉じ切ってから箱を割る**（`VeilPhaseEnd` = 0.35）。
箱は不透明なので、その裏のパススルーが開いたまま箱に割れ目が入ると
**体験エリアの中が覗ける**（LEDGER 0005 が禁じたもの）。区間を重ねないので覗きは起こりえない。
`IntroShatterTests.VeilPhase_FinishesBefore_BoxPhaseStarts` が固定する。

⚠ **開口（`frame`）は破砕より遅れて閉じる**（`IntroLogic.FrameCloseAt` = 0.70）。開口は
覆いのセルも箱の破片も切るので、飛んでいる最中に閉じると通り道でぷつりと消える。
逆に**開口を無視すると、枠の外に空いた穴から中が覗ける**（箱は開口で切られていて穴の向こうに居ない）
— だから「切る側に倒す ＋ 開口を後ろへ寄せる」の組み合わせが正解で、片方だけでは壊れる。

⚠ **スクリーン矩形の上にホームを持つセルは割らない。** 段 4 の終わりに枠の中まで消えると、
枠がどこにあるか分からないまま段 5 の映像が点く。終わりの画は従来どおり「黒 ＋ 枠の中に現実」。

⚠ **縮みは寄った分に比例させる**（`shrink = (1 - travel) * …`）。放射状に集まるので寸法を保つと
行き先の近くで破片が重なる。覆いは乗算ブレンドで**穴が必ず勝つ**ため、重なると「破片が集まる」ではなく
**1 つの大きな穴に融合する**（吸い込まれる画が出ない）。

⚠ **セル格子は `shatter > 0` のあいだだけ張る。** 1 セル = 独立した quad なので、隣り合う辺が
浮動小数で 1 ulp ずれると髪の毛ほどの黒い格子が現実に出る。段 1〜3 は 1 枚 quad のままにして、
「割れる」という段 4 の合図を先食いさせない。箱も同じ（段 0 は 15 秒以上あるので、
そのあいだ 27,000 頂点を流す理由が無い）。

**見る手段**（シェーダの誤りは `unity.ps1 test` に 1 件も出ない）は
**`.\tools\unity.ps1 menu intro` の `intro_4_frame_*.png` 5 枚**。
⚠ `menu shatter` は**箱の破片だけ**を見る絵なので、いまは退避中（`Attic/`）。

⚠ このプレビューは**パススルーの合成まで真似てある**。覆いは `Blend Zero SrcAlpha` で
「現実を出す＝ alpha を 0 にする」だけなので、素直にカメラで撮ると**現実が出るはずの所が真っ黒**になり、
判断が丸ごと逆になる。実機と同じ `最終 = アプリの rgb + 現実 × (1 - アプリの alpha)` を CPU で解いている。

観測は **`shat`（覆いの到達点）/ `shatC`（セル数）/ `shatRect`（吸い込み先）**。
`ShowTelemetryHost` と `analyze-xp-log.py` を**対で**直す。段 Frame に達したのに `shat` が
0 のままなら、進みは配っているのに 1 画素も割れていない（FAIL）。

#### 終幕（黒のまま装置が力尽きて、報告を出して終わる）— 2026-08-15 に作り替えた

⚠⚠ **「導入の逆再生でパススルーへ戻す」5 段（Warm / Unswap / Open / Restore / Hold）は廃止した**
（`canon/LEDGER.md` 0048・ユーザー逐語「パススルーには戻さず、背景が黒いまま、スクリーンが、
電池が切れかけみたいな感じでだんだんとちかちかしながら消えていき、最後に…書いて終了にしてほしい」）。

判断は [`OutroLogic`](../../Assets/Scripts/Streaming/OutroLogic.cs)、配線は
[`OutroDirector`](../../Assets/Scripts/Streaming/OutroDirector.cs)（`IntroDirector` と同じ GameObject）。

⚠⚠ **2026-08-23 に消え方を作り替えた**（`canon/LEDGER.md` 0111・ユーザー逐語「テンポが悪く、
終わったかがわかりずらい」「ちかちかする演出は目に悪いのでやめたい」）。
設計は [reports/2026-08-23_outro-redesign.html](../../reports/2026-08-23_outro-redesign.html)。

| 段 | 既定 | 何が起きるか |
|---|---|---|
| `Collapse` | **0.9s** | **ブラウン管の電源断。** 画が縦に潰れて横一本の線になり（0.35s）、線が中央へ縮んで点になり（0.30s）、残光が消える（0.25s）。**単発・一方向・戻らない** |
| `Dark` | 1.2s | 何も無い黒。間 |
| `Report` | 1.5s | 報告の 4 行が**1 字ずつ打たれ始める**（打鍵音つき） |
| `Done` | — | **文字を出したまま次のランを待つ**（`Active` は落ちるが `Presenting` は立つ） |

合計 **3.6 秒**（旧 8.7 秒）。`run.outro` の `collapseSec` / `darkSec` / `reportFadeSec` で調整。
**報告の 1 文字目まで 2.1 秒**（旧 7.2 秒）、**打ち終わりまで 5.5 秒**（旧 10.6 秒）。

⚠⚠ **「ちかちか」（`Flicker`・6〜24Hz の明滅・6 秒）は廃止した。** 戻さない理由が 2 つある:

1. 6〜24Hz は**光過敏性発作の危険帯**（3〜30Hz）のただ中。VR で視界の中心を占める面で
   6 秒間やってよい速さではない
2. **明滅は往復する動きなので、原理的に終端に読めない。** しかも体験は 3 分間
   「画面が変になる ＝ 異変」を教え続けているので、**その語彙で終わろうとすると
   最後の 1 手が異変と区別できない**

⚠ **意味は「装置が調査を終えて自分で落ちた」。** 誰かが外から切った、にはしない —
外部の意図を示唆すると「まだ何かいる」と読まれ、直後に死んだ装置が報告を打つ矛盾も立つ。
だから**物理スイッチの音も足さない**（新しい音源は 1 本も増えていない）。

⚠ **`darkSec` は詰めていない。** 問題にされたのは 6 秒の側で、この「間」は報告に重みを与える。
両方詰めると事務的になる。

⚠⚠ **報告を打ち切るのは段 `Done` より後**（打つ尺 3.4 秒 > `reportFadeSec` 1.5 秒・
`canon/LEDGER.md` 0063）。面は次のランまで消えないので体験としては問題ないが、
**「終幕が終わった」を打鍵の完了と読めない**。到達したかは `ev=sum` の
`repShown` / `repTypeN` でしか取れない。

- ⚠⚠ **パススルー・覆い・隔離には触らない。** 本編の時点で背景は既に黒なので、
  **何もしないことが「黒のまま」**。始めるときに覆い・隔離・乱れを畳むだけ
- **動かすのは 2 本**（どちらも書く先は `MjpegScreen` の Renderer の材質・writer は `OutroDirector` だけ）:
  - **`_ScreenPower`（0..1）** = 装置に届いている電力。`ScreenComposite` の**いちばん最後**に
    掛かるので、映像も砂嵐も時計も管の縁も一緒に落ちる。**潰れているあいだは 1 のまま**で、
    `Dark` 以降 0（消え方は下の 1 本が持つ）
  - **`_ScreenCollapse`（0..1）** = 電源断の進み。**形（潰れる → 縮む → 消える の割合・
    線の太さ・明るさの上限）はシェーダが持ち、C# は進みだけを出す**（数字を 2 か所に書かない）
- ⚠⚠ **既定が逆向き**（power = 1 が正常 / collapse = 0 が正常）。終幕を畳むすべての経路
  （終わった / ラン開始 / 相が変わった / `OnDisable`）で **`OutroDirector.ResetScreen` を通す** —
  3 つ（電力 1 / 潰れ 0 / 時計を出す）を必ず一緒に戻す。**別々に戻す実装にしない**
  （片方だけ書き戻す経路が必ず生まれる。「凍結が解けない」を 4 回踏んでいる）
- ⚠⚠ **潰れるのは「画」で、管のガラスではない。** シェーダは frag の冒頭で uv を逆写像し、
  **`CrtSdf`（角の丸み・縁の暗さ）だけは元の枠の uv を使う**。潰れた uv を渡すと `fwidth` が
  1/squeeze で発散して、角を切る `saturate(-d/aa)` が**線そのものを黙って消す**
- ⚠ **時計（OSD）は潰れの頭 12% で消す**（`OutroDirector.OsdCutAt` → `ScreenOsd.Suppressed`）。
  潰れた座標のまま矩形を引くと暗黙の微分が飛んで**灰色の帯**になる（字ではなく汚れに見える）。
  装置ごと落ちるときに時計も消えるのは筋が通る（`rules/streaming.md` が禁じる「時刻の嘘」は
  巻き戻し・停止・加速のことで、これには当たらない）
- ⚠ **輝度補償に上限がある**（`COLLAPSE_GAIN_MAX` = 1.25）。潰れると光が細い帯に集まるので
  明るくなるが、エネルギー保存にすると 167 倍 ＝ 暗所の VR の視界中心に閃光を置くことになる。
  **1.25 は「白飛びさせない」から決めた実測値**（素の画の尖頭 0.797 × 1.25 ≒ 1.0）。
  2.5 で焼いたときは白飛びが 0 px → 537 px へ増えた
  - ⚠ **効いているのは post であってプレートではない。** カメラを据え直した別構図のプレートで
    測り直しても尖頭は 0.797 のままだった（post の露出・コントラストが天井を決める）。
    ⇒ **プレートが変わっても測り直しは要らない。`show.json` の global post を変えたら測り直す**
- **乱数を使わない**ので同じ版は同じ絵になる（進みは単調に増えるだけ・往復しない）
- **報告の 4 行**は [`OutroReport`](../../Assets/Scripts/Diagnostics/OutroReport.cs)（文言は
  [`OutroReportText`](../../Assets/Scripts/Diagnostics/OutroReportText.cs)）。**体験前の注意書き
  （`TitleNotice`）と対の面**で、置き場・深度の逃がし方を揃えてある（2.6m /
  TMP Overlay の `ZTest Always` / queue 5000）。
  ⚠⚠ **大きさはもう揃っていない**（2026-09-05・`canon/LEDGER.md` 0161）。注意書きだけ
  ユーザー指定で 2/3（本文 1.2°）にした。**報告は本文 1.8° のまま** — 揃え直さない。数は `ShowControlClient.VisitorMarkCount`、
  **全角数字**（紙の「観測者番号 ０３７」と揃える）
- ⚠⚠ **報告は 1 字ずつ打たれ、1 字ごとに打鍵音が 1 発鳴る**（2026-08-16・`canon/LEDGER.md` 0063）。
  速さは **`CommsPanelLogic.CharsPerSecFor`（日本語 12 / Latin 18 文字/秒）をそのまま使う** —
  同じ装置の印字なので、連絡の面と違う速さで打つと装置が 2 台あるように聞こえる
  （⚠ **言語では変わる**・0149。1 人が浴びるのは 1 言語だけ）。音の正本は `rules/sound-design.md` §4
  - ⚠ **不透明度のフェードは持たない。** 打鍵そのものが出現の演出で、重ねると頭の数文字だけ
    薄いという半端な絵になる。`reportFadeSec` は**段の長さ**としてだけ効く
  - ⚠ **揃えは左。ただし字の塊は視界の中央へ運ぶ**（`textBounds` の重心を x も y も中心へ）。
    左寄せの意図は行頭が揃うことで、塊が視界の左に寄ることではない
  - ⚠ **縦も上寄せ**（`Left` ＝ 縦中央 は使えない）。1 字ずつ出すと、行が増えた瞬間に TMP が
    「見えている行数」で縦中央を取り直し、**打ち終わった行が上へ跳ねる**（連絡の面で実測 38px）
  - ⚠ **改行では鳴らさない**（`maxVisibleCharacters` は改行も 1 文字として数える）。
    報告は改行を 4 つ持つので、鳴らすと「字が出ていないのに 1 発鳴る」が 4 回起きる
  - 観測は `ev=outro` の `repChars=` / `repSfx=` と `ev=sum` の `repTypeN=` / `repShown=`。
    **`ShowTelemetryHost` と `analyze-xp-log.py` を対で直す**
- ⚠ **`OutroReport` は `StaffViewing` の門を通さない**（体験は既に終わっていて、この 4 行が
  「HMD を外してよい」を伝える唯一の手段）。`HmdTextGateTests.OutroReport_IsNotGatedByStaffViewing` が固定する
- ⚠ **終幕が有効なら終了で黒を出さない**（`ShowRunDirector.ShouldBlackout`）。
  `ShowEndingFader` は 0.3m ＝ 全部の面のうち最も手前なので、重ねると消えていく過程も報告も隠れる
- ⚠ **`PassthroughStyler` は終幕を読まなくなった**（読むと真逆になる — 終幕の頭で現実が立ち上がる）。
  `OutroDirector.PassthroughReadyProvider` / `OutroLogic.WarmMaxSec` / `OutroInput` も消えている
- ⚠⚠ **音は電源が落ちる音 1 本だけ**（2026-08-23・`canon/LEDGER.md` 0125・ユーザー指定）。
  終幕の頭で `sfx_power_off` が 1 度鳴り、1.29 秒で消える。そのあとは
  3 分鳴り続けた装置の声が引き、そのぶん部屋の音が前へ出て、
  `Done` で無音へ落ちる（`rules/sound-design.md`「終わりに音を残さない」は生きている）。
  ⚠ **2 本目を足すなら世界観の判定が要る**（`SoundCueLogicTests.Outro_FiresOnlyThePowerOff_AndOnlyOnce`）。
  `SoundCue.ShellOpen` は enum と音源を残したまま**鳴らさない**
  - ⚠⚠ **音は段を読まない**（2026-08-23）。旧実装は `st != OutroStage.Flicker` で切っており、
    **段名を変えた瞬間に終幕が丸ごと無音**になる形だった（画は正しく落ちるので実機で聴くまで
    気づけない）。いまは終幕の頭からの経過（`SoundShowState.outroElapsedSec`）だけを読む
  - ⭐ **音は画より少し遅れて終わる**（画 0.9 秒 / 装置の声 1.5 秒）。同時に切ると
    「ぶつっと切れた」になる。正本は `rules/sound-design.md`
- `run.outro` のキーが無い show.json では JsonUtility が `enabled=false` に化けるので、
  `ShowOutroDef.LooksUnset()` で検出して既定へ落とす（導入と同じ罠・同じ手当て）。
  **旧キー（`flickerSec` / さらに古い `unswapSec` 等）しか持たない show.json も、
  新キーが 0 → `Sanitized()` が既定へ倒すので走る**
  - ⚠⚠ **`collapseSec` だけ許容が狭い**（0.4〜2.0 秒・`OutroTiming.PickCollapse`）。
    旧キーの値（`flickerSec: 6.0`）を**誰かが新キーへ写しても通さない** — 電源断は
    一回性の事象で、2 秒を超えたらそれはもう別の演出

**見る手段**（シェーダの誤りは `unity.ps1 test` に 1 件も出ない）:

```powershell
.\tools\unity.ps1 menu outro
```

31 コマ ＋ 素 1 枚を `Assets/Screenshots/outro/` へ焼き、**尖頭・白飛びの画素数・平均**の推移を出す。
⚠ **眩しさは平均では測れない** — 潰れると光る面積が 1/167 まで減るので、輝度補償が何倍掛かっても
平均は必ず下がる（実測: 素 0.169 → 線 0.002）。判定に使うのは**尖頭と白飛びの画素数**。
⚠ **数値が緑でも絵を必ず開く**（管のガラスが一緒に潰れていないか・時計が灰色の帯として
残っていないかは、絵でしか分からない）。

**観測**は `ev=outro` の **`cl=`** と `ev=sum` の **`clMax=`**。
⚠⚠ **`ev=outro` の `cl` は必ず頭の値（≒0）**（段の縁でしか出ないため）。潰れ切ったかは
**`clMax`（走行全体の最大値）**でしか取れない。
⚠⚠ **この観測が無いと「潰れなかった」を誰も検出できない** — 段は正しく進み `pw` も
`Dark` 以降 0 になるので、書けていなくても他の判定は全部 PASS する（画は
「0.9 秒ふつうに映ってから黒へ瞬断」になり、暗い現場では目視でも同じに見える）。
判定は `analyze-xp-log.py` の「## 終幕」節。**`ShowTelemetryHost` と対で直す**。
- **`run.outro.lineId` は未実装**（スキーマだけ）

##### 終幕の合図は「著作した演出が終わったこと」（`run.outro.afterTakeId`）

判断は [`EndingCueLogic`](../../Assets/Scripts/Streaming/EndingCueLogic.cs)（純ロジック・テスト 10 本）、
配線は `ShowRunDirector.Update`。**ユーザーが「周回リセットのときにリセットされるフラグ」と
名指ししたもの**（`canon/LEDGER.md` 0048）。

- `run.outro.afterTakeId` が指す演出が**走っているのを見て**、そして**走らなくなったら**撃つ。
  撃つと `ShowRunLogic.RequestFinish()` ＝ 相が `Finished` へ落ち、`OutroDirector` が始まる
- ⚠ **「走っていない」だけを見ない。** 演出が始まる前も `ActiveTakeId` は空なので、
  空だけを見ると本編に入った瞬間に撃つ
- ⚠ **落ちるのは `ShowRunDirector.BeginRun`（周回リセット）だけ。** 1 回撃ったら再武装しない
- ⚠ **これは出口を増やしただけで、従来の終わり方は 1 つも外していない**
  （`endGraceSec` / `endHoldMaxSec` / `hardLimitSec`）。指した演出が最後まで走らない現場でも必ず終わる
- **いまの `show.json` の `L4C0#0` は最後のカットが `sec` の尺を持つ**ので、帰りの A に
  立ち止まっている体験者でも自分から終わる（旧記述の「次にカメラが切り替わるまで」＝
  安全網の側からしか終われない形は 0084 で解消済み）。尺は
  **報告から 7.1 秒**（`canon/LEDGER.md` 0156）:

  | 報告から | 何が起きるか |
  |---|---|
  | 0.00s | 呪いが排除される（`ScreenDecayLogic.Release`）。人形の画が畳まれて `live` へ |
  | 〜1.20s | 画の劣化と AI の侵食が 0 へ戻る（`ReleaseSec`） |
  | 〜2.60s | 人形 → 生身の入れ替わりが戻る（`TakeSchema.DefaultSwapMs`） |
  | 〜4.10s | エージェントの面が「異変を排除しました」を出して畳み終わる（枠 0.45 ＋ 打つ 0.75 ＋ 読ませる 2.0 ＋ 畳む 0.9） |
  | 4.10 → 7.10s | **何も無いリアルタイム映像の 3 秒**（ユーザー指定・0156） |
  | 7.10s | 演出が終わる → 合図が撃たれて電源断 |

  ⚠⚠ **4.10 秒は日本語の話。** 打つ尺は文字数 ÷ 速さなので English は 4.68 / Français は 4.85 秒で、
  **3 秒の間はそれぞれ 2.42 / 2.25 秒に縮む**。3.00 秒ちょうどになるのは日本語だけ
  ⚠ **watchdog は演出の頭から数える**ので、このカットを伸ばすと押すまでの猶予がそのぶん減る
  （`maxDurationSec` 48 − 7.1 ＝ 40.9 秒）。片方だけ動かさない
- 観測は `[XP] ev=outro` の `armed=` / `cue=`（`analyze-xp-log.py` の「## 終幕」節が判定する）

#### 本編へ入る判定は「演出が終わったか」（2026-08-06 置き換え・慣らし歩行を外した）

きょうの体験で**慣らし歩行は要らない**と判断し、`run.introMinSec` を 20 → **0** にした
（導入は導入演出だけ。あとは 1 周目のカメラ A で少し動く。A–B の境界は結構進まないと切り替わらないので
想定外の切替は起きない、というユーザーの実測）。**ただし値を 0 にするだけでは演出が 1 度も出なくなる。**

旧判定は `introPlaying`（＝演出が進行中か）の否定で、**段 0（開始待ち）はそこに含まれない**ため
「進行中でない」が成立していた。慣らしの 20 秒が「演出が始まるまでの猶予」を兼ねていたので露見して
いなかったが、0 にすると**体験者がスタート区間に立った時点で本編へ飛ぶ**。

→ [`IntroDirector.Completed`](../../Assets/Scripts/Streaming/IntroDirector.cs)（`BeginIntro` で落ち
`FinishIntro` で立つ。演出無効の設定では最初から true）を新設し、`ShowRunLogic.Tick` の引数を
`introPlaying` → **`introCompleted`（既定 true ＝ 演出を知らない呼び出し側では止めない）** へ置き換えた。
2026-07-30 の実害（`Holding` を使うと段 3・段 4 の条件待ちで false へ落ち、枠が出た直後に本編へ飛んで
Swap が一度も出なかった）も、「終わった」判定なら構造的に起きない。

⚠ **慣らしが 0 になったので、演出が終わった瞬間から 1 周目の録画が始まる**（`CueScheduler` のゲートが
開く）。3 周目の背景はその録画なので、体験者が固定視点に慣れる前の歩き方がそのまま素材になる。

#### HMD に出す文言の規約（2026-08-06 制定 / 2026-08-07 改訂）

**⚠ 体験者の視界には文字を 1 つも出さない**（2026-08-07・ユーザー指摘「体験者が被っているときに表示する
文字、世界観を壊すので消してください。スタッフの時は表示していい」）。読み手を面で分ける旧規約は
「体験者向けの面」を許していたが、機器の言葉がホラー体験の中に混ざる時点で世界が壊れる。

**門は 1 つ**：[`StatusHud.StaffViewing`](../../Assets/Scripts/Diagnostics/StatusHud.cs)
＝ 右 B のステータス表示 **or** 位置合わせ作業中。どちらも**コントローラを持っている人にしか
起こせない**ので、体験者が被っている間は構造的に立たない（体験者はコントローラを持たない運用）。
HMD 内の文字面はすべてここを見て出入りする。**解決できないときは出さない側へ倒す**
（判定できない環境で文字が復活する方の失敗を既定にしない）。

| 面 | いつ出るか | 出すもの |
|---|---|---|
| `ShowEndingFader` の黒（0.3m）と その上の 1 行（**1.5m**） | 黒は誰にでも / 文字はスタッフが見ている **かつ 位置合わせ中でない**とき | 中止時の `部屋の位置がずれたので止めました`。⚠ **黒と文字は別の Canvas**（黒 0.3m ＝ 視界を覆い切るため / 文字 1.5m ＝ 0.3m は輻輳の負担が大きい）。隠れないのは深度ではなく `sortingOrder` のおかげ（UI は深度を書かない） |
| `StatusHud`（視線前方 1.6m） | **右 B で開いたとき**、または **位置合わせ中**（登録ガイダンスを強制表示） | 相・周回・場所・異常 1 件 ＋ 直し方 |
| `ControllerGuidePanel`（右手の手元） | スタッフが見ているときだけ（位置合わせ中も出す） | ボタンの早見表だけ。⚠ 手を下げると視界外なので、**復帰手順の置き場にしない** |
| ~~`VisitorMarkPanel`（左手の手元）~~ | **廃止**（2026-08-16・`canon/LEDGER.md` 0058） | 報告の表示は `CommsPanel` の下段へ移った |
| `TitleNotice`（2.6m・黒の中） | 周回リセット直後の待ち（A を押す前）だけ | 体験前の注意書き ＋ **言語の選択**（2026-09-03）＋ 小さな案内 3 言語（0147）＋ **ホラー軽減モードの案内 1 行**（2026-09-05・0154）。⚠ **この面だけ 2/3**（`TitleNotice.Scale`・2026-09-05・0161）＝ 本文 1.2° / 補助 1.0°。段の正（1.8° / 1.5°）は動かしていないので**他の面は変わらない**。字だけでなく**枠と塊のあいだにも同じ率**を掛ける（字だけ縮めると塊が左へ寄る） |
| `OutroReport`（2.6m・黒の中） | 終幕の最後 | 報告の 4 行 |
| `CommsPanel`（1.5m・左下） | **3 点**（下）。体験者に見せる面なので門は通さない | AIエージェントの顔 ＋ その連絡 |

##### ⚠⚠ 体験者が読む面には言語が 3 つある（2026-09-03・`canon/LEDGER.md` 0127）

ユーザー指定「言語選択をできるようにしてほしい。日本語、英語、フランス語の 3 種類で。
最初の注意書きが表示されている間に、体験者がもつコントローラーから切り替えできるように」。

**正は [`ShowLanguage`](../../Assets/Scripts/Streaming/ShowLanguage.cs) 1 か所**（static）。
読む側が 3 つの asmdef に散っていて（文言 = Diagnostics / 入力 = Assembly-CSharp / 段 = Streaming）、
**Streaming は Diagnostics を参照できない**ので、いちばん下のここが唯一の配線点になる。

| 面 | 訳す | 理由 |
|---|---|---|
| `TitleNotice`（体験前の注意書き） | ○ | 安全の掲示。**言語の選択もこの面が出す** |
| `CommsPanel`（AIエージェントの連絡 8 通） | ○ | 体験者が読む |
| `VisitorMarkGuidance`（「解析中」） | ○ | 体験者が読む |
| `OutroReportText`（終幕の報告 4 行） | ○ | 体験者が読む |
| **スクリーン左上の周回**（`OsdClockLogic.LapLabel`・0167 / 訳は 0177） | ○ | 体験者が読む。`LAP 1` / `LAST`、`TOUR 1` / `FIN`。⚠ **時刻は訳さない**（数字と区切りだけ）。⚠ 版は独自（`make-osd-font.py`）なので HUD フォントの収集元とは無関係 |
| `StatusHud` / `ControllerGuidePanel` / `RegistrationGuidance` / `RecoveryGuidance` / 黒の上の 1 行 | ✗ | **読み手は日本語のスタッフ**。訳すと現場が読めなくなる |
| 題字「廻リ視」 | ✗ | 作品の名前（焼いた版 1 枚） |

- ⚠⚠ **訳文は「その面を持つ .cs の中」に置く。** 収集元
  （`JapaneseHudFontSetup.CollectHudCharset()` ＝ `tools/unity.ps1` の `hud-font` の `Src`）は
  4 つとも既に入っているので、**フォントの配線を 1 行も触らなくてよい**。
  新しいファイルへ非 ASCII を出すと、追加を忘れたとき**実機で豆腐になるのに警告が 1 件も出ない**
  （0035 の「声」と同じ型）。だから `ShowLanguage` は ASCII しか持たない
- ⚠⚠ **「非 ASCII ＝ 全角」ではない。** フランス語のアクセント付き（é è à ç …）は
  U+00C0 以降だが**字は半角の Latin**。物差しは
  [`HmdTextStyle.LineWidth`](../../Assets/Scripts/Diagnostics/HmdTextStyle.cs) 1 か所
  （テストも面の折り返し幅もここを読む）。実測は `menu text-audit -Set lang=ja|en|fr`
- ⚠⚠ **打鍵の速さは言語で変わる**（2026-09-04・`canon/LEDGER.md` 0149・**0127 の規約を覆した**）。
  **日本語 12 / Latin 18 文字/秒**（`CommsPanelLogic.CharsPerSecFor` が唯一の窓口）。
  同じことを言うのに Latin は 1.8〜2.0 倍の字数が要るので、同じ速さだと読む側が倍待つ。
  ⚠ **上限を決めているのは打鍵音の粒立ち** — 音源の実効は 59.8ms で、18 ＝ 55.6ms 間隔なら
  前の一撃は -35dB まで落ちている（空白では鳴らないので実際は約 15 発/秒）。
  **22（45.5ms）は 0056 が「連続音になる」として退けた値。超えない**
- ⚠⚠ **切り替え方は 3 言語ぶん出す**（2026-09-04・`canon/LEDGER.md` 0147）。選択中の言語だけで
  書くと、**それを読めない人には切り替え方が届かない**（＝ 日本語のまま始めるしかない）。
  並びの下に**一回り小さい字**（補助 1.5°）で 3 行。
  - ⚠ **「選んだらスタッフへ」は選んでいる言語で 1 行だけ**（0151 で 6 行 → 4 行へ整理）。
    その行を読むのは**言語を選んだ後**なので、選ばれているのは必ずその人が読める言語
  - ⚠⚠ **2 枚は「実際に組まれた字」の上端・下端で積む**（`characterInfo`）。
    `preferredHeight` は行送りの高さなので上下の余白を含み、**狙い 0.075m の空きが
    画では 0.194m**（2.6 倍）になっていた ＝ 空きの値を触っても差のぶんが効かない。
    `textBounds` も枠を返すことがあるので使わない
- ⚠ **体験者が替わったら日本語へ戻す**（`TitleScreen.BeginTitle` 1 か所）。
  押さなかった人に前の人の言語を出さない
- ⭐ **タブレットからも入る**（2026-09-11・`canon/LEDGER.md` 0185 / 0187）。**Quest 自身の HTTP（:8090）**が
  タブレットの選択を受け、`VisitorPrefs` が**注意書きの段でだけ** `ShowLanguage.Select` / `HorrorRelief.Select` へ書く。
  `BeginTitle` は**戻した直後に載せ直す**（順を入れ替えるとタブレットの設定が消える）。体験者が A を押したら
  枠を空にする（次の人へ持ち越さない）。卓も PC も経由しない。手元の単押し・長押しは残っている。
  契約は [streaming.md](streaming.md)「タブレットの口」、罠は `memory/visitor_tablet.md`
- **巡らせるのは左のどのボタンでもよい**（2026-09-03・0128）。X／Y だけではない
- ⚠ **切り替わると音が鳴る**（2026-09-04・`canon/LEDGER.md` 0153）。2 本を**交互**に
  （`sfx_lang_1` / `_2`）。鳴らすのは `TitleNotice` が字を書き替えている行で、
  **押して変わった回**（`ShowLanguage.ChangeCount`）だけ ＝ 体験者の交代では鳴らない。
  正本は `rules/sound-design.md`「言語を切り替えたときの音」
- 観測は `ev=sum` の **`lang` / `langN`**（対で出す）。**画にも音にも出ない**ので、
  どの言語で出ていたかはここにしか残らない。`analyze-xp-log.py` は
  「注意書きが引っ込んだ後にも言語が変わった」＝ 門の漏れを FAIL にする
- ⚠ **紙（`docs/onsite/handout.html`）は日本語のまま**（2026-09-03 時点）。
  受付とヘッドセットの中で言うことが食い違う形が 0035 で 1 度起きているので、
  外国語の来場者を通すなら紙も要る → `canon/OPEN.md`

##### ⚠⚠ 読ませる面は頭のヨーだけを追う（2026-08-20 ユーザー赤入れ）

「最初の注意書きが目の前に追従するんだけど見づらい」。**頭の子に置いて局所座標だけ決める
（＝ head-lock）と、上下に振っても面が眼から離れない。** 題字が 2026-08-13 に同じ指摘を受けており
（`canon/LEDGER.md` 0029）、そこで使った追従を [`HeadYawFollow`](../../Assets/Scripts/Streaming/HeadYawFollow.cs)
へ出して、**体験前の注意書き（`TitleNotice`）と終幕の報告（`OutroReport`）**も同じ根の下へ移した。

- **値の正はここ 1 か所**（`ScreenAnchor` / `TitleScreen` と対）。`TitleScreen` の const は
  この class から引く ＝ **数字を 2 か所に書かない**
- ⚠ **視界を覆う黒はこれに乗せない**（`TitleScreen` の覆い・`ShowEndingFader`）。
  覆いが頭から離れると、振り向いた瞬間に縁が視界へ入って現実が細く覗く
- ⚠ **出る縁で頭の正面へ置き直す**（`SnapToHead`）。置き直さないと、前に消えたときのヨーから
  緩慢に寄ってくる ＝ 面が視界の外から流れ込む
  - ⚠⚠ **自前の追従を持つ面も同じ**（2026-09-04・`canon/LEDGER.md` 0140）。`CommsPanel` は
    `YawFollowLogic` を自分で回していて `SnapToHead` を通らず、置き直しが抜けていた
    （出るたびに違う方角から回り込んでいた）。いまは畳まれているあいだ種を捨てる。
    `TitleScreen` は周回リセットで同じことをする。**追従を持つ面は必ずどこかで種を落とす**
- ⚠ **頭を解決できない環境（Editor プレビュー・テスト）では何もしない** ＝ 従来の head-lock。
  面が消える方へ倒さない
- ⚠ **`menu text-audit` の絵と数値はこれを判定できない**（大きさとはみ出し専用）。
  追従の癖は実機で被る以外に確かめる手が無い

⚠⚠ **連絡を寄越すのは AIエージェント**（2026-08-17・`canon/LEDGER.md` 0067・ユーザー指定
「上司じゃなくAIエージェントという事にしてほしい」）。2026-08-15〜16 は「上司」だった。

- **依頼した側（0036 の『スタッフは上司である』）は覆されていない。** 変わったのは
  **装置越しに連絡してくる主体**だけ。上司は体験の中に一度も現れない（声もかけない — 0038）
- **画に出る文字は 1 字も変わらない。** 4 文面（下の表）に送り主は書かれておらず、
  紙の依頼書も「装置を通じて指示が伝達される」としか書いていない。
  ⚠ **だから「AI である」ことは、いまの体験からは読み取れない。** 出す手は
  `canon/OPEN.md` に案として置いてある（**ユーザーが口にするまで実装しない**）
- ⭐ **②b が筋の通る側へ動いた。** 「異常は検出されませんでした」は
  「装置が世界を判定している」として 0065 で保留になっていたが、**検出しているのが AI なら
  それは装置の仕事**。人間の上司が遠隔で判定しているより無理が無い

##### ⚠⚠ 地は黒い半透明。連続して言う 2 通は同じ面のまま繋ぐ（2026-08-19・`canon/LEDGER.md` 0096）

ユーザー指定 2 つ。「スクリーンの背景 → 黒い半透明に」「0a,0b のように、連続していう言葉は、
前の言葉を表示して 2s たったら、そのスクリーンのまま、次の言葉が始まるようにして。
毎回消して表示しなおすのはしない」。

- **半透明は色の alpha だけでは 1 ビットも効かない。** URP の Unlit は既定が不透明なので、
  `_Surface` / キーワード / 混ぜ方 / 深度書き込みの **4 つを全部倒す**（`CommsPanel.MakeTranslucent`）。
  ⚠ フォールバックの `Unlit/Color` にはそのプロパティが 1 つも無い ＝ **倒せない**ので警告を出す
- **薄めるのは alpha であって rgb ではない。** 出入りを rgb の掛け算で作ると、半透明の面では
  「消えていく」ではなく「黒くなっていく」に見える
- 濃さは 地 `PanelAlpha` = 0.50 / 縁 `BezelAlpha` = 0.38。⚠ **縁は地の裏に敷いた一回り大きい面**なので
  中央は 2 枚重なる（合成 1-(1-0.38)(1-0.50) ＝ **0.69**）。縁を濃くすると中央だけ透けなくなる
- **繋ぐ合図は「読ませ終わった」**（`CommsPanelLogic.DoneReading` → `CommsCueInput.panelDoneReading`）。
  ⚠ **「畳み終わった」ではない** — それを待つと枠が左へ畳まれてから開き直す ＝ 毎回かならず吃る
- **`WalkGapSec` は 0**（⓪a → ⓪b は間を置かない）。⚠ 0 でないと、読ませ終わった面が畳まれ始めてから
  次が届くので、上の合図があっても繋がらない
- **枠も丈も出来上がっているなら、開く段（0.45 秒）そのものを飛ばす**（`CommsPanelLogic.Begin`）。
  飛ばさないと**何も動かない 0.45 秒**のあいだ面が空になる ＝ 「消えて、待って、また出た」に見える。
  ⚠ 畳み切った後はちゃんと開く段から出す（`AfterItFolded_TheNextNoticeOpensAgain` が対で固定する）

##### ⚠⚠ 面ぜんたいが 2/3 になった（2026-08-19・`canon/LEDGER.md` 0091）

ユーザー指定「AIエージェントのスクリーンの大きさを今の 2/3 にしてほしい」。
**倍率は根（`CommsRoot`）1 か所**（`CommsPanel.Scale`）で、地・縁・顔の枠・文字・壊れの複製が
同じだけ縮む。**この節と下の節に書いてある寸法・見かけ角は、どれも倍率を掛ける前の値。**

| | 前 | 後 |
|---|---|---|
| 文面の帯 | 0.76m ＝ 28.0° | 0.507m ＝ **19.2°** |
| 顔を含む全幅 | 0.954m ＝ 35.0° | 0.636m ＝ **24.0°** |
| 本文 / 下段の字 | 1.8° / 1.5° | **1.2° / 1.0°** |

- **置き場所は動かない**（1.5m・外へ 8°・下へ 17° は面の原点の角度）。文面の帯の中心へ向かって縮む
- ⚠ **字だけ据え置かない。** 1 行 14 文字が帯に入らず、折り返しの前提（3 行まで）が崩れる
- ⚠ **`menu text-audit` の狙い値にも同じ倍率が掛かっている**（`HmdTextAudit` の `TierDeg`）。
  段の素の値（`HmdTextStyle.BodyDeg`）と突き合わせると必ず「外れ」と出る

##### AIエージェントには顔がある（2026-08-17・`canon/LEDGER.md` 0071）

ユーザー指定「左に角丸の四角い枠線をつけて、その中にAIの顔を入れれるようにしてほしい。
顔は、paperdoll で作った"スイ"にして。輪郭だけ抽出し、髪は輪郭と同じ色で塗りつぶし、背景はない感じ」。

| | |
|---|---|
| 寸法の正 | [`CommsFaceLayout`](../../Assets/Scripts/Diagnostics/CommsFaceLayout.cs)（純ロジック・テスト 8 本） |
| 描くもの | [`CommsAvatar.shader`](../../Assets/Art/Shaders/Streaming/CommsAvatar.shader)（角丸の枠線を距離場で・顔は版の A だけ） |
| 版を焼く | `py -3.11 tools/make-comms-face.py --parts` → `Assets/Resources/Comms/SuiFace.png` |
| 見る | `.\tools\unity.ps1 menu comms-preview` |
| 観測 | `ev=sum` の **`commsFace=<枠>/<版>/<濃さ>`** |

- 枠は1.5m先の0.15m角。見かけ5.7°。色は文字と同じ象牙色。
  顔の内部が同じ位置で人形へ置き換わる。枠線は静止させる。
  旧版の明滅と赤・シアン分離は使わない。
- ⚠⚠ **文面の帯は 1mm も動いていない。** 顔のぶんは**面を左へ 0.194m 伸ばして**作った
  （全幅 0.954m ＝ 35.0°・左端は視線から -29.4°）。**幅を据え置いて字を詰めると、
  いちばん長い行（14 文字）が 3 行へ折り返して枠の高さの前提ごと崩れる。**
  `CommsFaceLayoutTests.RightEdge_IsUnchanged_...` が固定する
- ⚠ **面の丈に下限（0.194m）ができた。** 枠が縦にはみ出さないため。0065 の
  「出ている帯だけを覆う」に反しない — あれが禁じたのは**中身の無い空の箱**で、いまは左に顔が居る。
  伸びるのは短い文面のときだけ（いちばん高い姿 ＝ 2 行 ＋ 下段 ＝ 0.295m はこの下限を超える）
- ⚠⚠ **「髪を塗りつぶす」は、肌も塗ることになった。** 参考のスクショは「白い紙 ＋ 青い墨」で
  肌は紙の色だが、この面は**暗い地に明るい墨**なので明暗が逆 ＝ **肌を墨で塗ったものが紙にあたる**。
  肌を抜くと顔が黒い穴になり目も口も読めない（塗り分ける方向を 6 通り試して全滅）。
  ⇒ **頭の外形を塗って、元の絵の線を彫る**。詳細と実測は `tools/make-comms-face.py` の冒頭
- ⚠ **細い線は 1 本も残らない**（110 画素）。焼く側で線を太らせてある。閾値を上げて線を増やすと
  髪が糸くずの束になるので、**版を触ったら `--parts` の絵を必ず開く**

##### ⚠⚠ 顔は周回とともに市松人形へ侵食される（2026-08-17・`canon/LEDGER.md` 0073）

ユーザー指定「バグが進んでいくごとに、スイが日本人形のイラストに少しずつ侵食されていって
3 周目では完全に変わっちゃうみたいな感じに」。

- **進みは `CommsGlitchLogic.CorruptionFor` そのもの**（文字の壊れとまったく同じ値）。
  ⚠ **独自の曲線を持たせない** — 2 つ持つと片方だけ直したときに
  「文字は原型を保てないのに顔はスイのまま」が黙って起きる。
  ⭐ あの値は**帰りの A で戻る**（0070）ので、**AI の復帰がそのまま顔にも出る**
- **人形の版は、実物の写真から Codex に起こさせた線画を、スイとまったく同じ作り方で彫る**
  （`DollFace.png`）。依頼の作法は [tools/doll-ref/PROMPT.md](../../tools/doll-ref/PROMPT.md)
- ⚠⚠ **写真からは焼けない**（2026-08-17・0074 で 2 通り試した）。正本の写真は**背景も髪も暗くて
  外形が取れず**（実測: 背景 L=26〜52 / 髪 L=27）、明るい資料の板は**彫るべき線が無い**
  （出るのは「丸い頭に丸い顔が浮いている」だけの絵）。⇒ **線画にすると素材の形がスイと揃う**
- ⚠ **線は閉じさせる。** 外形は「線で囲まれた内側」を塗りつぶして取るので、
  1 画素でも途切れると塗りが外へ漏れて絵が丸ごと壊れる
- ⚠⚠ **画風は 3 つの数で揃える**（2026-08-17・0075 / 0076・赤入れ「チープ」「スイの絵柄で」）。
  `make-comms-face.py` が焼くたびに出す — **量**（線 ÷ 頭の面積）/ **重さ**（線 1 本の太さ ÷ 頭の幅）/
  **散らばり**（線のかたまりの数）。スイは 24.3% / 2.04% / 13。
  ⚠⚠ **量だけでは嘘をつく** — 量がそっくり（23.8%）で重さが 1/3 の版は、並べると別物だった。
  ⚠ 直すのは**線の本数と太さ**であって、太らせるだけでは黒い帯になる
- ⚠⚠ **塗りが漏れても黙って通る。** 生成した線画の頭頂に隙間が空いていて、
  **頭が真っ暗な絵**が焼けた。`_seal()` が閉じる量を自動で上げ、
  外形が外接矩形の 5 割を超えるまで確かめる（超えなければ落とす）
- ⚠⚠ **溶暗（クロスフェード）ではなく斑で置き換える。** 2 通り焼いて比べた — 混ぜると alpha が
  中間の灰へ落ちて、**「侵されている」ではなく「薄くなっている」**に見える。斑ならどの画素も
  必ずどちらかの顔で、境目だけが柔らかい。斑は uv だけで決まる（実行時に乱数を振らない ＝
  ちらつかない。ちらつくと侵食ではなくノイズに見える）
- ⚠ **2 枚は同じ大きさ・同じ座りで焼く**（`make-comms-face.py` の `compose` が同じ規則で収める）。
  ずれると「侵食」ではなく「絵が入れ替わった」に見える
- 観測は `commsFace` の **4 つ目**。⚠ `commsGl` と**同じ値のはず**で、食い違ったら配線が壊れている

##### ⚠⚠ 連絡の面も、周回とともに壊れていく（2026-08-17・`canon/LEDGER.md` 0068）

ユーザー指定「大きい方のカメラが映ってるスクリーンの画質が粗くなる…それに合わせて、
AIエージェントのスクリーンもバグるような演出を徐々に…3 周目 A で演出最大に」。

**進みは映像の劣化とまったく同じ値を読む** — `ShowRunDirector.ScreenDecay`
（＝ [`ScreenDecayLogic.Progress`](../../Assets/Scripts/Streaming/ScreenDecayLogic.cs)）。
あれは**最後の周へ入った瞬間に 1.0 へ着いて以後動かない**ので、
「3 周目 A で最大」は**別の曲線を書かずにそのまま満たされる**。
⚠ **独自の周回カウンタを持たせない。** 2 つ持つと片方だけ直したときに黙って食い違う。

⚠⚠⚠ **これは「装置の劣化」ではなく「AI が呪いに侵される話」**（2026-08-17・`canon/LEDGER.md` **0070**）。

> 体験者をサポートしているAIが、だんだん呪いに侵食され、3周目のA~Cでは原型を保てなくなっているが、
> 4周目のAでなんとか復帰して、体験者を助けようと…表示する

| 周 | AI | 面 |
|---|---|---|
| 1 周目 | 無事 | きれい |
| 2 周目 | 侵され始める | 固定箇所が少しずつ欠ける |
| **3 周目 A〜C** | **原型を保てない** | 通常文は固定箇所が欠ける。改変の正文と否定文は読める |
| **4 周目 A（帰り）** | **なんとか復帰** | 3.2 秒かけて戻り、③を読める形で出す（傷 0.12 は残る） |
| **報告が通った後** | **呪いが解けた** | **傷ごと 0。文字も顔も 1 画素も壊れない**（0129） |

- ⚠⚠ **侵食は単調ではない。** 映像の劣化（`ScreenDecayLogic`）は単調のままで、
  **連絡の面だけが山になる**（`CommsGlitchLogic.CorruptionFor`）。
  装置は壊れ続け、AI は持ち直す — **2 つは別の話**
- ⚠ **報告の前は完全には戻さない**（`RecoveredLevel` = 0.12）。押す前に直り切ると、
  **体験者の報告が何も変えなかった**ことになる
- ⚠⚠ **報告が通ったら 0 まで消す**（2026-09-03・`canon/LEDGER.md` **0129**）。
  侵食は呪いのせい（0083）なので、原因が消えたのに侵食だけ残ると
  「呪いを消したのにエージェントがバグったまま」になる。時計は**画が戻るのと同じ 1 本**
  （`ScreenDecayLogic.ReleaseK`）— 侵食の側に別の時計を置かない
- ⭐ **3 周目を壊し切れるようになったのは、③が 4 周目 A で出るから。**
  0069 の「文字は最後まで読める側へ倒す」は**消えたのではなく、置き場所が変わった**

2026-09-14の依頼で、報告文の意味改変を禁止した旧方針は変更された。
ユーザーの目的は「何かがおかしい。そしてシステムが嘘をついている」と伝わること。
具体的な消去順序と秒数はエージェントの実装案。採否は `canon/OPEN.md` に記録する。

最終通常周の人形置換が表示されたところで、一度だけ正しい検出結果を印字する。
全文を1.8秒保持する。人形側から1.0秒かけて字形を消す。
0.25秒の空白を挟み、否定文を0.3秒で現す。否定文は2.2秒保持する。
最後の否定文は通常の欠落表現の対象外。顔は人形のまま。
通常の空振りと解除成功には、この改変を出さない。
改変中の再報告では頭へ戻さない。帰りの区間と解除成功は既存の復帰を優先する。

通常の侵食も刻みごとのランダムな字欠けを廃止する。
欠けと滲みの位置は固定し、侵食量で増減する。文字列を記号へ置換しない。
字幅と改行位置を保つ。原色の矩形とRGB分離と明滅は追加しない。
帰りの区間の警告は読み取れる状態へ戻す。解除後は欠けも滲みも0。

実装と検証の入口は [comms_takeover.md](../memory/comms_takeover.md)。

⚠⚠ **`Unlit/Color` は実機のビルドに入っていない**（2026-08-17 に走行の画で判明）。
**組み込みシェーダでも、どのマテリアルからも参照されず Always Included にも無ければ剥がれる。**
連絡の面の**地と縁は実機で 1 度も描かれておらず**、文字と壊れだけが宙に浮いていた。

- **Editor では出る**ので `menu comms-preview` の絵は正しい姿を映し続けていた。**実機だけの症状**
- 2026-08-15 に「文字だけが宙に浮く」を実機の画で見て**縁を足した**が、
  **縁も同じシェーダ**なので同じ理由で消えていた（同じ穴に 2 度落ちた）
- ⇒ `Universal Render Pipeline/Unlit` を先に引く（URP のマテリアルが参照しているので必ず入る）。
  色は **`_BaseColor` と `_Color` の両方へ書く**（`Material.color` は URP の `_BaseColor` を触らない）
- ⇒ 観測は `ev=sum` の **`commsBg`**（地と縁を組めたか）。**この 1 ビットが無いと永久に気づけない**
- ⭐ **確かめ方はビルドログ**: `grep -o 'Compiling shader "[^"]*"' <build log> | sort -u` に
  そのシェーダが居なければ、実機には入っていない

⚠ **`CommsPanel` の発火は 3 点**（`canon/LEDGER.md` 0054）。判断は
[`CommsCueLogic`](../../Assets/Scripts/Streaming/CommsCueLogic.cs)（純ロジック・テスト 13 本）、
文面は `CommsPanel.TextFor` が持つ。**①③はラン 1 回に 1 度・②は押すたび**。

| 何 | 条件 | 文面 | 開くまで＋打つ | 残る | 引く |
|---|---|---|---|---|---|
| ① | 本編（`Run`）へ入った直後 | 異変を見つけたら<br>ボタンを長押ししてください<br>装置が解析して対処を試みます | 3.53s | **2.0s** | — |
| ①b | **①を読ませ終わった縁**（間を置かない） | 調査を開始してください。 | 1.45s | **2.0s** | 0.9s |
| ②a | 報告が通った（解除が効いた） | 異変を排除しました | 1.20s | **2.0s** | 0.9s |
| ②b | 報告が通らなかった | 異状は検出されませんでした | 1.53s | **2.0s** | 0.9s |
| ③a | **締めのカットに入って 5 秒**（`HaltAfterClosingSec`・0178） | 止まってください！ | 0.70s（**打たない**） | **1.1s** | — |
| ③b | **③a を読ませ終わった縁**（間を置かない） | 異常があなたを<br>取り込もうとしています。<br>排除してください。 | 2.95s | **2.0s** | 0.9s |

⚠⚠ **③の時計は「締めのカットに入ってから」**（2026-09-06・`canon/LEDGER.md` **0178**・ユーザー指定
「止まってください！が出るのは、押してないとき一律 5s にしてみて」）。**報告待ちが立つのは待たない。**
4 周目 A は入った瞬間から人形の動画が 8 秒流れるので、5 秒のここは**まだ動画の最中** —
人形が手を伸ばしている所へ「止まってください！」が重なる。
⚠ 旧実装（報告待ちが立ってから 2 秒 ＝ 入って 10 秒）は、**動画のあいだに押した人が③を一度も見なかった**。

⚠⚠ **締めに入って 4 秒は、押しても受け付けない**（`TakeRunnerLogic.MarkGraceSec`・同 0178・
ユーザー指定「4-A に入ってから 4s は、押しても反応しないようにしてほしい」）。
- **入力ごと止める**（`OvrControllerBridge`）ので**ゲージも溜まらない**。押させてから捨てると
  「溜まったのに何も起きない」＝ 装置が壊れて見える
- **数える前に返す**（`ShowControlClient.RecordVisitorMark`）ので、終幕の報告数に幽霊が乗らない
- ⚠ **掛かるのは締めのカットだけ**（`untilMark` の段を持つ take）。1〜3 周目の報告は従来どおり

⚠⚠ **③a が出たら③b は必ず続く**（同 0178・ユーザー指定「止まってください！以降の流れは全員に見せる」）。
報告したかは見ない。**0168 の「押したら③b を止める」は覆っている** —
③a より前に押し切れる時間そのものを上の猶予で塞いだので、順序が逆転する筋が無くなった。
⚠ ③a は**面が空くまで待って必ず出す**（消費しない）。時間で立つ①を消費するのとはここが違う。

⚠⚠ **③a だけは打鍵音を鳴らさない**（2026-09-06・`canon/LEDGER.md` **0168**・ユーザー指定
「止まってください！はカタカタ音無しにすっと出てくる感じで」）。文面ごと 0.25 秒で浮かび、
読ませるのも 1.1 秒（他は一律 2 秒）。`rules/sound-design.md` の「1 文字 1 発」の唯一の例外で、
決め所は `CommsCueLogic.DeliveryOf` の 1 か所。

⚠⚠ **文面は 2026-08-19 に全面改稿した**（`canon/LEDGER.md` 0096 / **0097**）。
③は体験者自身を名指しする。**下段の見出しも「解析中」**へ。

⚠⚠ **①は 2 通に割ってある**（0097・ユーザー指定「調査を開始してください→異変をみつけたら〜と、
表示は時間的に分けて。その間を切り詰める」）。**押し方の説明**と**開始の合図**を同時に読ませない。
- 割ったぶんの間は **0**。①を読ませ終わった縁で、**同じ面のまま文面だけ替わる**（⓪a → ⓪b と同じ）
- ⚠ **①b だけは押しのけられても消えない**（①③と違う）。②や③に割り込まれた回では、
  その連絡を読ませ終わってから改めて出す — 開始の合図を一度も読まないまま終わる人を作らない
- ⚠ 判定は `analyze-xp-log.py` の「①b が届いていない」と「①→①b が 8 秒以上空いている」
- ⚠⚠ **順番は 0174 で入れ替わった**（上の表が正）。**時間で立つ方（①）を消費し、
  読ませ終わりで立つ方（①b）は消費しない**というラッチの形は 0097 のまま

⚠⚠ **読ませる尺は全文面で同じ 2 秒**（2026-08-19・`canon/LEDGER.md` 0092・ユーザー指定
「基本、出し切るまでに文字は読めるので、出し切った後残す時間は一律 2s に」）。
**読む時間は打鍵中にもう始まっている**ので、打ち終わってから要るのは読み落としを拾う時間だけ。
**長い文面ほど画に居る時間は自然に長くなる**（打つ尺が文字数から決まる）ので、役割で足す必要が無い。
⚠ 0065 の「1 つの値を全文面に使わない」は**ここで覆っている** — あれが避けたのは 7 秒固定
（報告 1 回で面が 10.2 秒灯る）で、2 秒はその向きの逆。導入の 3 通（⓪a / ⓪b / ⓪c）も同じ 2 秒。

⚠⚠ **押し方は①にしか出ない**（同 0065）。下段は**状態**（`報告中` ＋ ゲージ）だけを持ち、
指示を持たない。それまでは `X／Y：異変を報告` が面の開いているあいだずっと出ていて、
**押している最中にも「押せ」と言い続けていた**。
⚠ **装置の面にキー名を出さない** — 入力機器の名前が出ると、調査の記録ではなく
ゲームの操作説明に見える。左で触れるのは X と Y だけで**どちらでもよい**ので「ボタン」で足りる。
⚠ **「初回だけ」を回数カウンタで作らない。** ①はラン 1 回に 1 度という構造が既に保証しているので、
そこへ相乗りさせれば数える状態が要らない（カウンタは落とす縁を書き忘れると
**2 人目以降に出なくなる** — 2026-08-15 に音で踏んだ型）。

⚠⚠ **押し始めたら、走っている連絡は片づく**（同 0065）。ユーザーの「X／Y を押して閉じる」への
答えで、**X／Y に 2 つ目の意味を与えずに**「自分の行為がこの面に効く」を返す。
⚠ 移る先は畳む段（`Out`）ではなく **`Guide`**（枠は開いたまま丈だけ縮む）。畳んでから開き直すと、
1 秒後に届く②の連絡で必ず吃る。
⚠ **縁でしか片づけない** — 押しっぱなしのあいだ毎フレーム立つので、
状態で見ると**自分で起こした②を自分で消す**（押した手応えが 1 つも返らない）。

⚠ **③は「4 周目 A」を外から組み直さない** — **報告を待つ段を持つカットに入ったこと**を見る
（`TimelineDirector.ClosingTakeSec`）ので、著作が変わっても追随する。

⚠⚠ **同じ signal を音も読む。** 締めのカットが待ち始めた縁で**人形の笑い**が 1 回鳴る
（2026-08-16・`canon/LEDGER.md` 0062。正本は `rules/sound-design.md`）。
③の連絡は同じ縁の 3 秒後なので、**笑い → 連絡**の順に来る。
⚠ **自動走行はためらってから押す**（`ShowWalkDebugDriver.ReportHesitateSec` = 4.5 秒）。
⚠⚠ **もう③の閾値とは連動しない**（2026-09-06・0178）。③は締めへ入ってからの時計で出るので、
ここが短くても③は必ず出る。**縮めても③は死なない**（旧記述は逆だった）。
⚠ **観測は `ev=comms id= n= built= chars= sfx= wait=` と `ev=sum` の
`comms=` / `commsBuilt=` / `typeN=`**。`comms=` は段だけでなく**実際に書いた文字の濃さと枠の開き**を
持つ（画に出た側）。`ShowTelemetryHost` と `analyze-xp-log.py` を**対で**直す。

⚠⚠ **字が 1 字出るたびに打鍵音が 1 発鳴る**（2026-08-16・`canon/LEDGER.md` 0056）。
正本は `rules/sound-design.md` §4「連絡の面の打鍵」。ここで押さえるべきは 1 つだけ —
**`CommsPanelLogic.CharsPerSecFor`（日本語 12 / Latin 18・0149）はそのまま打鍵の間隔になる**ので、
打つ速さを変えると音の密度も変わる（22 に戻すと連続音になる）。

##### ⚠⚠ 手元の面は「繋がっている」ではなく「位置が取れている」で出す（2026-08-16 実機で踏んだ）

`VisitorMarkPanel`（左）と `ControllerGuidePanel`（右）は**コントローラのアンカーの位置**へ置く。
ところが `OVRInput.IsControllerConnected` は**電源が入っていれば true** で、
カメラから見えていない（伏せてある・体の陰・起動直後）と姿勢は無効になる。
そのとき [`OVRCameraRig.UpdateAnchors`](../../Library/PackageCache/com.meta.xr.sdk.core@201.0.0/Scripts/OVRCameraRig.cs)
は有効なコントローラが 1 つも無いと `GetLocalControllerPosition(Controller.None)` ＝ **ゼロ**を書くので、
**アンカーはトラッキング原点（床の中心）へ飛ぶ**。

⇒ 実害（ユーザー報告）: 手元にあるはずの面が**足元の遠くに小さく**出ていた。
位置合わせ中はその早見表が唯一の操作説明なので、**作業がそのまま止まる**。

- いまは `OVRInput.GetControllerPositionValid` も見て、**位置が取れていないあいだは出さない**
  （`SetControllerState(connected, positionValid)`）。復帰したらスナップして戻る（`_seeded=false`）
- ⚠ **接続だけを見る判定を書き足さない。** 人形の左腕（`OvrHandTrackingBridge.TryReadController`）は
  最初から両方見ていて、そこだけ正しかった
- 観測は `ev=sum` の **`ctrlL=` / `ctrlR=`**（`<繋がっている>/<位置が取れている>`）。
  **画にも音にも出ない不具合なので、ここが唯一の手掛かり**。
  `analyze-xp-log.py` の「## コントローラの位置」が判定する（**対で直す**）

⚠⚠ **`VisitorMarkPanel` は門を通さない唯一の面**（2026-08-15・`canon/LEDGER.md` 0050）。
上の 3 つと並べると規約違反に見えるので、**善意で `StaffViewing()` を足されると
体験者に一生見えない面になる**（しかも誰も気づかない）。
`HmdTextGateTests.VisitorMarkPanel_IsNotGatedByStaffViewing` が足させない。
⚠ 枠も地も持たない（ユーザー指定「文字だけで枠線も背景もいらない」）。手元に常時あるので、
面を立てると視界が塞がる。

##### ⚠⚠ 大きさ・色・書式の正は [`HmdTextStyle`](../../Assets/Scripts/Diagnostics/HmdTextStyle.cs) 1 か所（2026-08-16 制定・`canon/LEDGER.md` 0052）

**面ごとに数字を決めない。** 2026-08-16 まで 7 面がそれぞれ勝手な `fontSize` を持っていて、
1 文字の見かけ角が **0.18°〜2.67° と 15 倍**ばらついていた（うち 1 面は実機で点にしか見えなかった）。

**段は 3 つ。既定は本文 1 つで、あとの 2 つは例外 2 か所のためにしかない。**

| 段 | 見かけ角 | どこ |
|---|---|---|
| 補助 | 1.5° | 報告の面の見出し `報告中` **だけ** |
| **本文** | **1.8°** | **既定。とくに理由が無ければこれ** |
| 注目 | 2.2° | 黒の上の 1 行 **だけ**（見落とすと詰む） |

**色は 2 色。** 地 `(0.82, 0.78, 0.72)` と警告 `(1.00, 0.55, 0.40)`。
**読み手（体験者 / スタッフ）で色を分けない** — 分けると「別の装置が 2 台ある」ように見える。
分かれているのは出る場所と門で足りる。リッチテキスト用の 16 進はパレットと機械照合してある
（`HmdTextStyleTests`）。⚠ Tracking 側（`RegistrationGuidance`）は依存の向きの都合で値を複製するが、
同じテストが食い違いを落とす。

**書式（文言を書く人が守るもの）**

1. 操作は **`入力：動作`**（全角コロン）。`A = 確定` `トリガー2秒 → 〜` は使わない。
   **同じ操作は 1 字まで同じ言い方**（`A：押したまま 0.5 秒静止で記録` は早見表とガイダンスで同一）
2. ラベルも **`ラベル：値`**（`場所：C:North` / `最大のずれ：5 cm`）
3. 括弧は**全角 `（ ）`**。要らない括弧は付けない
4. 区切りは**全角 `／`**（`2周目／全3周` / `X／Y`）。半角のままにしてよいのは
   時刻 `1:05` やゾーン名 `C:North` のような**データそのもの**の中だけ
5. **測った値**は数値と単位のあいだに半角空白（`5 cm` / `123 ms`）。`%` は空けない。
   ⚠ 操作名の中の数（`トリガー2秒`）は測った値ではないので空けない
6. **`⚠` は付けない**（警告色と出る場所で足りる。字面が騒がしくなるだけ）
7. 1 行に情報を 2 つまで。3 つ目は改行する（`・` で数珠つなぎにしない）
8. 句点は**文章にだけ**（注意書き・終幕）。状態・操作・警告には付けない
9. **状態は丁寧、手は常体**（異常の 1 行目「〜ています／〜ました」／直し方「〜する」）。
   状態の報告とチェックリストという役割の違いが、そのまま調子の違いになる
10. 揃えは**左**。中央にしてよいのは**黒の中に単独で出る 1 行だけ**（＝ 中止の 1 行 ＝
    `ShowEndingFader`）。**複数行あれば必ず左**。
    ⚠⚠ **終幕の報告は 2026-08-16 に中央 → 左へ移した**（`canon/LEDGER.md` 0063）。
    黒の中に単独で出る面でも、4 行あれば中央揃えは読みにくい。
    ⚠⚠ **左寄せにしたら字の塊は視界の中央へ運ぶ**（`textBounds` の重心を x も y も中心へ）。
    左寄せの意図は行頭が揃うことで、塊が視界の左へ寄ることではない。
    ⚠ **`menu text-audit` の絵ではこれを判定できない** — `Shoot` は面の原点を必ず画面中心へ
    運んでから撮るので、塊をどこへ置いたかは 1 枚も写らない（あの絵は**大きさとはみ出し専用**）。
    位置は `OutroReportLayoutTests` のように機械で固定する。
    ⚠ **注意書き（`TitleNotice`）は前から左**（`Left` ＝ 左寄せ・縦中央）。
    2026-08-16 まで規約が「中央」と書いていたが、実装は一度も中央ではなかった
11. 語は **「位置合わせ」「×印」「点」「ずれ」「ガイド線」** に固定。
    廃語 = 登録 / 再登録 / 基準点 / 残差 / 誤差 / マーク / ワイヤー / 確認線 / 周回リセット / 砂嵐 / course。
    ⚠ **「ずれ」は符号を持たない量**にだけ使う（±の付く割合は「差」）
12. 一時メッセージの寿命は**読み切れるか**で決める。2 行の失敗通知に 2.5 秒は短い（4〜6 秒）

**大きさの決め方**: 面は距離だけを持ち、`HmdTextStyle` が fontSize を逆算する。
⚠ **単位系が 2 つある** — Canvas 系（`fontSize × canvasScale`）と 3D 系（`fontSize × 0.1 × localScale`。
透視カメラの TMP は内部で 0.1 を掛ける）。**この 0.1 を知らずに数字を決めたのが、
2 回続けて起きた「実機で読めない」の正体**（0035 の 8.5 倍と、連絡の面の 10 倍間違い）。
⚠ **3D 系は `fontSize` ではなく `transform.scale` で掛ける**（fontSize を上げるとメッシュの座標が広がる）。
**折り返し幅も同じ scale で割る** — 固定値にすると、字を直したとき枠だけ取り残されてはみ出す。

##### ⚠ 触ったら `menu text-audit` を通す

```powershell
.\tools\unity.ps1 menu text-audit
```

[`HmdTextAudit`](../../Assets/Scripts/Streaming/Editor/HmdTextAudit.cs) が全面の
**1 文字の見かけ角**（Unity が組んだメッシュの実測。式ではない）と
**いちばん長い行 vs 枠**を出し、外れていれば落とす。あわせて 8 枚の絵を
`Assets/Screenshots/hud-text/` へ**同じ画角で**焼くので、並べれば大きさがそのまま比べられる。

⚠ **数字が緑でも絵は必ず開く。** そして**絵で分かるのもここまで** — 立体視と実機の輝度は
被らないと分からない。1 文字の見かけ角は 2 回続けて机上で外しているので、次の走行で必ず確かめる。

⚠⚠ **この絵で「面の中でどこに字があるか」を判定しない**（2026-08-16 に誤読しかけた）。
`Shoot` は **TMP の原点を必ず画面中心へ運んでから**撮るので、面が自分で決めた置き場所は
1 枚も写らない。終幕の報告の絵は左上に寄って見えるが、実機では塊が視界の中央に来る
（`OutroReportLayoutTests` が固定）。**測っているのは大きさとはみ出しだけ。**

⚠ **Edit モードでは `Awake` が走らない。** 面を自分で組むもの（`BuildsItsOwnText`）を
テストや道具から扱うときは、**リフレクションで `Awake` を明示的に呼ぶ**。忘れると
面が組まれず、テストは `Ignore` へ落ちて**検証していないのに緑に見える**。

⚠⚠ **スクリーンより奥に立つ面は、深度でも弾かれる**（2026-08-13・`canon/LEDGER.md` 0027）。
TMP の既定シェーダは `ZTest [unity_GUIZTestMode]` ＝ 既定 **LEqual** なので、描画順（queue）を
いくら後ろにしても**本編のスクリーン（2.0m・不透明・ZWrite On）の深度に隠れる**。
体験前の注意書き（`TitleNotice`・**2.6m**）がこれで 1 文字も出ていなかった。しかも警告は出ない。
- 無事だったのは**たまたま手前に立っている面だけ**（`IntroPrompt` 1.5m / `StatusHud` 1.6m）
- 自前シェーダの面（題字・覆い・殻・箱）は全部 `ZTest Always` なので関係ない
- **新しい TMP の面をスクリーンより奥へ置くなら、TMP の Overlay 版（`ZTest Always`）を使う**

⚠⚠ **導入の合図（`IntroPrompt`）は 2026-08-13 に廃止した**（`canon/LEDGER.md` 0033）。
ユーザー判定「小さいログとか前進してください見たいな文字は無しで」。**面ごと消した**ので、
`IntroDirector.PromptText` も `menu hud-font` の収集元からも外してある。
残っているのは下の表の 3 つだけ。**新しい文字面を足すときは、
「ステータスくらいの大きさでちゃんと示す文字」か「体験の演出としての文字」のどちらかであること。**

⚠⚠ **視界に重なる面は、位置合わせ中は登録ガイダンスへ譲る**（`StatusHud.RegistrationActive`）。
門を `StaffViewing` へ統一した時、登録中も `IsActive` 経由で開くようにしたが、**登録ガイダンスを
出しているのは StatusHud 自身（1.6m）で、`IntroPrompt`(1.5m) と `ShowEndingFader`(0.3m) は
その手前に重なる**。譲らないと作業中のスタッフに登録の文字が 1 文字も見えない。

実害（2026-08-07・現地で発覚）: トリガー 2 秒長押しで登録へ入っても導入の
`そのまま前へ進んでください` しか見えず、**モードが変わっていないように見えた**。
実際は登録モードに入っていて、手前の面が覆っていただけ。**距離が近い面ほど強い**ので、
新しい面を足すときは門だけでなく**距離と重なり**を見ること。手元の `ControllerGuidePanel` は
視界に重ならないので譲らない（登録中こそ操作早見表が要る）。
`HmdTextGateTests.Registration_SuppressesOverlappingSurfaces` が固定する。

⚠ **`StatusHud` の自動表示（旧 `recenterAutoShowSec` = 要再登録で 5 秒だけ開く）は廃止した。**
体験者の視界へ業務連絡が湧く唯一の経路だった。異常はスタッフが右 B で開けば最優先の 1 件として
必ず出るし、卓の heartbeat にも出ている。**本編中に位置がずれても自動では知らせない**ので、
現場では卓を見るか、気になったら B で開く。

⚠⚠ **「右手をあげてください」という合図そのものを廃止した**（2026-08-13・`canon/LEDGER.md` 0034
「伏線にするのは、スマートではありません」）。HMD 内の面も、スタッフの声掛けも、show.json の
`raiseHandPrompt` も全部消してある。**3 周目の反転は説明せずに気づかせる** — 背景が 1 周目の録画
なので、いま歩いている自分がそこに居ない。手をあげろと指示するのは種明かしを先に配るのと同じ。

⚠ **「押しても振動しないときはガイドパネルが出ているか見る」は、先に右 B を押してからになった**
（B を押せばパネルもステータスも出る）。

- **異常は「何が起きたか」＋「何をすれば直るか」の 2 行で、最優先の 1 件だけ**を本文と差し替えで出す。
  文言は [`RecoveryGuidance`](../../Assets/Scripts/Diagnostics/RecoveryGuidance.cs) が 1 箇所で持ち、
  `RecoveryGuidanceTests` が**全異常に手順があること**を機械で固定する（異常を足すには手順を書く以外の
  道が無い）。旧実装は 8 種のうち復帰動作を書いているものが **0** で、読んだスタッフは現場で黙って立った
- **数値は「その数字で判断が変わるか」で残す。** 残す = ずれ cm / カメラ番号 / 経過 / 周回。
  消した = 遅れの常時表示（100ms 超のみ）/ 揺らぎの内訳 / cue の id / `カメラ切替中…`（0.17 秒で読めない）
- **語は 3 語に固定**（「位置合わせ」「×印」「点」）。廃語 = 登録 / 再登録 / 基準点 / マーク / 残差 /
  誤差 / 周回リセット / 砂嵐 / course。テストが混入を落とす
- **体験者に取れる手が無いことは体験者に出さない**（旧 `位置合わせがまだです（スタッフが始めます）`）。
  減るのは不安ではなく没入で、機器の言葉がホラー体験の入口に出た瞬間に世界が壊れる

### 接近 — 追いつかれて、曲が替わったまま 3 周目 A へ（2026-08-22 / 0119 で黒マスクを外した）

世界観の逐語は `canon/LEDGER.md` 0102、設計書は
[reports/2026-08-22_approach-redesign.html](../../reports/2026-08-22_approach-redesign.html)。

⚠⚠ **2026-08-23（`canon/LEDGER.md` 0119）に、黒マスクを 4 周目 A（人形 → 体験者）だけへ寄せた。**
ユーザー逐語「2-Cで体験者→人形になるときに、黒いマスクがかかる演出を無くそう。うまく動いていなかった。
黒いマスクが出るのは、4-Aで人形→体験者に戻るときだけで」。

| どこ | 前（0102 / 0103） | 後（0119） |
|---|---|---|
| 2 周目 C の末尾 | ライブ ＋ **持続の覆い**（`swapHold`） | **覆いなしのライブ**。代わりに**呼びかけの次のカットで劇伴が替わる** |
| 3 周目 A の入り | 右半分が**最初から包まれている**（`swapHold`） | **包まれていない**。鏡映し → 凍結（黒マスクを入れる前の形） |
| 3 周目 A の人形化 | `transition:"swap"`（入れ替わりのノイズ） | **`transition:"glitch"`**（全画面の乱れ。黒マスクを入れる前の形） |
| 4 周目 A の人へ戻る | `transition:"swap"` | **変えていない**（黒マスクはここだけ） |

**追いついた文脈を運ぶのは黒ではなく曲になった。** 3 周目 A で人形へ変わるのは変わらないが、
その手前の 2 周目 C には何も残らないので、**呼びかけ（「あーそぼー」）を合図に劇伴が入れ替わる**
（カットの `bgm`・`rules/sound-design.md`「追いつきから終幕までの劇伴」）。
**2 周目 C のジャンプスケアを廃止**し、2 周目 A・B の保留（`cue_monster_A/B/C` を指す 3 本）も消した。
置き換えたのは「**視点が急に切り替わる・何かが近づいてくる**」1 本の弧で、
2-B が文法を教え、2-C が距離を詰め、3-A が結果を見せる。

| 区間 | 何が起きるか |
|---|---|
| **2 周目 B** | 入って即、**画面ごと別の場所へ飛ぶ**（`backrooms_B` プレート 4 秒・乱れで入り、切替音つき）→ ライブへ戻る。報告で畳める（`dismissible`）。⚠ 接近の予備動作は 0107 で 2-C の頭へ移した（`canon/LEDGER.md` 0122） |
| **2 周目 C（頭）** | 入って即、低い視点の遠景が **1 度割り込んで戻る**（予備動作・1.5 秒） |
| **2 周目 C** | **人形視点だけ**が飛び飛びに 4 カット、乱れを混ぜながら間隔を詰めて出て、4 回目で**白衣の背中に追いつく**。ここまでライブへは 1 度も戻らない。追いついた直後に**ライブへ戻る**（0119 で覆いは外した）。呼びかけを合図に**劇伴が入れ替わり**、区間を出るまで鳴り続ける |
| **3 周目 A** | 鏡映しの反対側からリアルタイムの自分が来る → 凍結 → **全画面の乱れ**とともに **2 周目 A の録画**が歩き出し、人形が立つ |

⚠⚠ **2-B・2-C はライブ依存ゼロ。** 全カットが録画（`source:"clip"`）なので、
**配信が全滅していても演出は完走する**（0102「リアルタイム映像は事故しやすい」）。
素材が無いカットは実機が飛ばす（§6.4）＝ C はただのライブへ退化して体験は続く。

#### 白衣の仕掛け

体験者に白衣を着せ、当日録画の被写体も白衣を着る。監視カメラの粗い画で人物の同定に使えるのは
**服の色と形だけ**なので、そこを揃えると「白衣が映っている ＝ 自分が映っている」と読まれる。

- ⚠ **参加者が映っている時間を最小にする**（0102）。接近クリップは参加者が映っていない所から始め、
  白衣が画面に入るのは最後の 2 カットだけ。映るほど「動きが自分と違う」に気づく機会が増える
- 設定と噛む: 紙の依頼書の「怪異調査員」に制服が生まれる。**着せる行為そのものが没入の入口**になる
- 副産物 3 つ: 服装の個人差が消える（誰が来ても録画素材が成立する）／暗い部屋で白衣は明るいので
  背景差分（入れ替わりの芯）が立ちやすい／3-A の黒い覆いが白衣の上に乗る ＝ コントラスト最大
- 当日の手順は [docs/onsite-checklist.md](../../docs/onsite-checklist.md) の **§0**

#### 2 周目 C の時系列（実測滞在 約 7 秒に収める）— 0104 で人形視点だけに・0107 で位置駆動に

⚠⚠ **差し込むのは人形視点だけ。合間にライブへは戻らない**（`canon/LEDGER.md` 0104）。
それまでは「偽ライブ（カメラ C の当日録画）と人形視点を交互に」だったが、
**0102 の『当日録画』は差し込みを全部人形視点にする前提の言葉**だった、というユーザーの明示。

> 人形視点で追いかけた映像とリアルタイム映像の矛盾は、人形が追いつくような 2-C での連続した
> 演出中にはライブ映像に戻さずに、ずっと録画にする（録画だけど連続的じゃなく、数秒の飛び飛びの
> カットで乱れを混ぜつつぱぱぱっとする感じ）で想定していた。次に戻るときは、体験者が黒マスクで
> おおわれるとき。

⚠⚠ **差し込みは 2 つに分かれ、開始の決まり方が違う**（`canon/LEDGER.md` 0107）。

> 差し込み位置を位置駆動にします。2-Bの予備動作を2-Cの前半に置きます。2-Cの近接を、即から後半にします。

| 演出 | 開始 | 中身 |
|---|---|---|
| **予備動作**（`L2C2#0`） | **C に入って即**（`at="enter"`） | 人形視点の遠景が 1 度割り込んで戻る（0.9s ＋ live 0.6s） |
| **接近**（`L2C2#1`） | **`line_approach`（x=+0.05）を越えたら**（`at="line"`） | 下の表の 5 カット |

- ⚠⚠ **接近は `policy="yield"` / `ifMissed="skip"`。** ユーザー判断「**3-A を潰さないが最優先**」を
  機構にしたもの。区間 C を出た瞬間に畳まれるので、3 周目 A の入れ替わりが必ず頭から走る。
  **踏まずに抜けた体験者には出さない**（黒い覆いを見ないまま進んでよい、という判断）
- ⚠ **`hold` に戻さない。** 最後のカットが `untilZoneChange` なので、`hold` だと発火が遅れた分だけ
  3-A に食い込み、入れ替わりが Ready のまま捨てられる
- **予備動作の最中に線を踏んだら**、接近は `Ready` でラッチされ**予備動作が終わってから**出る
  （既存の機構。演出どうしの引き継ぎなので復帰の暗転は挟まらない）
- ⚠ **線は自動走行の経路上に置く。** 走行は B(-0.675) → C(+0.225) と東進するので、
  **+0.225 より東へ引くと走行が永久に踏まず、検証手段が消える**

| # | 画 | 尺 | 遷移 | 音 |
|---|---|---|---|---|
| 0 | 人形視点①（遠い） | 1.0s | 乱れ | 切替 1 発 |
| 1 | 人形視点②（近い） | 0.9s | 乱れ | 切替 1 発 |
| 2 | 人形視点③（すぐそこ） | 0.8s | 乱れ | 切替 1 発 |
| 3 | **人形視点④ 追いつき**（白衣の背中が画面を埋める） | 1.4s | 乱れ | 切替 1 発 ＋ **人形の呼びかけ「あーそぼー」**（1.60s・0109） |
| 4 | **ライブ C**（0119 で覆いを外した） | 区間を出るまで | 乱れ | **劇伴が替わる**（3 秒のクロスフェード・0119） |

- **飛び飛びであること自体が演出**（連続した 1 本を切って出すのではない）。継ぎ目は
  `transition:"glitch"` で覆う ＝「乱れを混ぜつつぱぱぱっと」
- **ライブへ戻る唯一の縁が #4**。⚠⚠ **0103 は「戻った先の自分が黒に包まれている」で
  文脈を運んでいたが、0119 でそれを外した。** いま運ぶのは**曲**で、#4 の頭から
  3 秒かけて劇伴が入れ替わり、**3 周目 A・B・C・4 周目 A まで替わったまま鳴り続ける**
  （カットの `bgm` ＝ レーンの書き換え。演出が終わっても戻らない）
- ⚠ **呼びかけ（1.60s）は #3 の頭で鳴り、#4 の頭にはまだ 0.2 秒残っている。**
  クロスフェードは #4 の頭から始まるので、**声の尾と新しい曲の入りが重なる**。
  ユーザー指示は「あーそーぼーの後」なので、声が終わる前に音が変わり始めるのは許容
  （等パワーの入りは `sin(t·π/2)` なので、声が終わる 0.2 秒地点で新しい曲は振幅 **0.10**
  ＝ **-19.6dB**。声の下に居る）
- ⚠ 素材が無いカットは実機が飛ばすので、**当日撮らなければ C はただのライブへ退化する**
  （体験は続く）。0104 以降、当日撮るのは**人形視点 5 本だけ**（偽ライブは不要になった）
- ⚠ **2 周目 B の差し込みは接近の弧ではない**（0107 で予備動作を C へ移した）。
  0122 で入った `backrooms_B` は独立した 1 本で、切替音は **2-B に 1 発・2-C に 5 発**
- ⚠⚠ **追いつきのカットでだけ声が鳴る**（`canon/LEDGER.md` 0109・カットの `dollCall`）。
  **声（1.60 秒）はカット（1.4 秒）より長いが、画は待たない** — はみ出した 0.2 秒は #4
  （ライブ ＋ 持続の覆い）へ被る。ユーザー指定「映像はこの音を無視してそのまま先に進んで ok」。
  ⭐ 被る先が「ライブへ戻った自分が黒に包まれている」カットなので、**呼びかけの尾がそこへ渡る**。
  ⚠ 踏まずに抜けた体験者・区間を早く出た体験者では、最大 1.6 秒が 3 周目 A の頭へ被る
  （接近は `policy:"yield"` なので画は畳まれるが、`SfxPlayer` の声は鳴り続ける）。正本は
  `rules/sound-design.md`「人形の呼びかけ」

#### 報告では消さない（`dismissible` を立てない）

- **2-B の 0.9 秒は物理的に押し切れない**（長押し 1.0 秒 ＋ 気づき）
- **2-C の追いつきは 3-A の前提**なので、消されると弧が壊れる
- 押した人には「異常は検出されませんでした」が返る ＝ **装置にはこの接近が見えていない**、
  という読みが立つ（侵食の弧 0082 と矛盾しない）。対処の成功体験は 1 周目 B が担い続ける

#### 3 周目 A の入り — 人形へ変わるのはここ（0119 で黒マスクを外した）

**鏡映しの反対側からリアルタイムの自分が歩いてきて、凍り、乱れとともに人形になる。**

⚠⚠ **2026-08-23（0119）に、黒マスクを入れる前（`show.json` rev 35・2026-08-19）の形へ戻した。**
ユーザー逐語「体験者→人形になるシーンに関しては、黒いマスクを導入する前の方がよかったかも」
「今を全部戻すではなく、3-Aだけそれに戻してほしい」。戻したのは**黒マスクの 3 点だけ**:

| カット | 前 | 後（0119 ＝ rev 35 の形） |
|---|---|---|
| 鏡映し（`untilLine`） | `swapHold: true` / `swapMinX: 0.5` | **`swapHold: false`** |
| 凍結（1.2s） | 同上 | **`swapHold: false`** |
| 録画 ＋ 人形 | `transition:"swap"` | **`transition:"glitch"`** |

- ⚠ **録画の周（`recLap: 2`）は戻していない。** 1 周目 → 2 周目は 0102 の別判定
  （「3-A の録画は 1周目 A → 2周目 A へ変更」）で、黒マスクとは関係が無い
- **人形が立つ縁は変わらない**（`cg:"doll"` は録画のカットが持つ）。変わったのは
  「砂が体験者だけを覆って人形になる」から「**全画面の乱れの下で入れ替わる**」へ
- ⚠ `swapHold` を使うカットは**この体験にもう 1 つも無い**。機構は残してある
  （`rules/streaming.md` の「持続の覆い」節）ので、戻したくなったら 3 点を書き戻すだけ

#### 凍結と録画の開始位置が合わない — 原因は 3 つ

| 原因 | 何が起きるか | 手当て |
|---|---|---|
| ① course の鏡 ≠ 画像の鏡 | カメラ A が対称軸の真上に無い限り、course で対称な 2 点は画面上では対称に写らない | **卓の 🪞**（画像空間で反転して床へ逆投影）。①はこれで消える |
| ② 横断点が周ごとに違う | 線は約 1.5m あり、凍結は 3 周目・録画の頭は 2 周目の横断点。歩線が違えばその差だけずれる | 線を通路の狭い所に**短く**引き直す（`line_freeze` は「検証用の仮置き」ラベル） |
| ③ 継ぎ目が裸 | 残ったずれがそのまま「跳んだ」に見える | `transition:"glitch"` の乱れが覆う（0119 まではここが `swap` で、覆っていたのは入れ替わりに付く 0.55 × 0.20 秒の乱れだった） |

⚠⚠ **`line_freeze` と `line_rec_start` は対の資産。片方を動かしたら必ず 🪞 で作り直す。**
押し忘れは本番前チェックの `🪞 鏡の線` が ❌ で名指しする。

### 周回カウントと cue 自動発火（2026-07-17〜）

「何周目のどのゾーンで cue を出すか」の事前オーサリング（詳細は [streaming.md](streaming.md) の該当節と
[.claude/plans/2026-07-17_pre-authored-cue-schedule.md](../plans/2026-07-17_pre-authored-cue-schedule.md)）:

- [`LapCounter`](../../Assets/Scripts/Tracking/LapCounter.cs)（[Tracker] に `Setup Main Demo Scene` が自動配置）が
  **`CameraSwitchDirector.ZoneCommitted`（ショーの時計）**を購読し、show.json `layout.course.order` の**順方向一致でのみ**進行ポインタを進める。
  `order[0]` 復帰で lap++（1 始まり）。逆走・行き来・スキップは前進しない
  （2026-07-25 段 B 以前は `CameraStreamRegistry.ActiveChanged` / `SwitchCommitted` ＝**画面**を購読していた。
   director 未割当時のフォールバックとしてのみ `ActiveChanged` 購読が残る）
- **時計は画面から独立**（段 B）: 確定は [`ZoneProgressionLogic`](../../Assets/Scripts/Streaming/ZoneProgressionLogic.cs) が
  dwell だけで行い、cue / インサート / override の凍結にも dip にも左右されない。**演出で画面が止まっていても、
  体験者が歩けば周回・区間追跡は進む**。逆にスタッフ手動 A・Web 固定・インサートの画面切替は時計を動かさない

#### ⚠⚠ 体験者は引き返す（2026-08-17）

**演出の途中で後ろのカメラへ戻る体験者が居る。** そのとき起きることは 2 つで、
契約の正本は [streaming.md](streaming.md) の「周回数は 2 つある」と
「引き返したら、途中で切れた演出を頭から出し直す」。ここで押さえるのは要点だけ:

1. **引き返した先は「前にそこに居たときの区間」**。`1周目C → 2周目A → 引き返して C` は
   `(1, C)` として扱う。旧実装は `(2, C)` と読んで、**まだ通っていない 2 周目 C の演出を
   消費していた**（`once` なので、後で正規にそこへ来ても二度と出ない）
2. **途中で切れた演出は、報告していなければその区間へ戻ったとき頭から出し直す**
   （ユーザー指定「異変を報告済み → 再演出は無し／報告していない → 最初から再演出」）。
   完走した演出は戻ってきても出ない

⚠ **周回数は 2 つあり、混ぜると壊れる。** 終了判定（`lap > totalLaps`）は**進行の周**、
演出・録画・区間 post / BGM は**区間の周**。区間の周を終了判定へ渡すと、
帰りの A で 1 区間引き返した瞬間に**体験が終わらなくなる**。

⚠ **一度録れた区間は録り直さない。** 引き返して同じ区間へ戻ると、放っておくと
1 周目の映像が数秒の断片へ上書きされる（3 周目に流すのはその映像）。
- 純ロジック（`LapCounterLogic` / `CueScheduleLogic` / `ZoneProgressionLogic`）は MonoBehaviour から分離済みで EditMode テストがある
  （`Assets/Tests/Tracking/LapCounterTests.cs` / `Assets/Tests/Streaming/CueSchedulerTests.cs` /
   `Assets/Tests/Streaming/ZoneProgressionLogicTests.cs`）。セマンティクスを変えるときはテストを先に直す
- `PlayerZoneTracker` に `ZoneChanged` イベントを公開済み（旧: イベント無し）。ただし LapCounter の駆動は
  camera index キー（= course.order / schedule.camera と同一キー空間）のため Director 経由
- 起動時は既にスタート領域に居て確定イベントが出ないため、LapCounter が現在カメラを「進入」としてシードする
  （`seedInitialZone`。lap1 スタート領域の cue を発火可能にするため）

### 前後 (z) 方向の演出を入れる時
現在 z は全ゾーン共通 [-1.2, +1.2]。**前後で挙動を変えたいなら別軸のロジックを足す**（zone は左右専用にしておく）。`PlayerStateBus` のような中央集約は Phase 4（CG 合成）着手時に検討、それまでは Tracker と並列に小さな BehaviourScript で済ませる。
