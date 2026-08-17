#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 事前オーサリング済みスケジュール（何周目のどのカメラで cue を出すか）の
    /// 純粋な判定ロジック。MonoBehaviour（<see cref="CueScheduler"/>）から分離して EditMode テスト可能にする。
    ///
    /// 判定は「ゾーン進入（= アクティブカメラ切替）時点」で 1 回だけ行う（enter 時点評価）。
    ///   - lap（1 始まり）と camera が一致するエントリを配列先頭から探す
    ///   - once=true のエントリは <see cref="MarkFired"/> 済みならスキップ
    ///   - liveCueActive（control.activeCue 非空 = ライブ手動オーバーライド中）なら一切発火しない
    /// delaySec は Decision に載せて返すだけ（実際の遅延計時は MonoBehaviour 側）。
    /// </summary>
    public sealed class CueScheduleLogic
    {
        /// <summary>
        /// cue の任意上書き（タイムライン区間 cue 用）。has=false なら resolver の解決値をそのまま使う。
        /// has=true なら strength / fade / trim を丸ごと差し替える（部分パッチではなく全置換。
        /// JsonUtility は個別フィールドの present を判別できないため、区間 cue の hasOverride を present-flag に使う）。
        /// </summary>
        public struct CueOverride
        {
            public bool has;
            public float strength;
            public float fadeIn;
            public float fadeOut;
            public float trimStart;
            public float trimEnd;
        }

        /// <summary>スケジュール 1 行分。camera はカメラ index（ゾーンは cameraIndex でキー）。</summary>
        public struct Entry
        {
            public int lap;
            public int camera;
            public string cueId;
            public float delaySec;
            public bool once;
            public CueOverride ov;   // タイムライン由来の cue 上書き（legacy schedule は has=false）
        }

        /// <summary>enter 時点評価の結果。fire=false なら他フィールドは無効。</summary>
        public struct Decision
        {
            public bool fire;
            public int entryIndex;
            public string cueId;
            public float delaySec;
            public CueOverride ov;
        }

        private Entry[] _entries = Array.Empty<Entry>();
        private bool[] _fired = Array.Empty<bool>();

        /// <summary>登録済みエントリ数（テスト用）。</summary>
        public int Count => _entries.Length;

        /// <summary>スケジュールを差し替える。once の発火済みフラグはリセットされる（新ラン相当）。</summary>
        public void SetEntries(Entry[] entries)
        {
            _entries = entries ?? Array.Empty<Entry>();
            _fired = new bool[_entries.Length];
        }

        /// <summary>once の発火済みフラグを全消去する（体験の明示リセット用）。</summary>
        public void ResetFired()
        {
            for (int i = 0; i < _fired.Length; i++) _fired[i] = false;
        }

        /// <summary>
        /// ゾーン進入時点の評価。(lap, camera) 一致 + 未発火（once） + 非抑止のとき最初のエントリを返す。
        /// 発火判定のみを行い、状態は変えない（MarkFired は実発火時に別途呼ぶ）。
        /// </summary>
        public Decision Evaluate(int lap, int camera, bool liveCueActive)
        {
            if (liveCueActive) return default;
            for (int i = 0; i < _entries.Length; i++)
            {
                Entry e = _entries[i];
                if (e.lap != lap || e.camera != camera) continue;
                if (e.once && _fired[i]) continue;
                return new Decision { fire = true, entryIndex = i, cueId = e.cueId, delaySec = e.delaySec, ov = e.ov };
            }
            return default;
        }

        /// <summary>エントリを発火済みにする（once の 1 回制限。実際に PlayCue した時点で呼ぶ）。</summary>
        public void MarkFired(int entryIndex)
        {
            if (entryIndex >= 0 && entryIndex < _fired.Length) _fired[entryIndex] = true;
        }

        /// <summary>
        /// 現在の周回進行から「次に発火する予定のエントリ」を読み取り専用で返す（StatusHud の予定表示用）。
        /// 状態は変えない。順序は周回×コース位置の直線化ステップで評価する:
        ///   step = (lap-1)*n + positionOf(camera)。currentPos はスタートからの現在コース位置。
        /// currentStep 以降で最小 step の未発火（once）エントリを返す。同一 step は配列先頭が勝つ
        /// （<see cref="Evaluate"/> と同じタイブレーク）。order に無いカメラのエントリは配置できないので除外。
        /// order 空 or 該当なしは false。
        /// </summary>
        public bool TryGetNext(int currentLap, int currentPos, int[] order, out Entry next)
        {
            next = default;
            int n = order?.Length ?? 0;
            if (n == 0) return false;

            long currentStep = (long)(currentLap - 1) * n + currentPos;
            bool found = false;
            long bestStep = long.MaxValue;

            for (int i = 0; i < _entries.Length; i++)
            {
                Entry e = _entries[i];
                if (e.once && _fired[i]) continue;
                int pos = IndexOf(order!, e.camera);
                if (pos < 0) continue; // コース順に居ないカメラは配置不能
                long step = (long)(e.lap - 1) * n + pos;
                if (step < currentStep) continue;
                // より早い step、または同 step で配列先頭側を優先（>= で先勝ちを保つ）。
                if (step < bestStep)
                {
                    bestStep = step;
                    next = e;
                    found = true;
                }
            }
            return found;
        }

        private static int IndexOf(int[] order, int value)
        {
            for (int i = 0; i < order.Length; i++) if (order[i] == value) return i;
            return -1;
        }
    }

    /// <summary>
    /// スケジュール発火の実行体。LapCounter からゾーン進入（camera, lap）を受け、
    /// <see cref="CueScheduleLogic"/> で判定 → delaySec 経過後に
    /// <see cref="ScreenOverlayController.PlayCue(OverlayCueData)"/> をローカル直接呼ぶ（サーバ不要 = オフライン発火）。
    ///
    /// スケジュール本体・cue 解決・ライブ抑止状態は <see cref="ShowControlClient"/> から供給される
    /// （Streaming 内なので直接メソッド注入。Tracking からは LapCounter が camera/lap を push する）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CueScheduler : MonoBehaviour
    {
        [Tooltip("cue を再生する ScreenOverlayController。null なら同 GameObject から取得を試みる。")]
        [SerializeField] private ScreenOverlayController? overlay;

        private readonly CueScheduleLogic _logic = new();

        // cueId → OverlayCueData の解決関数（sa:// / server URL 解決込み）。ShowControlClient が注入。
        private Func<string, OverlayCueData?>? _cueResolver;

        // control.activeCue 非空の間 true。ライブ手動発火中はスケジュールを抑止する。
        private bool _liveCueActive;

        // 進入からの遅延を計時する単一の保留発火（次の進入 or 抑止で置き換え / キャンセル）。
        private bool _pending;
        private float _pendingRemaining;
        private string _pendingCueId = "";
        private int _pendingEntryIndex = -1;
        private CueScheduleLogic.CueOverride _pendingOv;

        /// <summary>
        /// ゾーン進入（LapCounter 由来）を受けた直後に発火する。引数は
        /// <c>(camera, segmentLap, progressLap)</c>。
        ///
        /// ⚠⚠ <b>周回数が 2 つあるのは、逆走で戻ったときに両者が食い違うから</b>（2026-08-17）。
        ///   - <b>segmentLap</b> = いま体験者が居る区間の周（逆走で戻る）。
        ///     <b>区間キー (lap, camera) はこちら</b> — 演出・区間 post / BGM・端末内録画・実測滞在
        ///   - <b>progressLap</b> = 進行ポインタが数えた周（単調増加）。
        ///     <b>体験の終了判定はこちら</b>（<see cref="ShowRunLogic.NotifyLap"/>）。
        ///     segmentLap を渡すと、帰りの A で引き返した瞬間に周回が戻って<b>体験が終わらなくなる</b>
        ///
        /// 3 引数にしてあるのは、購読者に「どちらの周を使うか」をコンパイラが必ず選ばせるため。
        /// 片方だけ暗黙に流すと、選び間違いが沈黙したまま実機でしか出ない。
        ///
        /// TimelineDirector が購読して区間 post / インサートを分配する（Insert source の切替はここに来ないので
        /// 周回・区間追跡は体験者のゾーン進行だけを見る）。cue 評価の後に発火するため、インサート cue が
        /// 区間 cue より後に PlayCue され最後の命令が勝つ。
        /// </summary>
        public event Action<int, int, int>? CameraEntered;

        private void Awake()
        {
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
        }

        /// <summary>スケジュール定義を差し替える（ShowControlClient が show.json schedule から供給）。</summary>
        public void SetScheduleFromDefs(ShowScheduleEntryDef[]? defs)
        {
            if (defs == null || defs.Length == 0)
            {
                _logic.SetEntries(Array.Empty<CueScheduleLogic.Entry>());
                _pending = false;
                return;
            }
            var entries = new CueScheduleLogic.Entry[defs.Length];
            for (int i = 0; i < defs.Length; i++)
            {
                var d = defs[i];
                entries[i] = new CueScheduleLogic.Entry
                {
                    lap = d.lap,
                    camera = d.camera,
                    cueId = d.cueId ?? "",
                    delaySec = d.delaySec,
                    once = d.once,
                };
            }
            _logic.SetEntries(entries);
            _pending = false; // スケジュール変更で計時中の発火は無効化
        }

        /// <summary>
        /// 評価用エントリを直接差し替える（TimelineDirector が timeline 区間 cues[] を override 付きで flatten して供給）。
        /// legacy schedule 経路（SetScheduleFromDefs）と排他で、ShowControlClient が timeline supersede を調停する。
        /// </summary>
        public void SetEntries(CueScheduleLogic.Entry[] entries)
        {
            _logic.SetEntries(entries ?? Array.Empty<CueScheduleLogic.Entry>());
            _pending = false;
        }

        /// <summary>cueId → OverlayCueData の解決関数を注入する（URL 解決を ShowControlClient に集約）。</summary>
        public void SetCueResolver(Func<string, OverlayCueData?> resolver) => _cueResolver = resolver;

        /// <summary>
        /// 現在の周回進行から「次に発火する予定のエントリ」を照会する（StatusHud の予定表示用・読み取り専用）。
        /// timeline / legacy schedule のどちらの経路でも有効エントリは <see cref="_logic"/> に集約されているため、
        /// ここ 1 点で両方をカバーできる。currentPos / order は LapCounter の進行状態を渡す。
        /// </summary>
        public bool TryGetNextCue(int currentLap, int currentPos, int[] order, out CueScheduleLogic.Entry next)
            => _logic.TryGetNext(currentLap, currentPos, order, out next);

        /// <summary>control.activeCue 非空（ライブ手動オーバーライド中）を通知する。</summary>
        public void SetLiveCueActive(bool active) => _liveCueActive = active;

        /// <summary>体験の明示リセット（once 発火済みを全消去）。</summary>
        public void ResetRun()
        {
            _logic.ResetFired();
            _pending = false;
        }

        // 本編（Run 相）かどうか。導入中と終了後は false。既定 true = ShowRunDirector が居ないシーン・
        // 旧ビルドでは従来どおり常に流れる（自己修復の流儀: 新機能の不在で既存機能を止めない）。
        private bool _showGateOpen = true;

        /// <summary>
        /// 区間進行を下流へ流すかを切り替える（<see cref="ShowRunDirector"/> が導入・終了で閉じる）。
        /// 閉じている間は保留中の発火も捨てる。
        /// </summary>
        public void SetShowGate(bool open)
        {
            _showGateOpen = open;
            if (!open) _pending = false;
        }

        /// <summary>本編の区間進行が下流へ流れているか（HUD / 診断表示用）。</summary>
        public bool ShowGateOpen => _showGateOpen;

        /// <summary>
        /// ゾーン進入（アクティブカメラ切替）を LapCounter から受ける。enter 時点で 1 回評価する。
        /// 一致エントリがあれば delaySec 経過後に発火予約（delaySec&lt;=0 は即発火）。
        /// 一致が無ければ「その進入で条件を満たすものは無い」ので保留中の発火はキャンセルする
        /// （トリガーゾーンを抜けたら遅延中の cue は取り消す）。
        /// </summary>
        /// <param name="camera">確定したゾーンのカメラ index。</param>
        /// <param name="lap">
        /// <b>区間の周</b>（逆走で戻る）。cue の (lap, camera) 一致はこれで見る —
        /// 引き返した先は「前にそこに居たときの周」として扱う（<see cref="CameraEntered"/> の注記）。
        /// </param>
        /// <param name="progressLap">
        /// <b>進行の周</b>（単調増加）。ここでは使わず <see cref="CameraEntered"/> へ中継するだけ。
        /// 終了判定を持つ購読者（<c>ShowRunDirector</c>）が読む。
        /// </param>
        public void NotifyCameraEntered(int camera, int lap, int progressLap)
        {
            // 本編の外（導入中・終了後）は区間進行を一切下流へ流さない。演出の武装・端末内録画・
            // 区間 post / BGM・実測滞在がまとめて止まる（下流それぞれに条件を配ると必ず片方を忘れる）。
            // 画面のカメラ切替は Director 側なのでここでは止まらない — 導入では映像は出したいから。
            if (!_showGateOpen) { _pending = false; return; }

            CueScheduleLogic.Decision d = _logic.Evaluate(lap, camera, _liveCueActive);
            if (!d.fire)
            {
                _pending = false;
            }
            else if (d.delaySec <= 0f)
            {
                _pending = false;
                Fire(d.cueId, d.entryIndex, d.ov);
            }
            else
            {
                _pending = true;
                _pendingRemaining = d.delaySec;
                _pendingCueId = d.cueId;
                _pendingEntryIndex = d.entryIndex;
                _pendingOv = d.ov;
            }
            // cue 評価の後に TimelineDirector（区間 post / インサート）を駆動する。
            // 進入は Insert source ではここへ来ない（LapCounter が Zone のみ Feed するため）ので、
            // これが「体験者のゾーン進行」の単一かつ deterministic な信号になる。
            CameraEntered?.Invoke(camera, lap, progressLap);
        }

        private void Update()
        {
            if (!_pending) return;
            // 計時中にライブ手動発火が入ったら抑止（enter 時点評価の一貫性: 抑止解除後の再発火はしない）。
            if (_liveCueActive) { _pending = false; return; }
            _pendingRemaining -= Time.deltaTime;
            if (_pendingRemaining > 0f) return;
            _pending = false;
            Fire(_pendingCueId, _pendingEntryIndex, _pendingOv);
        }

        private void Fire(string cueId, int entryIndex, CueScheduleLogic.CueOverride ov)
        {
            OverlayCueData? data = _cueResolver?.Invoke(cueId);
            if (data == null)
            {
                Debug.LogWarning($"[CueScheduler] cue 未解決のため発火スキップ: id={cueId}（cues[] に定義があるか確認）");
                return;
            }
            // タイムライン区間の上書きを適用する。ResolveCue は毎回新規 OverlayCueData を返すので
            // 直接パッチしてよい（共有インスタンスを汚さない）。
            if (ov.has)
            {
                data.strength = ov.strength;
                data.fadeInSeconds = ov.fadeIn;
                data.fadeOutSeconds = ov.fadeOut;
                data.trimStart = ov.trimStart;
                data.trimEnd = ov.trimEnd;
            }
            overlay?.PlayCue(data);
            _logic.MarkFired(entryIndex);
            Debug.Log($"[CueScheduler] スケジュール発火 cue={cueId}{(ov.has ? " (override)" : "")}");
        }
    }
}
