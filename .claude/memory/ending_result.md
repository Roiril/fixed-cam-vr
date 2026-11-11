# 最後の A と観測記録（2026-09-21）

ユーザー指定は LEDGER 0247 と 0248。UI の細部は実装上の判断であり、世界観の正本には追加しない。

## 終了判定

- 警告は凍結線通過 OR 確定した最後の A への進入から 3 秒。前の連絡が読まれている間は順番を待つ。
- スイの Prompt が全文可視になる瞬間から 15 秒を数える。印字開始ではない。
- それ以降の未報告で A の実領域から出るか、確定区間が 4-A 以外になるか、15 秒に達すると Trapped。
- 2026-09-25追加。最後の演出で未報告の人形が画面の左半分へ入っても Trapped。この条件は Prompt の全文表示を待たない。合成で使うカメラから人形の足位置を投影する。中央と画面外は含めない。通常周の人形や人間の代役は対象外。
- PlayerZoneTracker は A の矩形すべてで実位置を判定する。カメラの直近区間保持とは分ける。追跡不明や 0.25 秒以上更新のない位置は退出判定に使わない。
- 報告と期限/離脱/左半分への進入が同じフレームなら報告優先。Update の入力と通信表示を受けた後の LateUpdate で判定する。
- MarkResult.Released だけを解除成立とする。帰還ライブでは対面位置・相互の向き・写真保存成功を待つ。撮影後の回転と退場まで完了してから Released の結果を出す。固定7.1秒やwatchdogで写真待ちを打ち切らない（[ending_farewell_doll.md](ending_farewell_doll.md)）。
- watchdog と技術的な中断を解除と誤認しない。これらは Interrupted。ランの上限は維持する。
- Trapped は同じフレームに画面の電力を落とす。通常映像へ戻るカットと電源断の縮小を通さず、暗転から結果へ進む。
- 終了後の報告入力は受理しない。結果へ進む間の押し直しで件数と結末を変えない。
- 新しい体験者で警告時刻と結末を初期化する。

## 時間切れの手前の沈み（2026-11-11）

ツインで「報告しない人」を通したとき、③b から人形 End まで約 15 秒、画も音も変わらず（輝度 13.4 → 13.1・効果音 0 件）、
落ちる瞬間は 0.2 秒以内に輝度 38 → 0 だった。所見の採否はシュビーに委ねられた（LEDGER 0323）。
沈む段はシュビーの実装判断で、採否は未判定（`canon/OPEN.md`）。

- 時間切れの手前 `EndingDecisionLogic.SinkSec`（6 秒）で、主画面の映像が明るさ係数 1.0 → `SinkFloor`（0.35・線形光）へ沈み、
  周辺の落ち込みが 0 → `CameraFeelFx.HeartVignetteBase`（0.66）へ強まる。曲線は `PerceptualFade.Darken`。
- 進みは `EndingDecisionLogic.SinkAmount(③b の全文表示からの経過秒)` の純粋関数。時間切れの判定と同じ `ShowClock` の同じ値で、
  時計を 2 つ持たない。長押しの途中も止めない。音は足していない。ホラー軽減でも同じに出す。
- 押すのは `ShowRunDirector.TickEndingSink`（`TickScreenDecay` の中・劣化の Tick の後）。書くのは既存の writer 2 つで、
  映像の明るさは `IntroDirector.WriteIgnite` が `_IntroLive` に掛け合わせ、周辺は `CameraFeelFx.WriteVignette` が
  心音のビネットと大きい方を `_HeartVignette` へ書く。新しい uniform・シェーダの項・`_SwitchDim` / `_ScreenPower` の writer は足していない。
- `_IntroLive` を選んだ理由: 明暗曲線の後・OSD と筐体（`_CrtTvCase`・0321）とガラスの前に掛かる線形の乗算なので、
  沈むのは映像だけで、係数がそのまま画に出る。露出（`_ExposureBias`）は明暗曲線とコントラストの前にあり、
  −1.5EV でも表示輝度 0.6 未満の画素が 0 へ潰れる（数式での見積もり）。ゆっくり沈む前に黒くなるので使わない。
- 報告が成立したら、報告の縁の値から `ScreenDecayLogic.ReleaseK`（0.5 秒）で 0 へ戻る（`SinkAfterRelease`）。
  `ShowRunDirector.ReleaseScreenDecay` が縁の値を取り、劣化の戻りと同じ `Release()` から始まる。
