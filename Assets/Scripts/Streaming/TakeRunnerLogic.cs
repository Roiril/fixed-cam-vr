#nullable enable
using System;
using System.Collections.Generic;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 演出（Take）の純判定・計時ロジック。「どの演出を・いつ・どのカットで走らせるか」だけを決める。
    /// 実際の画面適用（カメラ切替 / オーバーレイ / post）は <see cref="TakeRunner"/> が行う。
    /// <see cref="InsertLogic"/> の後継で、**1 本の演出が複数カットを持てる**点が本質的な差。
    ///
    /// 契約の正本は <c>.claude/plans/2026-07-25_shot-timeline-foundation.md</c> §6.3。要点:
    ///   1. 同時に走る演出は 1 本。**別の演出が画面を持っているあいだは、その区間に居る限り武装したまま待つ**
    ///      （2026-07-27 変更。旧実装は待たずに即決着させて捨てていた ＝ 著作した演出が黙って消えた。
    ///      設計 §7 の「捨てる」根拠は「**演出中に通過した**区間の演出を遅れて出すと文脈が壊れる」であって、
    ///      **まだ同じ区間に居るなら文脈は壊れない**。区間を出れば従来どおり武装ごと消えるので不変条件 4 は不変）
    ///   2. 同一区間の順序: at=enter は offsetSec 昇順 → 同値なら配列順 / at=exit は配列順
    ///   3. ifMissed=fireOnExit: offsetSec に達する前に離脱したら、その離脱の瞬間に発火
    ///   4. 区間を離れた時点で未発火の演出は必ず決着する（発火 or 破棄）。**遅れて別区間で発火しない**
    ///      破棄したら必ず <see cref="TakeDropped"/> で報告する（黙って消さない）
    ///   5. once はラン内 1 回（<see cref="ResetRun"/> でクリア）
    ///   6. ライブ卓の抑止中は発火しない
    ///   6b. **開始規則「このラインを通過したら」（at=line）** は時刻ではなく床のラインの横断で due になる
    ///       （<see cref="LineCrossLogic"/> の結果を <see cref="Tick"/> で受ける）。
    ///       ラインは担当カメラに紐づき、区間のカメラと違えば踏んでも発火しない。
    ///       それ以外（武装・決着・once・ifMissed）は enter と完全に同じ扱い
    ///       — 契約は <c>.claude/plans/2026-07-27_position-trigger.md</c> §3
    ///   7. maxDurationSec（既定 45s）を超えたら強制終了する
    ///   8. 終了時の復帰先は「いま体験者が居るゾーン」（引数で受ける・復元ではなく再計算）
    ///
    /// 時刻・現在ゾーンはすべて引数で受ける（UnityEngine 非依存）。
    /// </summary>
    public sealed class TakeRunnerLogic
    {
        /// <summary>演出 1 本の実行定義（区間キー + 開始規則 + カット列）。</summary>
        public struct Def
        {
            public int lap;
            public int camera;          // 区間キーのカメラ
            public bool onExit;         // true=exit アンカー / false=enter or line アンカー
            public float offsetSec;     // enter のみ
            public bool skipWhenMissed; // true=skip / false=fireOnExit
            public bool once;
            public float maxDurationSec;
            public bool yieldOnZoneChange; // true=yield（体験者が区間を移ったら打ち切る）/ false=hold
            // カットごとの尺。負値は「外部通知待ち」で、値が待つ相手を表す:
            //   WaitClipEnd (-1) = 素材の終端  /  WaitZoneChange (-2) = 次の区間確定
            //   WaitLine (-3)      = この床の線を横切るまで（待つ線は stepLineIndex が持つ）
            //   WaitMark (-4)      = 体験者が異変を報告するまで（4 周目 A の締め）
            public float[] stepDurSec;

            /// <summary>
            /// カットごとに待つ線の slot index（<c>-1</c> = 線を待たない）。<see cref="stepDurSec"/> が
            /// <see cref="WaitLine"/> のカットだけが読む。**null なら全カットが線を待たない**（後方互換）。
            /// </summary>
            public int[]? stepLineIndex;

            // 開始規則「このラインを通過したら」（at=line）。onExit=false と併用する。
            public bool onLine;         // true = 時刻ではなく床のラインの横断で発火する
            public int lineIndex;       // LineCrossLogic のスロット（範囲外 = 発火しない）

            /// <summary>
            /// true = 「自分を塞いでいた演出が終わるまで、区間を出ても待つ」（wait="chain"）。
            /// 持ち越すのは因果がはっきりしている場合だけ（離脱の瞬間に実際に画面が塞がっていて、
            /// かつ開始条件は満たしていた）。時刻に届かなかっただけのものは持ち越さない。
            /// </summary>
            public bool chainWait;

            /// <summary>
            /// <b>体験者が異変を報告したら畳まれる演出か</b>（<c>show.json</c> の <c>dismissible</c>）。
            /// 構造ガード（現カットが <see cref="WaitMark"/> / 終幕の合図が指す演出）は
            /// <see cref="TakeRunner"/> 側で既に落としてあるので、ここに来た時点で
            /// <b>この旗が立っていれば畳んでよい</b>。
            /// </summary>
            public bool dismissible;
        }

        public enum Action { None, BeginStep, EndTake }

        /// <summary>
        /// 演出が終わった理由。<b>観測に出す</b>（<c>ev=take st=end why=</c>）ので、
        /// 「著作どおり終わった」と「体験者が消した」と「壊れて打ち切られた」がログで分かれる。
        /// これが無いと、報告で畳む機構が**効かなくても効きすぎても走行のログから判別できない**。
        /// </summary>
        public enum EndReason
        {
            /// <summary>最後のカットまで流し切った。</summary>
            Completed = 0,
            /// <summary>watchdog（<c>maxDurationSec</c>）の強制終了。</summary>
            Watchdog = 1,
            /// <summary>policy=yield で、体験者が区間を移ったので打ち切った。</summary>
            Yielded = 2,
            /// <summary><b>体験者が異変を報告したので畳んだ。</b></summary>
            VisitorDismissed = 3,
        }

        /// <summary>区間を越えて待てるスロットの上限。超えたら古い方から捨てて報告する。</summary>
        public const int MaxCarrySlots = 2;

        /// <summary>持ち越しの寿命 (秒)。演出の watchdog（既定 45s）より少し長く取る。</summary>
        public const float CarryMaxWaitSec = 60f;

        /// <summary>1 回の評価結果。action=None なら他フィールドは無効。</summary>
        public struct Decision
        {
            public Action action;
            public int takeIndex;
            public int stepIndex;      // BeginStep
            public bool takeStarted;   // BeginStep: この演出の 1 カット目（＝画面の占有を開始する）
            public int returnCamera;   // EndTake: 復帰先

            /// <summary>EndTake: なぜ終わったか。<see cref="forced"/> はここから導く（二重の真実を作らない）。</summary>
            public EndReason reason;

            /// <summary>EndTake: watchdog による強制終了か。</summary>
            public bool forced => reason == EndReason.Watchdog;

            /// <summary>
            /// EndTake: <b>体験者が異変を報告して畳んだか。</b>
            /// <see cref="TakeRunner"/> はこれを見て復帰の遷移を「乱れ」に変える
            /// （黒の dip で返すと「カメラが切り替わった」に見えて、報告と因果が結ばれない）。
            /// </summary>
            public bool dismissed => reason == EndReason.VisitorDismissed;

            /// <summary>
            /// EndTake: この直後に次の演出が始まる（＝画面を返さずそのまま渡す）。
            /// これが true のとき <see cref="TakeRunner"/> は復帰の暗転を打たない —
            /// 打つと「演出の終わりの黒」と「次の演出の入りの遷移」が二重に出て、
            /// しかも次の演出の 1 カット目が必ずその黒へ相乗りして自分の遷移指定を失う。
            /// watchdog による強制終了（<see cref="forced"/>）では立てない（壊れて止まったので素直に画面を返す）。
            /// </summary>
            public bool chainNext;
        }

        // 武装中の enter / line 演出の状態。
        private enum Armed : byte
        {
            Waiting = 0,        // まだ開始条件を満たしていない
            DeferredToExit = 1, // ライブ卓の介入中に条件を満たした → 離脱時のみ発火しうる
            Ready = 2,          // 開始条件を満たした。画面が空き次第この区間で発火する
        }

        /// <summary>演出が破棄された理由。</summary>
        public enum DropReason
        {
            /// <summary>区間を離れる瞬間、画面が空いていなかった（別の演出 / ライブ卓が使用中）。</summary>
            ScreenBusyAtExit = 0,
            /// <summary>区間を離れる瞬間、他の演出が先に選ばれた（同時 1 本の原則）。</summary>
            LostToAnotherTake = 1,
            /// <summary>持ち越し（wait="chain"）が上限（本数 / 寿命）に掛かった。</summary>
            CarryExpired = 2,
            /// <summary>持ち越し中に、塞いでいた演出が人為的に消えた（卓の緊急停止・介入・定義差し替え）。</summary>
            BlockerGone = 3,
            /// <summary>
            /// <b>体験者が異変を報告して画面を空けたので、その場で出るはずだったものを捨てた。</b>
            /// 捨てないと、消した次のフレームに別の異常が噴き出して
            /// <b>「消えた」が体験者に一度も見えない</b>（報告が壊れているようにしか読めない）。
            /// </summary>
            VisitorDismissed = 4,
        }

        /// <summary>
        /// 演出を破棄したときの通知（takeIndex, 理由）。**黙って消さない**ための唯一の出口。
        /// 設計 §6.3-1 が約束していた「破棄 + 警告ログ」の実体（2026-07-27 に実装）。
        /// <see cref="TakeRunner"/> は警告ログへ、シミュレータはトレースの <c>drop</c> イベントへ流す。
        /// </summary>
        public Action<int, DropReason>? TakeDropped;

        private Def[] _defs = Array.Empty<Def>();
        private bool[] _fired = Array.Empty<bool>();

        // 現区間で武装している enter 演出（配列順を保つ）。
        private readonly List<int> _armedIndex = new();
        private readonly List<float> _armedDue = new();
        private readonly List<Armed> _armedState = new();

        // 持ち越し（wait="chain"）。区間を出ても「塞いでいた演出が終わるまで」待ち続ける演出。
        // **武装リストとは別に持つ**のが要点 — いま居る区間の演出を必ず優先し、持ち越しは劣後させる。
        // （持ち越しが先に出ると、体験者がいま居る場所の演出が毎回後ろへずれていく。）
        private readonly List<int> _carryIndex = new();
        private readonly List<float> _carryUntil = new();

        private bool _hasCurrent;
        private int _curLap, _curCam;
        private bool _suppressed;

        // 走行中の演出。
        private bool _running;
        // 体験者の報告を受けた。実際に畳むのは次の Tick（終わり方を EndTakeDecision 1 本に保つため）。
        private bool _dismissPending;
        private int _activeTake = -1;
        private int _activeStep = -1;
        private float _stepEnd;
        // 現カットが始まった時刻。線待ち（WaitLine）で「始まる前に横切った」を数えないために要る
        // — 横断には CrossLatchSec(0.6s) の猶予があるので、これが無いと区間へ入る途中で踏んだ線が
        //   カットの開始直後に効いて、1 カット目が一瞬で飛ぶ。
        private float _stepBeganAt = float.NegativeInfinity;
        private float _deadline;
        private int _baseZoneCam;

        /// <summary>演出が画面を占有中か。</summary>
        public bool IsActive => _running;

        /// <summary>走行中の演出 index（非走行時は -1）。</summary>
        public int ActiveTakeIndex => _activeTake;

        /// <summary>走行中のカット index（非走行時は -1）。</summary>
        public int ActiveStepIndex => _activeStep;

        /// <summary>演出開始時に体験者が居たゾーンのカメラ（復帰先の最終フォールバック）。</summary>
        public int BaseZoneCamera => _baseZoneCam;

        /// <summary>武装中（発火待ち）の演出数。テスト・診断用。</summary>
        public int ArmedCount => _armedIndex.Count;

        /// <summary>定義を差し替える。once の発火済みと進行はリセットする（新ラン相当）。</summary>
        public void SetDefs(Def[] defs)
        {
            _defs = defs ?? Array.Empty<Def>();
            _fired = new bool[_defs.Length];
            ClearArmed();
            DropAllCarry(DropReason.BlockerGone, report: false); // 定義が変わった＝ index の意味が変わる
            _running = false;
            _dismissPending = false;
            _activeTake = -1;
            _activeStep = -1;
            _hasCurrent = false;
        }

        /// <summary>ラン開始（体験者交代）。once 発火済みと進行を全消去する。定義は保持。</summary>
        public void ResetRun()
        {
            for (int i = 0; i < _fired.Length; i++) _fired[i] = false;
            ClearArmed();
            DropAllCarry(DropReason.BlockerGone, report: false); // ラン境界なので報告不要
            _running = false;
            // 前の体験者が押した 1 回を次のランへ持ち越さない。
            _dismissPending = false;
            DismissCount = 0;
            _activeTake = -1;
            _activeStep = -1;
            _hasCurrent = false;
        }

        /// <summary>ライブ卓の抑止（activeCue 非空 / cameraOverride 非 null）を通知する。</summary>
        public void SetSuppressed(bool s) => _suppressed = s;

        /// <summary>
        /// ゾーン確定（ショーの時計）を受ける。離脱区間の決着（exit 演出 / 取り逃した enter 演出）を行い、
        /// 進入区間の enter 演出を武装する。返すのは最大 1 つの即時 Decision（離脱時発火）。
        /// </summary>
        public Decision OnZoneCommitted(int newLap, int newCam, bool hadPrev, int prevLap, int prevCam, float now)
        {
            Decision result = default;
            bool segChanged = !hadPrev || prevLap != newLap || prevCam != newCam;
            if (!segChanged) return result;

            // policy=yield: 体験者が区間を移ったら演出を打ち切って画面を返す（歩行を邪魔しない演出用）。
            // このとき離脱区間の exit 演出は発火しない — 同時 1 本の原則を保ち、打ち切りの直後に
            // 別の演出が始まって「返したのにまた持って行かれる」のを避けるため。
            if (_running && _defs[_activeTake].yieldOnZoneChange)
            {
                Decision yielded = EndTakeDecision(newCam, EndReason.Yielded);
                // 打ち切りで消える武装も**黙って消さない**（旧実装はここだけ報告が漏れていた）。
                if (hadPrev) ReportRemainingDrops(prevLap, prevCam, -1, screenBusy: true);
                ClearArmed();
                _hasCurrent = true;
                _curLap = newLap;
                _curCam = newCam;
                ArmEnterTakes(newLap, newCam, now);
                return yielded;
            }

            if (hadPrev)
            {
                // 離脱の瞬間: 配列順で最初に該当する 1 本だけ発火し、残りは破棄する（不変条件 7）。
                bool screenBusy = _running || _suppressed;
                int pick = FindExitCandidate(prevLap, prevCam);
                if (pick >= 0 && !screenBusy)
                {
                    StartTake(pick, now, newCam);
                    result = BeginStepDecision(takeStarted: true);
                }
                else
                {
                    // 画面が空いていない / 出す資格のあるものが無い。出せなかったものは**黙って消さず報告する**
                    //（設計 §6.3-1「離脱時にもまだ走行中なら破棄 + 警告ログ」の実体）。
                    pick = -1;
                }
                // 持ち越し（wait="chain"）を先に確定させる。持ち越したものは「まだ生きている」ので
                // 破棄として報告しない。
                CarryOverArmed(screenBusy, pick, now);
                ReportRemainingDrops(prevLap, prevCam, pick, screenBusy);
                ClearArmed();
            }

            _hasCurrent = true;
            _curLap = newLap;
            _curCam = newCam;
            ArmEnterTakes(newLap, newCam, now);
            return result;
        }

        /// <summary>
        /// 毎フレーム評価。走行中は watchdog / カット進行 / 終了を、非走行中は発火判定を行う。
        /// <paramref name="latestZoneCam"/> は「いま体験者が居るゾーンのカメラ」（終了時の復帰先）。
        /// <paramref name="lines"/> は通過ラインの今フレームの結果（<see cref="LineCrossLogic.StateView"/>）。
        /// null なら at=line の演出は発火しない（位置が取れない環境＝従来どおりの動き）。
        /// </summary>
        public Decision Tick(float now, int latestZoneCam, LineCrossLogic.State[]? lines = null)
        {
            // ① 開始条件を満たした瞬間を **必ず記録する**（画面が塞がっていても取りこぼさない）。
            //    ライントリガーは「横切った」という事象で、猶予 CrossLatchSec（0.6s）で消えるため、
            //    ここで拾わないと「別の演出が走っていた」だけで永久に失われる。
            LatchReady(now, lines);

            // ①' 線待ちのカット（durKind:"untilLine"）は、待っている線を横切った時点で畳む。
            //     武装と同じ猶予・同じ担当カメラ照合を使う（別の区間の線では進まない）。
            EndStepIfLineCrossed(now, lines);

            // ② 持ち越しの寿命。塞いでいた演出がいつまでも終わらない / 体験者が遠くまで行ってしまった
            //    場合に、著作した演出が延々と待ち続けるのを止める（捨てるときは必ず報告する）。
            ExpireCarry(now);

            if (_running)
            {
                // ⚠ **報告を watchdog より先に見る。** 同じフレームで両方揃ったら、体験者の行為の方を
                //   理由にする（画も「乱れて消えた」になり、押した手応えが返る）。畳む先は同じ
                //   EndTakeDecision なので、後片付けの経路は 1 本のまま増えない。
                if (_dismissPending) return DismissDecision(latestZoneCam);

                if (now >= _deadline)
                    return EndTakeDecision(latestZoneCam, EndReason.Watchdog);

                // ⚠ ここで武装中の演出を決着させない（旧実装はしていた）。
                //   画面が塞がっているのは**システム内部の都合**なので、著作された演出を捨てる理由にならない。
                //   その区間に居るあいだは Ready のまま待ち、この演出が終わった次の Tick で発火する。
                //   区間を出れば OnZoneCommitted が ifMissed どおりに決着させる（不変条件 4 は不変）。

                if (now >= _stepEnd)
                {
                    int next = _activeStep + 1;
                    float[] durs = _defs[_activeTake].stepDurSec;
                    if (next < durs.Length)
                    {
                        _activeStep = next;
                        _stepBeganAt = now;
                        _stepEnd = StepEndTime(now, durs[next]);
                        return BeginStepDecision(takeStarted: false);
                    }
                    // 次に出るものが既に控えているなら、画面を返さずそのまま渡す（連続の繋ぎ目に黒を挟まない）。
                    return EndTakeDecision(latestZoneCam, EndReason.Completed, chainNext: HasNextReady());
                }
                return default;
            }

            if (_suppressed)
            {
                // ライブ卓の介入は**人間の明示的な判断**なので、その間に条件を満たした演出は待たせずに決着させる
                //（オペレータが画面を握っている最中に、解放した瞬間へ演出を溜め込まない）。
                SettleReadyWhileSuppressed();
                // 持ち越しも同じ理由で畳む（人が画面を取ったのに、後から溜まっていた演出が噴き出さない）。
                DropAllCarry(DropReason.BlockerGone);
                return default;
            }

            int pick = FindReady();
            if (pick >= 0)
            {
                RemoveArmedAt(pick, out int takeIndex);
                StartTake(takeIndex, now, _hasCurrent ? _curCam : 0);
                // 同時に条件を満たした他の演出は Ready のまま残り、この 1 本が終わったら順に出る
                //（同一区間に居るあいだだけ。区間を出れば決着するか、chain なら持ち越す）。
                return BeginStepDecision(takeStarted: true);
            }

            // いま居る区間に出すものが無ければ、持ち越しを出す（劣後）。
            if (_carryIndex.Count > 0)
            {
                int carried = _carryIndex[0];
                RemoveCarryAt(0);
                StartTake(carried, now, _hasCurrent ? _curCam : 0);
                return BeginStepDecision(takeStarted: true);
            }
            return default;
        }

        /// <summary>外部通知待ちの印。<see cref="Def.stepDurSec"/> にこの値が入っていると尺は無限大になる。</summary>
        public const float WaitClipEnd = -1f;
        public const float WaitZoneChange = -2f;
        public const float WaitLine = -3f;
        public const float WaitMark = -4f;

        /// <summary>
        /// <b>体験者が異変を報告した</b>（左 X / Y の 1 秒長押し）。効き方は 2 つで、**排他**。
        ///
        ///   ①現カットが <see cref="WaitMark"/>（<c>durKind:"untilMark"</c>）なら、
        ///     報告は**そのカットが消費する**。4 周目 A の締めがこれで、畳んだ先には著作された
        ///     次のカット（現実へ戻る 3 秒）がある。⚠ ここで演出ごと畳むとその 3 秒が丸ごと消え、
        ///     しかも <c>run.outro.afterTakeId</c> が指す演出なので**終幕が早撃ちされる**。
        ///   ②それ以外は、著作者が <c>dismissible</c> と宣言した演出**だけ**が畳まれて現実へ戻る
        ///     （<c>canon/LEDGER.md</c> 0050「報告したらそれらが消え」「推したら乱れたのちに元に戻って」）。
        ///
        /// ⚠ **1 回の押下が 2 つの意味を持たないようにする。** ①で return するのがその保証。
        /// ⚠ **カットが始まってからの報告だけを数える。** 直前の区間で押した 1 回が持ち越されて
        ///   カットを素通りさせるのを防ぐ（線待ちと同じ理由）。
        /// ⚠ 実際に畳むのは次の <see cref="Tick"/>（<see cref="DismissDecision"/>）。ここで畳むと
        ///   演出の終わり方が 2 経路になり、後片付けの網羅性が経路ごとに分かれる。
        /// </summary>
        public void NotifyMarkPressed(float now)
        {
            if (!_running) return;
            if (now < _stepBeganAt) return;

            if (CurrentStepWait() == WaitMark) { SetCurrentStepEnd(now); return; }

            if (_activeTake >= 0 && _activeTake < _defs.Length && _defs[_activeTake].dismissible)
                _dismissPending = true;
        }

        /// <summary>
        /// 報告で畳んだ回数（テレメトリ・テスト用）。<b>「押した回数」ではなく「実際に消えた回数」</b> —
        /// 押しても消えない演出の方が多いので、この 2 つを混ぜると効いたかが分からなくなる。
        /// </summary>
        public int DismissCount { get; private set; }

        // 体験者の報告で走行中の演出を畳む。
        //
        // ⚠⚠ **畳んだ直後に別の異常が噴き出さないようにする。** 出てしまうと「消えた」が画に
        //    1 フレームも出ず、体験者には報告が効かなかったようにしか見えない。
        //    捨てるのは「画面が空いたら即座に出るもの」だけ ＝ Ready の武装と持ち越し（carry）。
        //    **まだ時刻が来ていない武装（Waiting）は残す** — あれは後から別の異常として出るのが自然で、
        //    消すと著作した内容が黙って減る。
        private Decision DismissDecision(int latestZoneCam)
        {
            for (int k = _armedIndex.Count - 1; k >= 0; k--)
            {
                if (_armedState[k] != Armed.Ready) continue;
                int dropped = _armedIndex[k];
                RemoveArmedAt(k, out _);
                TakeDropped?.Invoke(dropped, DropReason.VisitorDismissed);
            }
            DropAllCarry(DropReason.VisitorDismissed);
            DismissCount++;
            // **連続の渡し（chainNext）は立てない。** 渡すと画面が異常のまま次の演出へ移り、
            // 現実が 1 フレームも出ない ＝ 報告の因果が画から消える。
            return EndTakeDecision(latestZoneCam, EndReason.VisitorDismissed);
        }

        /// <summary>
        /// 尺が <c>untilLine</c> のカットを、体験者がその線を横切った時点で畳む
        /// （<see cref="TakeRunner"/> が横断検出から呼ぶ）。
        ///
        /// ⚠ **待っている線だけで終わる。** 別の線を横切っても進まない — 1 つの区間に線を 2 本引く
        ///   のが 3 周目 A の設計なので（左＝録画の開始地点 / 右＝凍結点）、どの線でも終わる作りにすると
        ///   入ってくる途中で踏んだ線で凍ってしまう。
        /// </summary>
        public void NotifyLineCrossed(float now, int lineIndex)
        {
            if (lineIndex < 0) return;
            if (CurrentStepWait() != WaitLine) return;
            if (CurrentStepLine() != lineIndex) return;
            SetCurrentStepEnd(now);
        }

        // 線待ちのカットを、待っている線の横断で畳む。**カットが始まる前の横断は数えない** —
        // 横断には猶予（CrossLatchSec）があるので、区間へ入る途中で踏んだ線がそのまま効いてしまう。
        private void EndStepIfLineCrossed(float now, LineCrossLogic.State[]? lines)
        {
            if (lines == null || CurrentStepWait() != WaitLine) return;
            int slot = CurrentStepLine();
            if (slot < 0 || slot >= lines.Length) return;
            LineCrossLogic.State s = lines[slot];
            if (s.crossedAtSec < _stepBeganAt) return;
            if (now - s.crossedAtSec > LineCrossLogic.CrossLatchSec) return;
            if (!(s.camera < 0 || s.camera == _defs[_activeTake].camera)) return;
            SetCurrentStepEnd(now);
        }

        /// <summary>
        /// 走行中のカットが「体験者の報告」を待っているか。
        /// <b>自動走行が押す真似をするために公開する</b> — 押されないと締めのカットは実機で
        /// 一度も検証されない（走行はコントローラを持たない）。
        /// </summary>
        public bool IsWaitingForMark => _running && CurrentStepWait() == WaitMark;

        /// <summary>走行中のカットが待っている線の slot index（待っていなければ -1）。</summary>
        private int CurrentStepLine()
        {
            if (!_running || _activeTake < 0 || _activeTake >= _defs.Length) return -1;
            int[]? s = _defs[_activeTake].stepLineIndex;
            return (s != null && _activeStep >= 0 && _activeStep < s.Length) ? s[_activeStep] : -1;
        }

        /// <summary>
        /// 尺が <c>untilClipEnd</c> のカットで、素材の再生が終わったことを通知する
        /// （<see cref="TakeRunner"/> が VideoPlayer / 画像表示の終端で呼ぶ）。次の <see cref="Tick"/> で進行する。
        ///
        /// ⚠ <c>untilZoneChange</c> のカットは**この通知では終わらない**。素材が短くても、
        ///   体験者が次の区間へ移るまで出し続けるのがそのモードの意味だから。
        /// </summary>
        public void NotifyCurrentStepFinished(float now)
        {
            if (CurrentStepWait() == WaitClipEnd) SetCurrentStepEnd(now);
        }

        /// <summary>
        /// 尺が <c>untilZoneChange</c> のカットを、次の区間が確定した時点で畳む
        /// （<see cref="TakeRunner.NotifyZoneCommitted"/> から呼ぶ）。
        /// </summary>
        public void NotifyZoneChanged(float now)
        {
            if (CurrentStepWait() == WaitZoneChange) SetCurrentStepEnd(now);
        }

        /// <summary>走行中のカットの尺（負値なら待っている相手）。走行していなければ 0。</summary>
        private float CurrentStepWait()
        {
            if (!_running || _activeTake < 0 || _activeTake >= _defs.Length) return 0f;
            float[] d = _defs[_activeTake].stepDurSec;
            return (d != null && _activeStep >= 0 && _activeStep < d.Length) ? d[_activeStep] : 0f;
        }

        /// <summary>
        /// 現カットの終了時刻を外部から与える（素材が無い <c>untilClipEnd</c> を既定尺で畳む等）。
        /// watchdog は別途効き続けるので、ここで未来を指定しても演出は必ず終わる。
        /// </summary>
        public void SetCurrentStepEnd(float endTime)
        {
            if (_running) _stepEnd = endTime;
        }

        /// <summary>走行中の演出を外部都合で畳む（卓の「■ 画面を取り返す」・ラン開始・定義差し替えの後片付け）。</summary>
        public void AbortActive()
        {
            _running = false;
            _dismissPending = false;
            _activeTake = -1;
            _activeStep = -1;
            // 画面を取り返した直後に、溜まっていた持ち越しが噴き出さないようにする。
            DropAllCarry(DropReason.BlockerGone);
        }

        // ---- 内部 ----

        private void StartTake(int index, float now, int baseZoneCam)
        {
            _running = true;
            _activeTake = index;
            _activeStep = 0;
            _fired[index] = true;
            _baseZoneCam = baseZoneCam;
            float[] durs = _defs[index].stepDurSec;
            _stepBeganAt = now;
            _stepEnd = StepEndTime(now, durs.Length > 0 ? durs[0] : 0f);
            _deadline = now + TakeSchema.ResolveMaxDuration(_defs[index].maxDurationSec);
        }

        // 尺が負（untilClipEnd / untilZoneChange）なら外部通知待ち = 無限大。watchdog が上限を保証する。
        private static float StepEndTime(float now, float durSec)
            => durSec < 0f ? float.PositiveInfinity : now + durSec;

        private Decision BeginStepDecision(bool takeStarted) => new()
        {
            action = Action.BeginStep,
            takeIndex = _activeTake,
            stepIndex = _activeStep,
            takeStarted = takeStarted,
        };

        private Decision EndTakeDecision(int latestZoneCam, EndReason reason, bool chainNext = false)
        {
            int take = _activeTake;
            _running = false;
            _activeTake = -1;
            _activeStep = -1;
            // 走行が終わったので、消化されなかった報告を次の演出へ持ち越さない
            //（線待ち・untilMark が「カットが始まる前の事象を数えない」のと同じ理由）。
            _dismissPending = false;
            return new Decision
            {
                action = Action.EndTake,
                takeIndex = take,
                returnCamera = latestZoneCam,
                reason = reason,
                chainNext = chainNext,
            };
        }

        /// <summary>この演出が終わった直後に出せるものが控えているか（連続の判定）。</summary>
        private bool HasNextReady() => !_suppressed && (FindReady() >= 0 || _carryIndex.Count > 0);

        // 進入区間の enter / line 演出を武装する（offsetSec 昇順 → 同値は配列順）。
        // ライントリガー（onLine）は offsetSec を持たない（契約上 0）ので、同値タイブレーク＝配列順で並ぶ。
        // 武装さえすれば due 判定（IsDue）が時刻と位置を出し分けるので、ここに分岐は要らない。
        private void ArmEnterTakes(int lap, int camera, float now)
        {
            var order = new List<int>();
            for (int i = 0; i < _defs.Length; i++)
            {
                Def d = _defs[i];
                if (d.onExit || d.lap != lap || d.camera != camera) continue;
                if (d.once && _fired[i]) continue;
                if (d.stepDurSec.Length == 0) continue; // カット空の演出は無視
                order.Add(i);
            }
            order.Sort((a, b) =>
            {
                int c = _defs[a].offsetSec.CompareTo(_defs[b].offsetSec);
                return c != 0 ? c : a.CompareTo(b);
            });
            foreach (int i in order)
            {
                _armedIndex.Add(i);
                _armedDue.Add(now + (_defs[i].offsetSec < 0f ? 0f : _defs[i].offsetSec));
                _armedState.Add(Armed.Waiting);
            }
        }

        // 開始条件を満たした武装スロットを Ready にする（**事象の取りこぼしを防ぐ唯一の場所**）。
        private void LatchReady(float now, LineCrossLogic.State[]? lines)
        {
            for (int k = 0; k < _armedIndex.Count; k++)
                if (_armedState[k] == Armed.Waiting && IsDue(k, now, lines)) _armedState[k] = Armed.Ready;
        }

        // 発火を待っている先頭の Ready を返す（無ければ -1）。
        private int FindReady()
        {
            for (int k = 0; k < _armedIndex.Count; k++)
                if (_armedState[k] == Armed.Ready) return k;
            return -1;
        }

        // 武装スロット k の発火条件。時刻トリガーは due 時刻、ライントリガーは今フレームの横断で決まる。
        private bool IsDue(int k, float now, LineCrossLogic.State[]? lines)
        {
            Def d = _defs[_armedIndex[k]];
            if (!d.onLine) return now >= _armedDue[k];
            if (lines == null || d.lineIndex < 0 || d.lineIndex >= lines.Length) return false;
            LineCrossLogic.State s = lines[d.lineIndex];
            // 横断は事象だが、武装がゾーン確定（dwell 0.5s）で起きるため猶予を持たせる
            // （ゾーンの入口すぐの線が永久に発火しないのを防ぐ・LineCrossLogic.CrossLatchSec）。
            if (now - s.crossedAtSec > LineCrossLogic.CrossLatchSec) return false;
            // **ラインは担当カメラに紐づく**（区間紐づけ）。別のゾーンのラインを踏んでも発火させない。
            // 武装自体が区間限定なので通常は一致するが、著作ミス（別ゾーンのラインを選んだ）を
            // ここで構造的に無効化する。camera<0 は未指定 = どの区間でも可。
            return s.camera < 0 || s.camera == d.camera;
        }

        // ライブ卓の介入中に条件を満たしたものを決着させる（待たせない）。
        //   skip        → 武装から外して破棄（報告する）
        //   fireOnExit  → 離脱時のみ発火しうる状態へ落とす
        private void SettleReadyWhileSuppressed()
        {
            for (int k = _armedIndex.Count - 1; k >= 0; k--)
            {
                if (_armedState[k] != Armed.Ready) continue;
                if (_defs[_armedIndex[k]].skipWhenMissed)
                {
                    int dropped = _armedIndex[k];
                    RemoveArmedAt(k, out _);
                    TakeDropped?.Invoke(dropped, DropReason.ScreenBusyAtExit);
                }
                else _armedState[k] = Armed.DeferredToExit;
            }
        }

        // 離脱時、「出る資格があったのに出られずに終わる」演出を報告する。
        // 「同時 1 本」で捨てるのは設計どおりだが、**捨てたことは必ず言う**（黙って消さない）。
        // skip 指定のものは著作者が「出さない」と言っているので報告しない。
        private void ReportRemainingDrops(int lap, int camera, int firedIndex, bool screenBusy)
        {
            if (TakeDropped == null) return;
            for (int i = 0; i < _defs.Length; i++)
            {
                if (i == firedIndex) continue;
                Def d = _defs[i];
                if (d.lap != lap || d.camera != camera) continue;
                if (d.stepDurSec.Length == 0) continue;
                if (d.once && _fired[i]) continue;
                if (!d.onExit && (d.skipWhenMissed || !IsArmed(i))) continue;
                // 持ち越したものはまだ生きている（捨てていない）ので報告しない。
                if (_carryIndex.Contains(i)) continue;
                TakeDropped.Invoke(i, screenBusy ? DropReason.ScreenBusyAtExit : DropReason.LostToAnotherTake);
            }
        }

        // 離脱の瞬間に発火する候補を配列順で 1 つ選ぶ。
        //   - at=exit で未発火のもの
        //   - 武装中の enter / line 演出のうち ifMissed=fireOnExit のもの
        //     （時刻未達・超過どちらでも / ライントリガーなら「そのラインを通過しなかった」場合）
        private int FindExitCandidate(int lap, int camera)
        {
            for (int i = 0; i < _defs.Length; i++)
            {
                Def d = _defs[i];
                if (d.lap != lap || d.camera != camera) continue;
                if (d.stepDurSec.Length == 0) continue;
                if (d.once && _fired[i]) continue;
                if (d.onExit || (!d.skipWhenMissed && IsArmed(i))) return i; // 配列順で先頭が勝つ
            }
            return -1;
        }

        private bool IsArmed(int takeIndex) => _armedIndex.Contains(takeIndex);

        // ---- 持ち越し（wait="chain"）----

        /// <summary>持ち越し中の演出数（テスト・診断用）。</summary>
        public int CarryCount => _carryIndex.Count;

        /// <summary>
        /// 離脱の瞬間、**画面が塞がっていたせいで**出られなかった演出を持ち越す。
        ///
        /// 条件は 4 つとも必須:
        ///   ①著作者が wait="chain" と明示している ②離脱の瞬間に実際に画面が塞がっていた（因果）
        ///   ③開始条件は既に満たしていた（Ready / DeferredToExit。時刻に届かなかっただけのものは持ち越さない）
        ///   ④at=exit ではない（離脱時の演出を別の区間で出すと文脈が最も壊れる）
        ///
        /// 「歩くのが速かっただけ」で持ち越さないのがこの機構の芯。持ち越しの根拠が
        /// 「別の演出が画面を持っていた」という**システム内部の都合**に限られるので、
        /// 体験者から見れば「前の演出が終わってから続けて出た」1 つの流れになる。
        /// </summary>
        private void CarryOverArmed(bool screenBusy, int firedIndex, float now)
        {
            if (!screenBusy) return;
            for (int k = 0; k < _armedIndex.Count; k++)
            {
                int i = _armedIndex[k];
                if (i == firedIndex) continue;
                if (!_defs[i].chainWait || _defs[i].onExit) continue;
                if (_armedState[k] != Armed.Ready && _armedState[k] != Armed.DeferredToExit) continue;
                if (_carryIndex.Contains(i)) continue;
                _carryIndex.Add(i);
                _carryUntil.Add(now + CarryMaxWaitSec);
            }
            // 上限を超えたら**古い方から**捨てる（新しい因果の方が体験に近い）。捨てたら必ず報告する。
            while (_carryIndex.Count > MaxCarrySlots)
            {
                int dropped = _carryIndex[0];
                RemoveCarryAt(0);
                TakeDropped?.Invoke(dropped, DropReason.CarryExpired);
            }
        }

        private void ExpireCarry(float now)
        {
            for (int k = _carryIndex.Count - 1; k >= 0; k--)
            {
                if (now < _carryUntil[k]) continue;
                int dropped = _carryIndex[k];
                RemoveCarryAt(k);
                TakeDropped?.Invoke(dropped, DropReason.CarryExpired);
            }
        }

        /// <summary>持ち越しを全部畳む。<paramref name="report"/> が false のときはラン境界（報告不要）。</summary>
        private void DropAllCarry(DropReason reason, bool report = true)
        {
            if (report && TakeDropped != null)
                for (int k = 0; k < _carryIndex.Count; k++) TakeDropped.Invoke(_carryIndex[k], reason);
            _carryIndex.Clear();
            _carryUntil.Clear();
        }

        private void RemoveCarryAt(int k)
        {
            _carryIndex.RemoveAt(k);
            _carryUntil.RemoveAt(k);
        }

        private void RemoveArmedAt(int k, out int takeIndex)
        {
            takeIndex = _armedIndex[k];
            _armedIndex.RemoveAt(k);
            _armedDue.RemoveAt(k);
            _armedState.RemoveAt(k);
        }

        private void ClearArmed()
        {
            _armedIndex.Clear();
            _armedDue.Clear();
            _armedState.Clear();
        }
    }
}
