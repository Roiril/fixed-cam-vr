#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace FixedCamVr.Streaming
{
    // ShowControlClient の一部: 体験 1 回ぶん — 新しい体験者のリセット・周の数え直しの合図・滞在の計測・体験者の報告。
    // 全体の入口と役目の一覧は ShowControlClient.cs の先頭。
    public sealed partial class ShowControlClient
    {
        /// <summary>
        /// <b>周の数え直しの合図。</b>LapCounter が購読して周回・位置・cue をリセットする。
        /// 発火するのは 3 か所: 卓の新しい体験者（runEpoch 変化・<see cref="TriggerRunReset"/>）、
        /// 現地の新しい体験者（<see cref="BeginNewVisitorRunLocal"/>）、<b>導入から本編へ移るとき</b>（<see cref="BeginMainRun"/>）。
        /// ⚠ <b>「体験者が替わった」合図ではない。</b>体験 1 回ぶんの状態（録画・報告数など）を落とす処理を
        /// ここへ購読させると、導入から本編へ移る時点で 1 周目の録画を消す。それは号令元 2 か所に直接書く。
        /// （2026-11-03 まで RunReset という名前で、名前が「体験者が替わった」と読めた。）
        /// </summary>
        public event Action? LapCountReset;

        /// <summary>heartbeat に載せる現在周回数の供給元（LapCounter が注入。null なら -1）。</summary>
        public Func<int>? ProgressLapProvider;

        // ---- 実測滞在時間（区間 (lap, camera) にどれだけ居たか）----
        //   卓のリボン UI が「進入 +20s の演出が実測平均 8s の区間に置かれている」を検出するための一次データ。
        //   駆動はショーの時計（CueScheduler.CameraEntered = ZoneCommitted 由来）だけ＝画面の切替では動かない。

        private readonly SegmentDwellLog _dwell = new SegmentDwellLog();
        private bool _dwellSubscribed;

        private void SubscribeDwell()
        {
            if (_dwellSubscribed) return;
            // 既存シーンで cueScheduler が未配線でも成立させる（ResolveXxx と同流儀）。
            if (cueScheduler == null) cueScheduler = FindObjectOfType<CueScheduler>();
            if (cueScheduler == null) return;
            cueScheduler.CameraEntered += OnSegmentEnteredForDwell;
            _dwellSubscribed = true;
        }

        private void UnsubscribeDwell()
        {
            if (!_dwellSubscribed || cueScheduler == null) { _dwellSubscribed = false; return; }
            cueScheduler.CameraEntered -= OnSegmentEnteredForDwell;
            _dwellSubscribed = false;
        }

        // 読むのは **区間の周**（逆走で戻る）— 引き返して 1 周目の C に立った
        // 30 秒は「1 周目 C の滞在」であって「2 周目 C の滞在」ではない（卓の実測滞在は
        // 「その区間に置いた演出が出るだけ居たか」を見る道具なので、居た場所で数える）。
        private void OnSegmentEnteredForDwell(SegmentEntry entry)
            => _dwell.Enter(entry.SegmentLap, entry.Camera, SimTime.Realtime);

        // 卓の新しい体験者（runEpoch 変化）。中身は ResetForNewVisitor と同じで、録画の世代だけ卓の runEpoch に合わせる。
        private void TriggerRunReset()
        {
            Debug.Log($"[ShowControl] ラン開始（runEpoch={_knownRunEpoch}）: 体験 1 回ぶんの状態をリセット");
            ResetForNewVisitor(() => ResolveRecorder()?.ResetRun(_knownRunEpoch));
        }

        /// <summary>
        /// 現地の「新しい体験者」（右 A 2 秒長押し・タブレットの強制リセット）。卓の ▶ ラン開始と同じ
        /// <see cref="ResetForNewVisitor"/> を通す。違いは 2 つだけ:
        /// 卓が掛けたままの占有を先に外すこと（<see cref="ReleaseLiveHolds"/>）と、
        /// 卓と runEpoch を共有できないので録画の世代を自分で進めること。
        /// </summary>
        public void BeginNewVisitorRunLocal(string origin = "現地")
        {
            Debug.Log($"[ShowControl] ラン開始（{origin}）");
            ReleaseLiveHolds();
            ResetForNewVisitor(() => ResolveRecorder()?.ResetRunLocal());
        }

        /// <summary>
        /// <b>体験者が替わるときに体験 1 回ぶんの状態を落とす、唯一の並び。</b>
        /// 呼ぶのは卓（<see cref="TriggerRunReset"/>）と現地（<see cref="BeginNewVisitorRunLocal"/>）の 2 つだけ。
        ///
        /// 体験 1 回ぶんの状態を持つ部品は、次の 3 つのどれかで落ちる。部品を足したら、どれで落とすかを決める:
        ///   1. この並び（報告数・滞在・cue・演出・BGM・音・録画・周の数え直し）
        ///   2. <see cref="ShowRunDirector.BeginRun"/>（画面の劣化・画の状態・警告音・言語切替の音・終幕の判定。
        ///      導入を挟まない設定でも必ず通る）
        ///   3. <see cref="ShowRunDirector.RunRestarted"/> の購読（連絡の面・ステータス・心音に合わせた画の加工・終幕の写真・題字・導入）
        /// 対応表と「書き忘れたら落ちるテスト」は <c>Tests/Streaming/VisitorRunResetCoverageTests</c>。
        ///
        /// ⚠ <b>順番を変えない。</b>周の数え直し（<see cref="LapCountReset"/>）は演出のリセットの後、
        /// <see cref="ShowRunDirector.BeginRun"/> の前。BeginRun はゲートを閉じたあとで演出と録画をもう一度落とす
        /// （ゲートが開いたまま周を数え直すと、導入中に本編の演出が武装されるため）。
        /// 各工程は <see cref="RuntimeHealth.Step"/> で隔離し、1 つが投げても後ろを飛ばさない。
        /// </summary>
        private void ResetForNewVisitor(Action resetRecorder)
        {
            _reportTally.Reset();   // 押した回数と報告した異変の数
            _dwell.Reset();         // 計時中の部分区間は「体験者 1 人分の滞在」として成立しないので捨てる
            RuntimeHealth.Step("cue", () => cueScheduler?.ResetRun());
            RuntimeHealth.Step("timeline", () => timelineDirector?.ResetRun());
            RuntimeHealth.Step("bgm", () => ResolveBgmDirector()?.ResetRun());
            // ⚠⚠ 音を落とさないと 2 人目以降で 2 つ壊れる（2026-09-04）: 人形の笑いの方角が 1 人目のまま・
            //   前の体験者の一撃や頭出しラッチが次のタイトル画面へ流れ込む。どちらも画にも録画にも出ない。
            RuntimeHealth.Step("sound", () => ResolveSoundDirector()?.ResetRun());
            // 端末内録画の世代を切り替える（前の体験者の映像を次のランへ持ち越さない・端末に残さない）。
            RuntimeHealth.Step("recorder", resetRecorder);
            RuntimeHealth.Raise("LapCountReset", LapCountReset);
            // 骨格も頭から（導入があれば導入から）。
            RuntimeHealth.Step("run", () => ResolveRunDirector()?.BeginRun());
        }

        /// <summary>
        /// <b>卓が前の体験者のために掛けた「画面の占有」を現地で外す。</b>
        ///
        /// ⚠⚠ 卓の ▶ ラン開始は `cameraOverride` / `activeCue` / `slots` を空にしてから
        /// `runEpoch` を進めるが、<b>現地の右 A 2 秒長押しはそれらに触れなかった</b>
        /// （サーバへ書けないので当然だが、<b>ローカルの適用状態まで残していた</b>）。
        /// 壊れる手順は現場で普通に起きる:
        /// <b>① 体験者 A の最中に卓でカメラ固定か手動 cue を使う ② PC が落ちる・卓を閉じる・
        /// Wi-Fi が切れる ③ スタッフが右 A 2 秒長押しでリセットして体験者 B を始める</b>。
        /// B には前の手動映像が残り、ゾーンの自動切替は凍結されたままになる。
        ///
        /// ⚠ 卓が生きているなら次の long-poll で卓の状態が正として戻ってくる（それでよい —
        /// 卓が居るなら卓の ▶ を使う）。ここが効くのは<b>卓が落ちている現場</b>。
        /// </summary>
        private void ReleaseLiveHolds()
        {
            bool hadCue = !string.IsNullOrEmpty(_appliedCue);
            bool hadOverride = !string.IsNullOrEmpty(_appliedOverride);
            if (!hadCue && !hadOverride) return;

            string prevOverride = _appliedOverride;
            _appliedCue = "";
            _appliedOverride = "";
            _overlay?.StopOverlay();
            // override 中は tracker を止めてある。戻すと OnEnable が記憶ゾーンを捨てて
            // 現在位置から引き直す（＝通常経路でゾーンのカメラへ復帰する）。
            if (zoneTrackerToDisable != null) zoneTrackerToDisable.enabled = true;
            ResolveSwitchDirector()?.SetOverrideActive(false);
            cueScheduler?.SetLiveCueActive(false);
            timelineDirector?.SetSuppressed(false);
            Debug.LogWarning("[ShowControl] 卓が掛けたままだった占有を現地で外しました"
                             + $"（固定={(hadOverride ? prevOverride : "-")} 演出={(hadCue ? "あり" : "-")}）");
        }

        /// <summary>
        /// 導入を終えて本編（1 周目）を始める。<see cref="ShowRunDirector"/> だけが呼ぶ。
        ///
        /// <b>端末内録画の世代は切り替えない。</b> 録画のリセットは「新しい体験者」だけの仕事で、
        /// ここに載せるとラン開始が複数系統に増え、遅れて届いた runEpoch が 1 周目の録画を消す経路ができる
        /// （企画書 3 周目の素材が黙って消える）。
        /// </summary>
        public void BeginMainRun()
        {
            _dwell.Reset();
            cueScheduler?.ResetRun();
            timelineDirector?.ResetRun();
            ResolveBgmDirector()?.ResetRun();
            LapCountReset?.Invoke();   // LapCounter が lap=1 / pos=0 / 現在ゾーンの再シード
        }

        /// <summary>
        /// <b>体験者が記録ボタン（左 X / Y）を押した回数。</b> ラン開始で 0 に戻る。
        ///
        /// 紙（調査依頼書）の「違和感を認めるたび、手元のボタンを一度押してください。
        /// ボタンを押した時刻は、自動的に記録されます」が指しているのがこれ。
        ///
        /// ⚠⚠ <b>終幕の結果画面の分子はこれ</b>（<c>canon/LEDGER.md</c> 0247 が 0234 を覆した。
        /// 「報告した異変の数ではないよね。ボタンを押した回数が現状は記録されてるはず」）。
        /// 読むのは <c>OutroReport.Capture</c>・スタッフの面（<c>StatusHud</c>）・<c>ev=mark n=</c>。
        /// 同じ演出への押し直しを 1 と数える内部の集計は <see cref="ReportedAnomalyCount"/>。
        ///
        /// ⚠ <b>体験の進行には 1 ビットも使わない。</b> 押さなくても体験は同じように進む
        /// （判定に使うと、押さなかった人が失敗した気になる）。唯一の例外が 4 周目 A の締め
        /// （<c>durKind:"untilMark"</c>・`canon/LEDGER.md` 0050）。
        /// 通信面は報告時の表示を見て「異常を検出しました」/「異常は検出されませんでした」を返す。
        /// 分岐の材料は <see cref="LastMarkDetected"/>。演出の解除可否とは分ける。
        /// ⭐ 押した時刻が残ると、<b>3 周目の反転に気づいたかが訊かずに分かる</b>
        /// （初見は消耗品なので、誘導せずに取れる観測の価値が高い）。
        /// </summary>
        public int VisitorMarkCount => _reportTally.PressCount;

        /// <summary>
        /// <b>報告した異変の数</b>（2026-09-19・<c>canon/LEDGER.md</c> 0234）。同じ演出のあいだに何度押しても 1。
        /// 演出の無い所で押しても増えない（<see cref="VisitorMarkCount"/> には残る）。ラン開始で 0 に戻る。
        /// <b>体験者の結果画面には出さない内部の集計</b>（スタッフの <c>StatusHud</c> には出る）。結果画面の分子は <see cref="VisitorMarkCount"/>（<c>canon/LEDGER.md</c> 0247）。
        /// 数え方は <see cref="VisitorReportTally"/>。
        /// ⭐ 後々の分岐（報告数によるエンディング・0234「まだしないが」）の材料はこれ。押した回数は使わない。
        /// </summary>
        public int ReportedAnomalyCount => _reportTally.AnomalyCount;

        /// <summary>今回の台本に著作された異変の総数。結果表示に用いる。</summary>
        public int TotalAnomalyCount => ShowAnomalyCatalog.CountUnknown(_timeline?.segments) == 0
            ? ShowAnomalyCatalog.Count(_timeline?.segments) : 0;

        /// <summary>直近の報告が乗った演出の id（走っていなければ空）。<c>ev=mark take=</c>。</summary>
        public string LastMarkTakeId => _reportTally.LastTakeId;

        /// <summary>直近の報告で <see cref="ReportedAnomalyCount"/> が増えたか。<c>ev=mark new=</c>。</summary>
        public bool LastMarkCounted => _reportTally.LastCounted;

        // 押した回数と報告した異変の数（体験 1 回ぶん）。落とすのは TriggerRunReset / BeginNewVisitorRunLocal の 2 か所。
        private readonly VisitorReportTally _reportTally = new();

        /// <summary>
        /// <b>直近の報告で、怪異の解除が通ったか</b>（2026-08-17・`canon/LEDGER.md` 0082）。
        /// 供給は <c>TimelineDirector.NotifyVisitorMark</c> の戻り値。
        /// これは演出を消す処理と主映像の解除だけが読む。通信面の検出表示には使わない。
        /// </summary>
        public bool LastMarkResolved { get; private set; }

        /// <summary>
        /// 直近の報告時に、著作された異常演出が画面を取っていたか。
        /// <see cref="LastMarkResolved"/> は演出を解除できたかを表すため、表示の検出結果とは分ける。
        /// </summary>
        public bool LastMarkDetected { get; private set; }

        /// <summary>
        /// 直近の報告で、異常演出は画面に出ていたが<b>報告では消せない演出だった</b>か。
        /// スイの返事が「取り除きます」（<c>MarkLogged</c>）と「取り除けません」（<c>MarkStuck</c>）に分かれる。
        /// 消している最中の演出への押し直しは立てない（消せる演出なので）。<c>ev=mark stuck=</c>。
        /// </summary>
        public bool LastMarkStuck { get; private set; }

        /// <summary>直近の報告を受けた瞬間の表示状態。次フレームの進行では書き換えない。</summary>
        public bool LastMarkDollReplacementShowing { get; private set; }

        public bool LastMarkSuppressed { get; private set; }

        /// <summary>直近の報告を受けた瞬間の区間の周（逆走で戻る方）。区間が未確定なら -1。</summary>
        public int LastMarkSegmentLap { get; private set; } = -1;

        /// <summary>
        /// <b>呪いが排除されて画がリアルタイム映像へ戻った回数</b>（2026-09-05・
        /// <c>canon/LEDGER.md</c> 0156）。<b>単調に増えるだけで、ラン開始でも 0 へ戻さない</b> —
        /// 読み手は「増えた瞬間」を縁として見るので、戻すとその戻り自体が縁に化ける
        /// （<c>CommsPanel.PulseCount</c> と同じ流儀）。
        ///
        /// 増えるのは締めのカット（<c>durKind:"untilMark"</c>）が報告で畳まれた
        /// <see cref="TakeRunnerLogic.MarkResult.Released"/> のときだけ ＝ <b>1 ラン 1 回</b>。
        /// 1〜3 周目で異変を消した（<c>Dismissed</c>）ときには増えない。
        ///
        /// 読むのは <c>OvrControllerBridge</c>（スタッフの手を震わせる）。⚠ <b>OVR を触らない規約なので
        /// 向こうから読みに来る</b>（<c>StartAuthorizedProvider</c> / <c>CommsPanel.PulseCount</c> と同じ）。
        /// </summary>
        public int CurseReleasedCount { get; private set; }

        /// <summary>
        /// <b>いま押しても受け付けない</b>（締めのカットに入って
        /// <see cref="TakeRunnerLogic.MarkGraceSec"/> 未満・<c>canon/LEDGER.md</c> 0178）。
        /// 入力ごと止めるのは <c>OvrControllerBridge</c> で、ここは万一届いたときの受け皿。
        /// </summary>
        public bool IsMarkTooEarly => timelineDirector != null && timelineDirector.IsMarkTooEarly;

        /// <summary>通信装置が乗っ取られている間の報告受付。表示側が供給する。</summary>
        public Func<bool>? VisitorMarkBlockedProvider;
        public bool IsVisitorMarkBlocked
            => CourseRegistrationActive
               || (VisitorMarkBlockedProvider?.Invoke() ?? false)
               || (timelineDirector != null && timelineDirector.ClosingSegmentSec >= 0f
                   && !(ResolveRunDirector()?.ClosingPromptReadable ?? false));

        /// <summary>報告の長押しが始まった。件数は増やさず、該当演出の画だけを動かす。</summary>
        public void NotifyVisitorMarkStarted()
        {
            if (ResolveRunDirector()?.Phase == ShowPhase.Finished || IsVisitorMarkBlocked || IsMarkTooEarly) return;
            timelineDirector?.NotifyVisitorMarkStarted();
        }

        /// <summary>受け付けていた長押しが終わった（離した・禁止された）。押し始めで替えた画を戻す。</summary>
        public void NotifyVisitorMarkEnded() => timelineDirector?.NotifyVisitorMarkEnded();

        /// <summary>
        /// 報告の長押しの進み具合（0..1）。毎フレーム届く。布が膨らむ演出（<c>pressMaskUrl</c> を持つ cue・0349）の目標。
        /// 受け付けない間（終了後・乗っ取り・締めの冒頭）は 0 を渡す。押し始めの合図と同じ門で落とす。
        /// </summary>
        public void SetVisitorMarkProgress(float progress01)
        {
            bool accepted = ResolveRunDirector()?.Phase != ShowPhase.Finished
                            && !IsVisitorMarkBlocked && !IsMarkTooEarly;
            timelineDirector?.SetVisitorMarkProgress(accepted ? progress01 : 0f);
        }

        /// <summary>記録ボタンが押された（実行体は <c>OvrControllerBridge</c>）。</summary>
        public bool RecordVisitorMark()
        {
            // 終了後の操作で結末や観測記録を書き換えない。
            if (ResolveRunDirector()?.Phase == ShowPhase.Finished) return false;
            // 位置合わせ、乗っ取り、締めの警告を読み切る前は、件数・異変解除・返信の
            // いずれも予約しない。復帰後へ持ち越さない。
            if (IsVisitorMarkBlocked) return false;
            // ⚠⚠ **締めに入って最初の数秒は「無かったこと」にする**（0178・ユーザー指定
            //    「4-A に入ってから 4s は、押しても反応しないようにしてほしい」）。
            //    ⚠ **数える前に返す。** 数えてから捨てると、終幕の報告数に幽霊が 1 件乗る
            //      （`OutroReportText` の「報告した怪異の数」）。
            if (IsMarkTooEarly)
            {
                Debug.Log($"[ShowControl] 記録ボタン — 締めに入って {TakeRunnerLogic.MarkGraceSec:0.0}s "
                        + "経っていないので受け付けない（canon/LEDGER.md 0178）");
                return false;
            }
            // 報告が乗った演出の id。⚠ **報告で段が進む前に凍らせる**（下の anomalyShowing と同じ理由 —
            //    報告した瞬間に走っていた演出が「1 つの異変」の単位・0234）。
            string markTakeId = timelineDirector != null ? timelineDirector.ActiveTakeId : "";
            LastMarkDollReplacementShowing = timelineDirector != null
                && timelineDirector.DollReplacementShowing;
            LastMarkSuppressed = timelineDirector != null && timelineDirector.Suppressed;
            // 区間の周だけを入れる。進行の周（ProgressLapProvider）で埋めると、引き返した体験者で
            // 意味の違う値が同じ欄に混ざる。区間が取れなければ -1。
            LastMarkSegmentLap = timelineDirector != null ? timelineDirector.SegmentLap : -1;
            // 報告で段が進む前に凍らせる。untilMark はこの直後に終了するため、後から見ると
            // 「何も映っていなかった」に変わってしまう。
            bool anomalyShowing = timelineDirector != null && timelineDirector.AnomalyShowing;
            // 同じく段が進む前に凍らせる。締めの報告は直後にカットが進み、消せる演出ではなくなる。
            bool removable = timelineDirector != null && timelineDirector.ActiveTakeRemovable;
            // 「報告するまで」のカット（4 周目 A の締め）と、dismissible な演出だけが反応する。
            // ⚠ 戻り値が「何に効いたか」。畳んだ本人が答えるので、凍らせる順序に依存しない。
            TakeRunnerLogic.MarkResult result =
                timelineDirector?.NotifyVisitorMark() ?? TakeRunnerLogic.MarkResult.None;
            LastMarkResolved = result != TakeRunnerLogic.MarkResult.None;
            LastMarkDetected = anomalyShowing || LastMarkResolved;
            LastMarkStuck = LastMarkDetected && !LastMarkResolved && !removable;
            // 押した回数と、報告した異変の数（0234「1つの異変に対して報告したら内部では1だけカウント」）。
            // 異変に数えるのは**画で「検出した」と言ったもの**だけ ＝ スイの返事（②）と同じ判定を使う。
            // 同じ演出への押し直し（侵食度 1 の嘘の一文を読んで押し直した形）は押した回数にしか乗らない。
            bool counted = _reportTally.Record(markTakeId, LastMarkDetected);

            // ⚠⚠ **呪いが解けるのは締めのカットが進んだ 1 回だけ**（`canon/LEDGER.md` 0083）。
            //    1〜2 周目で異変を消した（Dismissed）ときに戻すと、**まだ呪われている最中に
            //    視界が晴れて**、以後の周の劣化が「なぜまた悪くなるのか」説明できなくなる。
            if (result == TakeRunnerLogic.MarkResult.Released)
            {
                ShowRunDirector? runDirector = ResolveRunDirector();
                runDirector?.ReleaseScreenDecay();
                runDirector?.NotifyClosingMarkReleased();
                // スタッフの手へ「締めに入った」を渡す縁（`OvrControllerBridge` が増分を見る）。
                CurseReleasedCount++;
            }

            Debug.Log($"[ShowControl] 記録ボタン（体験者・左のどれか） {VisitorMarkCount} 回目"
                    + $"（解除は{(LastMarkResolved ? "通った" : "通らなかった")}・{result}）"
                    + $" 報告した異変 {ReportedAnomalyCount} 件"
                    + (counted ? $"（{markTakeId} を新しく数えた）" : "（増えていない）"));
            return true;
        }
    }
}