- 時間切れの Trapped は決まった時点の値（1）で止まる。A を出る・区間の移動・左半分（`TrappedByLeftHalf`）・中断では 0。
  `EndingDecisionLogic.TrappedByTimeout` が時間切れの Trapped を分ける（離脱と同じフレームなら時間切れ）。
- 0 に戻る経路: `BeginRun`（`PushEndingSink(0)`）・位置合わせ中と卓の画面占有（`Suppressed`）の間・
  `CameraFeelFx.ResetAll` / `OnDisable`・`ShowRunDirector.OnDisable`。押し続けている間は次の LateUpdate で押し直される。
- 人形 End の写真（`EndingFrameCapture`）は報告待ちの間ずっと撮り直すので、沈んだ画が入る。
  `EndingFrameCapture.RemoveEndingSink` が複製の材質へ沈む前の値を戻してから撮る。
- 観測は `ev=outro endingTime= sink=`（`sink` は CameraFeelFx が画へ書いた値・nc は材質なし）。
  解析器は終幕の頭の行で、時間切れの人形 End の `sink` < 0.9 を FAIL、帰還の `sink` > 0.05 を FAIL にする。
  `endingTime` の無い古いログは `promptAge` ≥ 14.9 で時間切れとみなす（`check_ending_sink`）。
- 外すときは `EndingDecisionLogic.SinkAmount` が常に 0 を返すようにする 1 か所で足りる。
- 未確認: Unity を起動していない（EditMode テスト・シェーダの実画・実機の見え方はどれも未実行）。

## 数の意味

結果画面に出る数は押した回数だけ（`OutroReportText.CountLine`・分母は出さない。`ending_roll.md`・`rules/ending.md`）。
分子は VisitorMarkCount。受理された報告操作の回数。同じ異常への再報告も含む。
押下途中の中止と入力禁止中の操作は数えない。

分母は画面に出ない。`ShowControlClient.TotalAnomalyCount` が `OutroReport.CapturedTotal` に保持し、
テレメトリ `ev=outro anomalyTotal=` に残る集計（`OutroReport.cs` は描画に渡さない）。
`ShowAnomalyCatalog` が定義する異常 ID の重複除外件数。
台本に設定された総数であり、その体験者が実際に見た数や報告で消した数ではない。
現行は 8 件。2-C の予備動作と接近を同じ 1 件とする。
1-B / 2-A / 2-B / 2-C / 3-A / 3-B / 3-C / 最後の A をそれぞれ 1 件とする。
既知の take が台本から消えれば分母も減る。対応表にない take があれば分母は「—」にする。
台本の構造を変えたときは対応表とテストも更新する。

## 表示と音

以下は2026-09-22時点の表示。2026-09-25以降のエンドロール形式は [ending_roll.md](ending_roll.md) を参照する。

OutroReport は結末と件数を表示開始時に固定する。
「帰還確認」「帰還不能」「調査中断」を別の見出しと文章にする。
どの結末でも「体験は終了です。装置を外してください。」を示す。
⚠ この行は旧実装の記録。現行は報告数だけを出し、分母は出さない（`OutroReportText.CountLine`・`ending_roll.md`）。
報告回数 / 異常総数は下段に控えめに置く。ラベルを左、比を右に分ける。成功率や評価語は付けない。
日本語・英語・フランス語を保持する。印字と打鍵の同期も保持する。

結果画面は映画の劇中にある終端表示を意図する。枠・角・目盛り・罫線・灰色のカード背景は使わない。
黒地に中立な白と灰で文字を置く。「帰還確認」「帰還不能」などの結末見出しは大きく、本文は左寄せにする。
既存の SourceHanSansJP-Normal を使い、新しい書体は導入しない。結末見出しと記録見出しの字間を調整する。
本文と装置を外す指示は視角 1.5 度を下限とする。2.6 m のヨー追従は維持する。
印字・打鍵・終了判定・音の動作は変更しない。
Resources 配下の OutroReportPlate.shader は APK 同梱対象。

Nocturnal Waters.mp3 を 48 kHz stereo PCM に復号した bgm_nocturnal_waters.wav を使う。
原曲と PCM16 の SHA256 は一致。音の加工はしない。元の -15.07 LUFS に対して音量 0.3576 で約 -24 LUFS。
結果表示開始に頭出し。1.5 秒でフェードイン。2D ループ。非表示/無効化で即停止。
2026-10-22 から、報告の出始めから 15 秒で面と曲が同じ進みでフェードアウトして閉じる（`OutroLogic.CloseLevel`・LEDGER 0286）。
取り込みは CompressedInMemory / Vorbis 0.42 / preload / normalize off / stereo。

