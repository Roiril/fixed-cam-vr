#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// **画面層**の切替ガード（クールダウン / cue・インサート・override 凍結 / 手動優先）の純ロジック。
    /// MonoBehaviour（<see cref="CameraSwitchDirector"/>）から分離して EditMode テスト可能にする。時刻は引数で受ける。
    ///
    /// **最小滞在（dwell）はここには無い** — dwell は「体験者がそのゾーンに居るか」という*人の層*の判定で、
    /// 画面が凍結していても進み続けなければならないため <see cref="ZoneProgressionLogic"/> へ分離した
    /// （設計 2026-07-25_shot-timeline-foundation.md 段 B）。本ロジックが受け取る
    /// <see cref="SetAmbient"/> の目標は **dwell 済みの確定ゾーン**である。
    ///
    /// ルール（計画 2026-07-19_viewer-ux.md）:
    ///   - switchCooldownSec: 切替直後の再切替をロック（Murch の最小ショット長）
    ///   - cue / インサート / override 中の自動切替は凍結（目標は保持し、解除後に最新を適用）
    ///   - 手動切替はクールダウンのみ尊重。手動後 manualHoldSec は自動切替を抑止（手動優先）
    /// </summary>
    public sealed class SwitchDirectorLogic
    {
        /// <summary>切替直後の再切替ロック（クールダウン）の既定 (秒)。部屋スケールに合わせ 2.0s から短縮。</summary>
        public const float DefaultCooldownSec = 0.5f;

        /// <summary>
        /// show.json control の present 判定。<paramref name="overrideValue"/> が正なら現場調整値を採用し、
        /// 0 / 未指定（JsonUtility 既定 0）ならコード既定 <paramref name="defaultValue"/> へ戻す。
        /// </summary>
        public static float ResolveTiming(float overrideValue, float defaultValue)
            => overrideValue > 0f ? overrideValue : defaultValue;

        private float _cooldownSec;
        private float _manualHoldSec;

        private int _current;
        private float _lastSwitchTime = float.NegativeInfinity;
        private float _lastManualTime = float.NegativeInfinity;
        private bool _cueActive;
        private bool _insertActive;
        private bool _overrideActive;

        // 画面へ未反映の「既定の映し先」（＝確定ゾーンのカメラ）。最新の 1 つだけを保持し、
        // 凍結 / クールダウン / manualHold の解除待ちで適用する。
        private bool _hasPendingZone;
        private int _pendingZone;

        /// <summary>現在アクティブとして把握しているカメラ index。</summary>
        public int Current => _current;

        /// <summary>保留中の自動切替要求があるか（＝抑止中。HUD 表示用）。</summary>
        public bool HasPendingZone => _hasPendingZone;

        /// <summary>保留中の目標カメラ index（<see cref="HasPendingZone"/> が false のとき無効）。</summary>
        public int PendingZone => _pendingZone;

        /// <summary>cue 再生中フラグ（自動切替を凍結する）。</summary>
        public bool CueActive => _cueActive;

        /// <summary>インサート表示中フラグ（cue と独立に自動切替を凍結する）。</summary>
        public bool InsertActive => _insertActive;

        /// <summary>Web cameraOverride 中フラグ（cue/insert と独立に自動切替を凍結する）。HUD/デバッグ用。</summary>
        public bool OverrideActive => _overrideActive;

        public void Configure(float cooldownSec, float manualHoldSec)
        {
            _cooldownSec = Mathf.Max(0f, cooldownSec);
            _manualHoldSec = Mathf.Max(0f, manualHoldSec);
        }

        /// <summary>現在カメラを与えて初期化する。時間ガードは「直後の切替を許す」状態に戻す。</summary>
        public void Reset(int current)
        {
            _current = current;
            _lastSwitchTime = float.NegativeInfinity;
            _lastManualTime = float.NegativeInfinity;
            _hasPendingZone = false;
            // 凍結フラグは安全側（false）へ。OnEnable 経路で insert / cue 凍結が残ったまま復帰して
            // ゾーン自動切替が始まらない事故を防ぐ（InsertController 側の後片付けと二重の保険）。
            _insertActive = false;
            _cueActive = false;
            _overrideActive = false;
        }

        public void SetCueActive(bool active) => _cueActive = active;

        /// <summary>
        /// Web cameraOverride の適用/解除。凍結し、遷移のたびに stale 保留を無条件クリアする
        /// （enter: override 前の保留を捨てる / exit: 解除後は tracker 再 Pick が権威なので保留を残さない）。
        /// cue/insert 凍結は保留を保持するが override は保持しない（tracker 自己回復が復帰の権威のため）。
        /// </summary>
        public void SetOverrideActive(bool active)
        {
            _overrideActive = active;
            _hasPendingZone = false;
        }

        /// <summary>
        /// インサート表示中の凍結を設定する（cue 凍結と同型）。true の間ゾーン自動切替は commit しないが
        /// 最新の保留ゾーンは保持し続ける（インサート復帰先の算出に使う）。
        /// </summary>
        public void SetInsertActive(bool active) => _insertActive = active;

        /// <summary>
        /// 本ロジックを介さない直接切替（Web cameraOverride 等）を同期する。現在カメラとクールダウンを
        /// 更新し、保留がその index を指していれば解消する。<see cref="CameraSwitchDirector"/> は
        /// registry.ActiveChanged からこれを呼ぶ（自分の commit も idempotent に通る）。
        /// </summary>
        public void NotifyExternalSwitch(int index, float now)
        {
            _current = index;
            _lastSwitchTime = now;
            if (_hasPendingZone && _pendingZone == index) _hasPendingZone = false;
        }

        /// <summary>
        /// 手動切替要求（右 A/B・キーボード）。クールダウンのみ尊重し dwell/cue は無視する。
        /// 受理したら手動時刻を更新（manualHold で自動を抑止）し commit すべき index を返す。
        /// </summary>
        public bool RequestManual(int target, float now, out int commitTarget)
        {
            commitTarget = _current;
            if (target == _current) return false;
            if (now - _lastSwitchTime < _cooldownSec) return false; // 最小ショット長
            _current = target;
            _lastSwitchTime = now;
            _lastManualTime = now;
            _hasPendingZone = false;
            commitTarget = target;
            return true;
        }

        /// <summary>
        /// 「既定として映すべきカメラ」を設定する（<see cref="ZoneProgressionLogic"/> が dwell を満たして
        /// 確定させたゾーンのカメラ）。即 commit せず保持し、凍結 / クールダウン / manualHold の解除を
        /// <see cref="Tick"/> が待つ。現在カメラと同じなら保持を解消する。
        /// </summary>
        public void SetAmbient(int target)
        {
            if (target < 0) return; // 無効 index（ゾーン外等）は保持に触れず無視
            if (target == _current)
            {
                _hasPendingZone = false;
                return;
            }
            _hasPendingZone = true;
            _pendingZone = target;
        }

        /// <summary>
        /// 毎フレーム評価。保持中の既定映し先がクールダウン・凍結・manualHold の全ゲートを通過したら commit する。
        /// </summary>
        public bool Tick(float now, out int commitTarget)
        {
            commitTarget = _current;
            if (!_hasPendingZone) return false;
            if (_pendingZone == _current) { _hasPendingZone = false; return false; }
            if (_cueActive || _insertActive || _overrideActive) return false; // cue / インサート / override 中は凍結
            if (now - _lastSwitchTime < _cooldownSec) return false;         // クールダウン（最小ショット長）
            if (now - _lastManualTime < _manualHoldSec) return false;       // 手動優先の抑止
            _current = _pendingZone;
            _lastSwitchTime = now;
            _hasPendingZone = false;
            commitTarget = _current;
            return true;
        }
    }

    /// <summary>
    /// カメラ切替を一本化して時間軸ガード（<see cref="SwitchDirectorLogic"/>）と dip-to-black 演出を掛ける実行体。
    /// PlayerZoneTracker / OvrControllerBridge / CameraSwitchInput はここを経由して切替を要求する。
    /// Web cameraOverride（ShowControlClient）は従来どおり registry.SetActive を直接叩くが、
    /// registry.ActiveChanged 購読で本 Director も同期する（既存挙動は不変）。
    ///
    /// dip-to-black: 黒へ <see cref="dipDownSec"/> → registry.SetActive → <see cref="dipUpSec"/> で立ち上げ。
    /// 減光は ScreenComposite の post 数式（Web FS_POST 一致規約）とは別系統の <c>_SwitchDim</c> uniform で行う。
    /// 配置先は Screen（MjpegScreen と同居し material を共有する GameObject）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CameraSwitchDirector : MonoBehaviour
    {
        /// <summary>
        /// 確定切替の出どころ。<see cref="SwitchCommitted"/> に載せて周回カウント（LapCounter）が
        /// 「体験者のゾーン進行（Zone）だけを数え、スタッフ手動 / Web 固定 / 外部同期は数えない」を実現する。
        ///   - Zone: PlayerZoneTracker（体験者のゾーン移動）
        ///   - Manual: コントローラ / キーボードのスタッフ手動切替（Next/Prev/絶対指定）
        ///   - Override: Web オペレータ卓の cameraOverride（手動固定）
        ///   - Insert: タイムラインのインサートショット（InsertController 経由の差し込み・復帰）
        ///   - External: 上記いずれでもない registry への直接切替（後方互換の catch-all）
        /// LapCounter は Zone のみを周回へ数える（Manual/Override/Insert/External は数えない）。
        /// </summary>
        public enum SwitchSource { Zone, Manual, Override, Insert, External }

        private static readonly int SwitchDimId = Shader.PropertyToID("_SwitchDim");

        [Header("References")]
        [Tooltip("切替対象の CameraStreamRegistry。null なら何もしない。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("cue 再生中判定に使う ScreenOverlayController。null なら同 GameObject から取得。")]
        [SerializeField] private ScreenOverlayController? overlay;

        [Tooltip("切替開始と同時に鳴らす音マスク。null なら無音。")]
        [SerializeField] private SwitchAudioCue? audioCue;

        [Header("Timing")]
        // switchCooldownSec / minDwellSec は **非シリアライズ**（既定 0.5s）。旧 2/2 が 1.8m 四方の部屋スケールに
        // 過大で「歩くとカメラ切替が起きず、止まった瞬間に遅れて切替わる」不具合の原因だった。SerializeField だと
        // 既存シーン YAML に焼き付いた旧 2/2 がコード既定を上書きし続けるため、LongPressSec の const 化と同じ手法で
        // 非シリアライズ化して YAML の authored 値を無効化する。現場調整は show.json control の
        // minDwellSec / switchCooldownSec（>0 で上書き）→ ShowControlClient → ApplyTimingOverride 経由で行う。
        private float switchCooldownSec = SwitchDirectorLogic.DefaultCooldownSec;
        private float minDwellSec = ZoneProgressionLogic.DefaultDwellSec;

        // manualHold は撤廃（0 固定）。手動で別カメラを覗いても、次のゾーン境界を跨いだら dwell 0.5s のみで
        // ゾーンのカメラへ戻る「立ち止まってプレビュー」挙動にする。SerializeField だと既存シーン YAML に
        // 焼き付いた旧 8 がコード既定を上書きし続けるため、switchCooldownSec/minDwellSec と同じく非シリアライズ
        // 化して authored 値を無効化する。純ロジック（SwitchDirectorLogic）の manualHold 機構は変えず 0 を渡すだけ。
        private const float manualHoldSec = 0f;

        [Header("Dip-to-black transition")]
        [Tooltip("黒へ落とす時間 (秒)。この終端でソースを差し替える。")]
        [SerializeField, Min(0f)] private float dipDownSec = 0.07f;

        [Tooltip("黒から立ち上げる時間 (秒)。")]
        [SerializeField, Min(0f)] private float dipUpSec = 0.10f;

        private readonly SwitchDirectorLogic _logic = new();

        // 人の層の時計（dwell のみ）。画面が凍結していても進み続ける。
        // ⚠ 所在は暫定 — 段 C で ShotDirector を新設する際に人の層へ移す（ZoneProgressionLogic の注記参照）。
        private readonly ZoneProgressionLogic _progress = new();

        private Material? _material;
        private bool _subscribed;

        private enum DipState { Idle, Down, Up }
        private DipState _dip = DipState.Idle;
        private float _dipTimer;
        private int _dipTarget;
        private SwitchSource _dipSource = SwitchSource.External;

        // exit インサートの黒転換中差し替え。Zone dip が全黒で commit した直後（同期連鎖内）に
        // InsertExitRedirect が立てる。AdvanceDip がその commit 後に読み、Up へ上がる前に
        // 黒のまま insert カメラへ差し替える（中間カメラのフラッシュを見せない）。-1 = 差し替えなし。
        private int _blackRedirect = -1;

        // registry.SetActive を発火する直前に「今から起こす切替の出どころ」を書き、
        // 同期発火する ActiveChanged（→ OnRegistryActiveChanged）が読む。
        // registry を経由するすべての切替を単一点（OnRegistryActiveChanged）で SwitchCommitted へ写すため。
        // 既定は External（Director を経由しない直接切替の catch-all）。
        private SwitchSource _commitSource = SwitchSource.External;

        /// <summary>
        /// アクティブカメラの確定切替が起きた時、(index, source) で発火する。
        /// LapCounter が source==Zone のみを周回へ数える駆動点。
        /// Director 自身の dip 確定・Web override・その他の registry 直接切替すべてがここを通る。
        /// </summary>
        public event Action<int, SwitchSource>? SwitchCommitted;

        /// <summary>
        /// **ショーの時計**。体験者のゾーンが dwell を満たして確定するたびに発火する。
        /// 画面が凍結中（cue / インサート / override）でも dip 中でも発火する — 体験者は歩くから。
        /// <see cref="FixedCamVr.Tracking.LapCounter"/> が周回カウントの駆動点として購読する。
        ///
        /// <see cref="SwitchCommitted"/>（画面が実際に切り替わった）とは**別物**。周回を画面から独立させるのが
        /// 段 B の主眼で、これにより <c>InsertController</c> の「復帰を Zone に偽装する」ハックが不要になった。
        /// </summary>
        public event Action<int>? ZoneCommitted;

        /// <summary>ゾーン由来の切替が保留・抑止されているか（HUD 表示用）。dwell 待ちと画面凍結の両方を含む。</summary>
        public bool SwitchSuppressed => _progress.HasPending || _logic.HasPendingZone;

        /// <summary>dip-to-black 演出の実行中か（HUD 表示用）。</summary>
        public bool Dipping => _dip != DipState.Idle;

        /// <summary>インサート表示中か（手動切替は拒否される。拒否時の赤メッセージ判定に使う）。</summary>
        public bool InsertActive => _logic.InsertActive;

        private void Awake()
        {
            if (registry == null) registry = GetComponentInParent<CameraStreamRegistry>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (audioCue == null) audioCue = GetComponent<SwitchAudioCue>();
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
            _logic.Configure(switchCooldownSec, manualHoldSec);
            _progress.Configure(minDwellSec);
        }

        private void OnEnable()
        {
            if (registry != null && !_subscribed)
            {
                _logic.Reset(registry.ActiveIndex);
                _progress.Reset(registry.ActiveIndex);
                registry.ActiveChanged += OnRegistryActiveChanged;
                _subscribed = true;
            }
            SetDim(0f);
        }

        private void OnDisable()
        {
            if (registry != null && _subscribed) registry.ActiveChanged -= OnRegistryActiveChanged;
            _subscribed = false;
            // dip 中に無効化されると _SwitchDim が黒のまま残る。解除して状態も畳んでおく。
            SetDim(0f);
            _dip = DipState.Idle;
            _dipTimer = 0f;
            _blackRedirect = -1;   // 消化されない予約を次の dip へ持ち越さない
        }

        // 自分の commit も含めここに来る（同値の再設定で idempotent）。Web override 等の直接切替も同期する。
        // registry を経由する全切替の単一観測点。ここで SwitchCommitted を _commitSource 付きで発火する
        // （Director の dip 確定（AdvanceDip）と SetActiveExternal が SetActive の直前に source を設定済み、
        //  それ以外の registry 直接切替は既定の External）。
        private void OnRegistryActiveChanged(int index)
        {
            _logic.NotifyExternalSwitch(index, Time.time);
            SwitchCommitted?.Invoke(index, _commitSource);
        }

        /// <summary>
        /// Director の時間ガードを介さない外部起点の切替（Web cameraOverride 等）を、出どころを明示して行う。
        /// registry.SetActive を source 付きで叩き、OnRegistryActiveChanged で SwitchCommitted へ写す。
        /// dip 演出は掛けない（従来の override も即時切替だった）。
        /// </summary>
        public void SetActiveExternal(int index, SwitchSource source)
        {
            if (registry == null) return;
            _commitSource = source;
            registry.SetActive(index); // 同値なら no-op（ActiveChanged も出ない）
            _commitSource = SwitchSource.External;
        }

        /// <summary>巡回 Next（手動）。受理したら true（インサート表示中・dip 中・クールダウン中は false）。</summary>
        public bool Next()
        {
            if (registry == null || registry.Count == 0) return false;
            return RequestManual(CameraStreamRegistry.WrapIndex(registry.ActiveIndex + 1, registry.Count));
        }

        /// <summary>巡回 Prev（手動）。受理したら true（インサート表示中・dip 中・クールダウン中は false）。</summary>
        public bool Prev()
        {
            if (registry == null || registry.Count == 0) return false;
            return RequestManual(CameraStreamRegistry.WrapIndex(registry.ActiveIndex - 1, registry.Count));
        }

        /// <summary>
        /// 絶対指定の手動切替（キーボード数字キー等）。受理したら true。
        /// インサート表示中（<see cref="InsertActive"/>）は演出を割らないよう拒否して false。
        /// dip 中・クールダウン中・同一 index も false。
        /// </summary>
        public bool RequestManual(int target)
        {
            if (registry == null) return false;
            if (_logic.InsertActive) return false;   // インサート差し込み中は手動切替を破棄
            if (_dip != DipState.Idle) return false; // dip 中は新規切替を始めない（cooldown でも弾かれる）
            _logic.Configure(switchCooldownSec, manualHoldSec);
            if (_logic.RequestManual(target, Time.time, out int commit))
            {
                StartDip(commit, SwitchSource.Manual);
                return true;
            }
            return false;
        }

        /// <summary>
        /// ゾーン判定の結果（PlayerZoneTracker から）。**時計**（<see cref="ZoneProgressionLogic"/>）へ渡し、
        /// dwell を満たした時点で <see cref="ZoneCommitted"/> が発火する。画面への反映はそこから別途
        /// 凍結 / クールダウンのゲートを通る。
        /// </summary>
        public void RequestZone(int target)
        {
            // ゾーン外 / 無効カメラ index は無視（現カメラ表示を継続し、保留も触らない）。
            // tracker は keepLastWhenOutside=true で null を出さない構成だが、Director 単体でも安全側に倒す。
            if (registry == null || target < 0 || target >= registry.Count) return;
            _progress.Configure(minDwellSec);
            _progress.Request(target, Time.time);
        }

        /// <summary>
        /// show.json control（minDwellSec / switchCooldownSec）由来のタイミング現場調整を適用する。
        /// present 判定は <see cref="SwitchDirectorLogic.ResolveTiming"/>（&gt;0 で上書き、0/未指定はコード既定へ戻す）。
        /// dwell は人の層（<see cref="ZoneProgressionLogic"/>）、cooldown は画面層（<see cref="SwitchDirectorLogic"/>）へ
        /// 別々に配る。ShowControlClient がライブ long-poll / 端末キャッシュ / 焼き込みのいずれからでも呼ぶ。
        /// </summary>
        public void ApplyTimingOverride(float dwellSec, float cooldownSec)
        {
            minDwellSec = SwitchDirectorLogic.ResolveTiming(dwellSec, ZoneProgressionLogic.DefaultDwellSec);
            switchCooldownSec = SwitchDirectorLogic.ResolveTiming(cooldownSec, SwitchDirectorLogic.DefaultCooldownSec);
            _logic.Configure(switchCooldownSec, manualHoldSec);
            _progress.Configure(minDwellSec);
        }

        /// <summary>
        /// Web cameraOverride の適用/解除を通知する（ShowControlClient.Apply から呼ぶ）。
        /// 画面を凍結するのに加え、**時計側の未確定保留も捨てる** — override 中は ShowControlClient が
        /// PlayerZoneTracker ごと無効化する＝ゾーン入力そのものが切れるため、直前に積まれた
        /// dwell 待ちの目標は根拠を失うから（cue / インサートの凍結では捨てない）。
        /// </summary>
        public void SetOverrideActive(bool active)
        {
            _logic.SetOverrideActive(active);
            _progress.ClearPending();

            // 解除時は「いま体験者が居るゾーン」を既定映し先として貼り直す。体験者が一歩も動いていなくても
            // override で画面が持って行かれた分を**再計算**で戻すため（不変条件 3）。時計は遷移でしか発火せず、
            // tracker の再 Pick も同一ゾーンなら何も要求しないので、ここで明示的に貼り直す必要がある。
            if (!active && _progress.Current >= 0) _logic.SetAmbient(_progress.Current);
        }

        // ---- インサートショット（InsertController 用）----

        /// <summary>
        /// exit インサート: 区間離脱の Zone dip が黒へ落ちていく最中に呼ばれ、Up で切替先を見せる代わりに
        /// 黒のまま insert カメラへ差し替える予約を立てる。実際の registry 切替は AdvanceDip が黒の時点で行う
        /// （この場での registry.SetActive 再入を避ける）。ゾーン自動切替を凍結する。
        ///
        /// **予約を立てるのは dip が Down（まだ黒に達していない）のときだけ**。それ以外（Idle / Up）で予約すると
        /// 消化されるべき dip が存在せず、次に起きる無関係な dip（インサート復帰など）に持ち越されて
        /// 「戻るはずが insert カメラへ飛ぶ」事故になる。その場合は通常の dip で素直に切り替える。
        /// </summary>
        public void InsertExitRedirect(int insertCamera)
        {
            _logic.SetInsertActive(true);
            if (_dip == DipState.Down)
            {
                _blackRedirect = insertCamera;   // 進行中の黒転換に相乗り（中間カメラのフラッシュを出さない）
                return;
            }
            StartDip(insertCamera, SwitchSource.Insert);
        }

        /// <summary>
        /// enter インサート: 現在の映像から insert カメラへ dip-to-black で切り替える（Insert source ＝周回に数えない）。
        /// ゾーン自動切替を凍結する。表示中の映像から入るため通常の dip（Down→黒→切替→Up）を掛ける。
        /// </summary>
        public void InsertBegin(int insertCamera)
        {
            _logic.SetInsertActive(true);
            StartDip(insertCamera, SwitchSource.Insert);
        }

        /// <summary>
        /// インサート表示を終え、復帰カメラ（最新ゾーン）へ dip-to-black で戻す。
        /// ゾーン凍結を解除する（dip 完了後にゾーン自動切替が再開する）。
        ///
        /// 旧実装にあった <c>asZone</c>（復帰を Zone source に偽装して周回へ数えさせる）は**廃止**。
        /// 周回は画面ではなく <see cref="ZoneCommitted"/>（時計）が駆動するようになり、インサート中の
        /// 実ゾーン移動はその時点で既に周回へ反映済みだから（段 B）。
        /// </summary>
        public void InsertReturn(int returnCamera)
        {
            _logic.SetInsertActive(false);
            StartDip(returnCamera, SwitchSource.Insert);
        }

        /// <summary>
        /// **いま体験者が居るゾーン**のカメラ index を返す（時計 <see cref="ZoneProgressionLogic"/> の確定値）。
        /// InsertController が演出終了時の復帰先を「復元でなく再計算」するために使う（不変条件 3）。
        /// 画面が何を映していたか・保留がどうなっていたかとは無関係で、演出中の移動もここに反映済み。
        /// 未確定（起動直後など）なら false。
        /// </summary>
        public bool TryGetCurrentZoneCamera(out int camera)
        {
            camera = _progress.Current;
            return camera >= 0;
        }

        private void Update()
        {
            float now = Time.time;
            _logic.SetCueActive(overlay != null && overlay.Current != null);

            // (1) 時計を進める。画面が凍結中でも dip 中でも必ず評価する（不変条件 4）。
            //     確定しても通知はまだ出さない — 先に画面側の dip を起こす必要があるため（下記 (3)）。
            bool zoneCommitted = _progress.Tick(now, out int zoneCam);
            if (zoneCommitted) _logic.SetAmbient(zoneCam);

            // (2) 画面。dip 中は進めるだけ。
            if (_dip != DipState.Idle) AdvanceDip();
            else if (_logic.Tick(now, out int commit)) StartDip(commit, SwitchSource.Zone);

            // (3) 時計の通知は画面の dip を起こした**後**に出す。
            //     exit インサートはこの通知の同期連鎖で InsertExitRedirect を呼び、
            //     「いま始まった Zone dip の黒中にインサートカメラへ差し替える」予約を立てる。
            //     先に通知すると _insertActive が立って (2) の Tick が凍結され、dip 自体が始まらず
            //     予約が消化されないまま残る（＝画面がゾーンにもインサートにも切り替わらない）。
            if (zoneCommitted) ZoneCommitted?.Invoke(zoneCam);
        }

        private void StartDip(int target, SwitchSource source)
        {
            _dipTarget = target;
            _dipSource = source;
            _dip = DipState.Down;
            _dipTimer = 0f;
            audioCue?.Play(); // dip の黒 70ms が視覚差替に先行 → J カット相当
        }

        private void AdvanceDip()
        {
            // unscaledDeltaTime で進める（StartupFader と同流儀）。timeScale=0 で黒凍結するのを防ぐ。
            _dipTimer += Time.unscaledDeltaTime;
            if (_dip == DipState.Down)
            {
                float t = dipDownSec <= 0f ? 1f : Mathf.Clamp01(_dipTimer / dipDownSec);
                SetDim(t);
                if (t >= 1f)
                {
                    // 全黒でソース差替 → ActiveChanged → OnRegistryActiveChanged で
                    // NotifyExternalSwitch + SwitchCommitted(_dipSource) を発火。
                    // この同期連鎖内で exit インサートが InsertExitRedirect を呼び _blackRedirect を立てうる。
                    _commitSource = _dipSource;
                    registry?.SetActive(_dipTarget);
                    _commitSource = SwitchSource.External;

                    // exit インサート: Zone 切替先を見せずに、黒のまま insert カメラへ再差し替えする
                    // （dim は 1 のまま維持 → Up で insert カメラを見せる。中間カメラのフラッシュを出さない）。
                    if (_blackRedirect >= 0)
                    {
                        int rc = _blackRedirect;
                        _blackRedirect = -1;
                        _commitSource = SwitchSource.Insert;   // 周回に数えない
                        registry?.SetActive(rc);
                        _commitSource = SwitchSource.External;
                        _dipTarget = rc;
                    }

                    _dip = DipState.Up;
                    _dipTimer = 0f;
                }
            }
            else // Up
            {
                float t = dipUpSec <= 0f ? 1f : Mathf.Clamp01(_dipTimer / dipUpSec);
                SetDim(1f - t);
                if (t >= 1f)
                {
                    SetDim(0f);
                    _dip = DipState.Idle;
                }
            }
        }

        private void SetDim(float v) => _material?.SetFloat(SwitchDimId, Mathf.Clamp01(v));
    }
}
