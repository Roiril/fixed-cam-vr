# 帰りの A まで体験を延ばす／位置合わせに床の高さを入れる（2026-08-02）

ユーザー要求（原文）:

> まず体験自体を、元の位置に戻って終了にしたいから、4週目のAまで体験に入れたくて、けど体験者は人形と
> 入れ替わった状態なので、流す映像自体は、2週目のAとなる。だから映像の差し替えをするのは、
> 3周目のB(1-B)-C(1-C)と4週目のA(2-A)とする。そしてすべてに人形を体験者の位置に表示する。
>
> また、位置合わせモードで位置合わせするときに毎回思うんだけど、床の高さを合わせられてない。
> 壁や床の領域が地面より下に表示される、なので、床から"1m"とか高さを決めて入力させるようにして、
> 高さも合わせるようにしてほしい。

---

## 1. 体験の形（変更後）

| 区間 | 画面 | 人形 |
|---|---|---|
| 1 周目 A / B / C | ライブ（**録画する**） | — |
| 2 周目 A | ライブ（**録画する**） | — |
| 2 周目 B / C | ライブ | — |
| 3 周目 A | **ライブ**（差し替えない） | — |
| 3 周目 B | 1 周目 B の録画 | ◯ follow |
| 3 周目 C | 1 周目 C の録画 | ◯ follow |
| **帰りの A**（lap=4, order[0]） | **2 周目 A の録画** | ◯ follow |

3 周目 A で差し替えないのは、B で初めて「過去の自分が映っている」＝ 自分は人形になっていた、という
反転を起こすため。A で人形を出すと種明かしが早い。

`record.laps = [1, 2]`（帰りの A で 2 周目 A を流すため 2 周目も録る）。

## 2. 終わり方 — `totalLaps` は 3 のまま、帰りの A だけを到達可能にする

進行ポインタ方式では **lap=totalLaps+1 の `order[0]` は構造的に必ず踏む**（周は order[0] へ戻った時に上がる）。
だから「3 周 ＋ 帰りの A」は totalLaps=3 のまま自然に表現できる。`totalLaps=4` にすると帰りの B・C まで
到達可能になり、企画書の「3 周」とも表示上食い違う。

**到達可能な区間**（3 実装が同じ式を持つ）:

```
lap <= totalLaps  ||  (lap == totalLaps + 1 && camera == order[0])
```

- C#: `ShowRunReach.IsSegmentReachable`（Streaming）
- 卓 JS: `run-model.mjs` の `isSegmentReachable`
- 解析: `analyze-xp-log.py` の `is_segment_reachable`

**期待値を 3 者にハードコードして突き合わせる**（(3,B)=可 / (4,A)=可 / (4,B)=不可 / (5,A)=不可）。

### ⚠ 現状の実装では帰りの A の演出が始まる前に暗転する

`lap=4` になったフレームで `lapsDone` が立つ。しかし `at:"enter"` の演出はその同じ連鎖で
**武装されるだけ**で、開始は次フレーム以降の `TakeRunner.Update`。`ShowRunDirector.Update` と
`TakeRunner.Update` のスクリプト実行順は未定義なので、`takeRunning=false` を見て即 Finished になる順序が
実在する。**終了条件成立後の最小保持（grace）が必須。**

```jsonc
"run": {
  "totalLaps": 3,
  "endGraceSec": 3.0,     // 終了条件成立後、演出が始まるのを必ずこれだけ待つ（0 = 従来動作）
  "endHoldMaxSec": 60.0   // 走行中の演出を見せ切る上限（旧 const 12 の置き換え）
}
```

保持は**単一式**で表す（新しい状態・ラッチを増やさない）:

```csharp
_endHeldSec += dt;
bool waiting = _endHeldSec < _endGraceSec || (takeRunning && _endHeldSec < _endHoldMaxSec);
```

- **時間切れ（`hardLimitSec`）では待たない。** 待つと hardLimit の意味が壊れる。
- 旧 `MaxEndHoldSec = 12f` では帰りの A の録画（2 周目 A の実滞在 = 20〜40 秒）が途中で切れる。既定 60。

## 3. 位置合わせの床の高さ — `CourseFrame` を 4 DOF にする

トラッキング原点は既に FloorLevel（`Main.unity` の `_trackingOriginType = 1`、OpenXR 経路でも Stage 相当）。
**設定では直らない**ので実測して合わせる。

- 現状 `_capturedWorld` は既に `Vector3` で y を持っていて、剛体フィットへ渡すときに捨てているだけ。
  **拾うだけで手に入る。**