## 再生成と観測

- `tools/unity.ps1 menu ending-record`: 専用フォントを焼いて実コンポーネントから 3 言語・3 状態の PNG を出す。
- 出力: `Assets/Screenshots/ending/`。単眼の Editor 描画。HMD の見え方そのものではない。
- `layout.csv` は 7 欄ごとの文字数と画素の有無。空の黒画像を欠字として検出する校正も行う。
- `tools/unity.ps1 test`: 終了判定・字形・配置・音源・回数のテスト。
- `py -3.11 tools/quest-record.py --walk --no-report --sec 180`: 報告しない自動走行。Development の `xpnomark` 起動フラグ。
- OSD は `py -3.11 tools/make-osd-font.py`。日本語は「終了」。英語は END。仏語は FIN。
- `ev=outro ending= anomalyTotal=` に結末と分母を記録する。
- `ev=outro endingLeft=1` は左半分への進入による人形End。`ending=Trapped` と併せて記録された場合だけ、ログ解析で終了前に届かなかった Halt / Prompt を不具合としない。
- `ev=sum repMusicBuilt= repMusicPlay= repMusicVol= repMusicSec=` に実音源の状態を記録する。
  再生命令だけでは鳴ったとしない。音量と再生位置の前進を `analyze-xp-log.py` が確認する。

## デザイン変更前の検証（2026-09-22 00時台）

- EditMode: 2120 / 2120 通過。ログ計器の人工イベント: 12 / 12 通過。
- Quest `2G0YC1ZF890864`。最終 APK の lastUpdateTime は 2026-09-22 00:10:51。
- 未報告: `20260922_000203_xp.log`。Trapped → Dark → Report → Done。0 / 8。94 文字 / 打鍵 94 回。曲は 0.48 → 34.55 秒。
- 報告あり: `20260922_000955_xp.log`。報告受理から 7.1 秒で Released。1 / 8。84 文字 / 打鍵 84 回。曲は 1.27 → 41.38 秒。
- 両結末の左右両眼を `logs/capture/ending-result-{trapped,released}_raw.png` で確認した。
- 実写カメラ 3 台は接続不能。録画素材を使う演出を含む体験全体の合格ではない。離脱と 3 秒境界は自動テストで検証。
- 最初の録画はビルドと同時実行で短く途切れた。以降はビルド完了後に録画。スリープ復帰直後の録画が 0 byte の場合、アプリを前面へ戻して 3 秒待つと録れた。
- 結果画面だけの録画では視野輪郭を検出できない。通し録画の変換も viewport 99% だったため採用せず、raw を正として確認した。

## デザイン変更後の検証（2026-09-22 朝）

- EditMode は 2122 / 2122 通過。本文の最小視角と見出しの優先、全結末の配色、連続した結果切り替えも検査する。
- 実コンポーネントから 3 言語 × 3 結末を描画。63 欄で 1340 / 1340 文字の領域に画素あり。空画像では欠字判定になることも確認した。
- 確認ツールで一部の文字が欠けて見える回があった。保存 PNG の直接画素検査と原寸表示では存在を確認できた。これを理由に実行時の TMP や文字送りを書き換えていない。
- 卓の作業中データは export せず既存の焼き込みを維持する。共有 HUD・乗っ取り・OSD の文言と字形は今回変更していない。mtime の焼き直し判定だけを `-Force` で外した。
- Android ビルド成功。Quest `2G0YC1ZF890864` の lastUpdateTime は 2026-09-22 07:59:31（端末時計）。VR category と focusaware を APK 内で確認した。
- 未報告走行は `logs/capture/20260922_075833_xp.log`。催促全文から 15.01 秒で Trapped。0 / 8。全文 94 文字に対して表示 94 文字・打鍵 94 回。音楽の再生位置は 0.91 → 25.00 秒。
- `logs/capture/ending-cinema-trapped_raw.png` は録画 165 秒の左右両眼。見出しから装置を外す指示まで確認した。黒地にカード境界は出ていない。
- 実写カメラ 3 台は今回も接続不能。これは結果画面の実機検証であり、体験全体の合格ではない。