- `floorY = median(capturedY) − regTouchHeightM`
- `layout.regTouchHeightM`（既定 **0** = 床に着ける）。卓の 📍 位置合わせ点パネルで入力。
  ユーザー案の「床から 1m」も入力すれば成立する。**既定を 0 にする理由**は、1m 空中でホバーすると
  XZ が確実にぶれて残差ゲート 0.12m を圧迫するため（いま XZ はうまくいっている）。
- y のばらつき（max−min）が 6cm を超えたら**警告を出す**（不合格にはしない）。「床に着けていない点がある」を
  現地で気づける、無料の品質ゲート。

### `CourseToWorld` の y 引数の意味が変わる

現在: `CourseToWorld(courseXZ, y)` の y は **world 絶対**（そのまま通す）。
変更後: y は **床からの高さ**（`originY + y` を返す）。

呼び出し側が渡している値はすべて「床からの高さ」のつもりの定数（ワイヤー床 0.03 / 壁上端
`wallHeight` / タイル 0.015 / CG の 0）なので、意味の変更で全部が正しく持ち上がる。
**唯一 world y を渡している経路**（`ShowWalkDebugDriver` の `headW.y`）は結果の y を使っていないので無害。

### 保存

`registration.json` に `originY` と `regSchema:2`、品質メタとして `floorSpreadM` を足す。
旧ファイルは JsonUtility が 0 で埋める → `regSchema=0` で「床の高さは未測定」と分かる（動作はブロックしない・
Review 画面で再登録を促すだけ）。**show.json には入れない**（機ごとに違う値で、show.json は既に 4 者がずれた実害がある）。

### 較正は不変

`cameras[].calib` は course 空間で解かれ、CG の仮想カメラ・人形・影・部屋プロキシはすべて同じ
`CourseToWorld` を通る。床ごと一様に平行移動するだけなので相対関係は変わらない。

## 4. 録画を実機で観測できるようにする

部屋が暗くて目視できないので、**ログで「録れた」「再生された」を別々に確かめる**。

| 出すもの | 何を証明するか |
|---|---|
| `ev=rec v=stop lap= cam= bytes= frames=` | その区間が**録れた**（frames=0 / bytes=0 は失敗） |
| `ev=recplay v=open lap= cam= frames= dur=` | 録画ファイルを**開けた**（＝カットが飛んでいない） |
| `ev=recplay v=close presented= ` | 実際に**画へ出した**フレーム数（0 なら開けただけで出ていない） |
| `sum` 行の `floorY=` `headY=` | 床の高さが解けたか・頭が床から何 m にあるか |
| `ev=reg` | 登録の結果（originY・残差・点数・y のばらつき） |

`analyze-xp-log.py` に対の判定を足す:

- `record.laps` × `course.order` の全区間で `ev=rec v=stop` が frames>0 で出たか（欠けたら **FAIL**）
- rec カットを持つ全区間で `recplay` の `presented>0` があるか（欠けたら **FAIL**）
- 到達不能な区間（(4,B) 等）に演出があれば **WARN**
- `headY` が 1.2〜2.0m の外なら **WARN**（床の高さがおかしい）

## 5. 同時に直さないと沈黙して食い違う対

| C# / データ | 対 |
|---|---|
| `ShowRunLogic`（保持式・既定） | `capture-server.py:_default_show` の run / `app.js` の `RUN_CFG_DEFAULT` |
| `ShowRunReach.IsSegmentReachable` | `run-model.mjs` / `analyze-xp-log.py`（**期待値をハードコードして突き合わせ**） |
| `ShowTelemetryHost` の新イベント | `analyze-xp-log.py` の判定 |
| `CourseFrame` の 4 DOF | 登録テスト 6 本 / `RegistrationGuidance` の文言 → **日本語フォント再生成** |
| `layout.regTouchHeightM` | `capture-server.py` の `_STATE_KEYS` / `floormap.js` |
| `ShowWalkDebugDriver` の周回数 | `run.totalLaps`（ハードコードをやめる） |

## 6. やらないこと

- `run.endSegment` / `finalLapSegments` の新設（終了判定が 2 系統になり「到達しなかった」失敗モードが増える）
- 凍結ラッチの追加（この codebase は「解けない凍結」を 4 回踏んでいる）
- `layout.regPoints[].y`（点ごとの高さ著作）— いまは全点が床の×印
- 高さ専用の登録ステップ（既存の点から無料で取れる）

## 7. 検証

1. EditMode テスト（終了保持の 3 分岐・到達可能区間・4 DOF 変換・ロールバック・y ばらつき）
2. `node --test tools/web-compositor/*.test.mjs`
3. `bash tools/run-quest-xp-test.sh walk 360` → `analyze-xp-log.py` で録画の記録と再生を判定
4. 暗所なので目視は補助。`quest-record.py` の画は「何か出た」の確認まで
