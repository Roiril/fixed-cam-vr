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
            // ゾーン自動切替が始まらない事故を防ぐ（TakeRunner 側の後片付けと二重の保険）。
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
        ///   - Insert: 演出が画面を占有する差し込み・復帰（TakeRunner 経由）
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

        [Tooltip("遷移 glitch と切替への乱れ重畳を掛ける GlitchFx。null なら同 GameObject から取得。")]
        [SerializeField] private GlitchFx? glitchFx;

        [Tooltip("画のホールド / 焼き付きを出す CameraFeelFx。null なら同 GameObject から取得。")]
        [SerializeField] private CameraFeelFx? feelFx;

        [Tooltip("入れ替わりのノイズ（transition:\"swap\"）。null なら乱れ遷移へ倒す。")]
        [SerializeField] private SwapMorphFx? swapFx;

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

        // 進行中の dip の実尺（StartDip の引数で確定。負値ならインスペクタ既定）。
        private float _curDipDown;
        private float _curDipUp;

        // 進行中の遷移を「黒」ではなく「映像の乱れ」で見せるか（transition:"glitch"）。
        // 状態機械（Down → 差し替え → Up）は黒と完全に同じで、見た目だけが変わる。
        private bool _curDipGlitch;

        // この遷移の乱れを「起きた回数」に数えるか（既定 true）。
        // false にするのは**体験者の操作で起きる乱れ**だけ — 数えると乱れの育ち方が
        // 押した回数の関数になり、著作した曲線が人によって別物になる（GlitchFx.SetSustain 参照）。
        private bool _curDipCountGlitch = true;

        // ゾーン切替そのものへ重ねる乱れの強さ（show.json control.switchGlitch）。0 = 重ねない。
        private float _switchGlitch;

        // 黒の瞬間に 1 回だけ呼ぶ処理（素材カットの差し替え）。TakeHoldBegin が積み、AdvanceDip が消費する。
        private Action? _blackAction;

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
        /// 段 B の主眼で、これにより旧 InsertController の「復帰を Zone に偽装する」ハックが不要になった。
        /// </summary>
        public event Action<int>? ZoneCommitted;

        /// <summary>ゾーン由来の切替が保留・抑止されているか（HUD 表示用）。dwell 待ちと画面凍結の両方を含む。</summary>
        public bool SwitchSuppressed => _progress.HasPending || _logic.HasPendingZone;

        /// <summary>dip-to-black 演出の実行中か（HUD 表示用）。</summary>
        public bool Dipping => _dip != DipState.Idle;

        /// <summary>
        /// 現在の dip / 乱れ遷移の全尺に対する進み 0..1。Down と Up を実尺で連結する。
        /// 遷移外は 1。
        /// </summary>
        public float TransitionProgress01
        {
            get
            {
                if (_dip == DipState.Idle) return 1f;
                float total = _curDipDown + _curDipUp;
                if (total <= 0f) return 1f;
                float elapsed = _dip == DipState.Down
                    ? Mathf.Clamp(_dipTimer, 0f, _curDipDown)
                    : _curDipDown + Mathf.Clamp(_dipTimer, 0f, _curDipUp);
                return Mathf.Clamp01(elapsed / total);
            }
        }

        /// <summary>インサート表示中か（手動切替は拒否される。拒否時の赤メッセージ判定に使う）。</summary>
        public bool InsertActive => _logic.InsertActive;

        /// <summary>
        /// 受信カメラの本数（registry 未配線なら 0）。演出のカットが指すカメラ index が
        /// 実在するかを TakeRunner が事前判定するために読む（範囲外を registry の clamp 任せにすると
        /// 「無言で別カメラに切り替わる」= 現場で原因が読めない症状になる）。
        /// </summary>
        public int CameraCount => registry != null ? registry.Count : 0;

        /// <summary>
        /// <b>いま画面に出しているカメラ</b> index（registry の実値。-1 = 未確定）。
        /// ⚠ <c>SwitchDirectorLogic.Current</c>（この Director が把握している値）とは別物 —
        /// 卓の cameraOverride は registry を直接叩くので、実際に映っているのはこちら。
        /// </summary>
        public int ActiveCameraIndex => registry != null ? registry.ActiveIndex : -1;

        /// <summary>
        /// この Director が切り替える registry（未配線なら null）。
        /// 「自分と同じ registry を回している Director か」を呼び出し側が確かめるために公開する
        /// （<see cref="ShowControlClient"/> の遅延解決が別リグの Director を掴むのを防ぐ）。
        /// </summary>
        public CameraStreamRegistry? Registry => registry;

        private void Awake()
        {
            if (registry == null) registry = GetComponentInParent<CameraStreamRegistry>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (audioCue == null) audioCue = GetComponent<SwitchAudioCue>();
            if (glitchFx == null) glitchFx = GetComponent<GlitchFx>();
            if (feelFx == null) feelFx = GetComponent<CameraFeelFx>();
            if (feelFx == null) feelFx = FindObjectOfType<CameraFeelFx>();
            if (swapFx == null) swapFx = GetComponent<SwapMorphFx>();
            if (swapFx == null) swapFx = FindObjectOfType<SwapMorphFx>();
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
            ClearTransitionVisual();
        }

        private void OnDisable()
        {
            if (registry != null && _subscribed) registry.ActiveChanged -= OnRegistryActiveChanged;
            _subscribed = false;
            // dip 中に無効化されると _SwitchDim（や乱れの持続成分）が残る。解除して状態も畳んでおく。
            ClearTransitionVisual();
            _dip = DipState.Idle;
            _dipTimer = 0f;
            _curDipGlitch = false;
            _curDipCountGlitch = true;
            _blackAction = null;
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

        /// <summary>
        /// 「このカメラはスタッフの巡回・ゾーン自動切替に出してよいか」を判定する述語
        /// （<c>role:"fx"</c> の演出専用カメラ = カメラ D を除くために ShowControlClient が注入する）。
        /// 未注入なら全カメラが対象（従来どおり）。
        /// </summary>
        public void SetCameraSelectable(Func<int, bool>? predicate) => _selectable = predicate;

        private Func<int, bool>? _selectable;

        private bool Selectable(int index) => _selectable == null || _selectable(index);

        /// <summary>巡回 Next（手動）。受理したら true（インサート表示中・dip 中・クールダウン中は false）。</summary>
        public bool Next()
        {
            if (registry == null || registry.Count == 0) return false;
            return RequestManual(NextSelectable(+1));
        }

        // 演出専用カメラ（role:"fx"）を飛ばして次の巡回先を返す。全部 fx なら現在地を返す（切替しない）。
        private int NextSelectable(int dir)
        {
            int n = registry!.Count;
            int idx = registry.ActiveIndex;
            for (int step = 1; step <= n; step++)
            {
                int cand = CameraStreamRegistry.WrapIndex(idx + dir * step, n);
                if (Selectable(cand)) return cand;
            }
            return idx;
        }

        /// <summary>巡回 Prev（手動）。受理したら true（インサート表示中・dip 中・クールダウン中は false）。</summary>
        public bool Prev()
        {
            if (registry == null || registry.Count == 0) return false;
            return RequestManual(NextSelectable(-1));
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

        // ---- 画面の占有（TakeRunner が使う差し込み・復帰）----

        /// <summary>
        /// exit インサート: 区間離脱の Zone dip が黒へ落ちていく最中に呼ばれ、Up で切替先を見せる代わりに
        /// 黒のまま insert カメラへ差し替える予約を立てる。実際の registry 切替は AdvanceDip が黒の時点で行う
        /// （この場での registry.SetActive 再入を避ける）。ゾーン自動切替を凍結する。
        ///
        /// **予約を立てるのは dip が Down（まだ黒に達していない）のときだけ**。それ以外（Idle / Up）で予約すると
        /// 消化されるべき dip が存在せず、次に起きる無関係な dip（インサート復帰など）に持ち越されて
        /// 「戻るはずが insert カメラへ飛ぶ」事故になる。その場合は通常の dip で素直に切り替える。
        /// </summary>
        public void InsertExitRedirect(int insertCamera, float downSec = -1f, float upSec = -1f, bool glitch = false)
        {
            _logic.SetInsertActive(true);
            if (HandoffDip(insertCamera, null, downSec, upSec, glitch)) return;
            StartDip(insertCamera, SwitchSource.Insert, downSec, upSec, glitch);
        }

        /// <summary>
        /// 演出（Take）が**カメラを変えずに**画面を占有する（全面差し替えの映像カット等）。
        /// ゾーン自動切替を凍結し、遷移が指定されていれば dip を掛けて**黒の瞬間に
        /// <paramref name="onBlack"/> を 1 回だけ呼ぶ**（素材の差し替えを黒中で行うため）。
        /// 遷移なし（cut）なら即座に <paramref name="onBlack"/> を呼ぶ。解除は <see cref="InsertReturn"/>。
        /// </summary>
        public void TakeHoldBegin(float downSec = 0f, float upSec = 0f, Action? onBlack = null, bool glitch = false)
        {
            _logic.SetInsertActive(true);
            if (HandoffDip(null, onBlack, downSec, upSec, glitch)) return;
            if (downSec <= 0f && upSec <= 0f)
            {
                onBlack?.Invoke();   // カット（瞬時）: 進行中の遷移が無いなら本当に瞬時
                return;
            }
            SetBlackAction(onBlack);
            // カメラは変えない。registry.SetActive(同じ index) は早期 return するので切替イベントも出ない。
            StartDip(_logic.Current, SwitchSource.Insert, downSec, upSec, glitch);
        }

        /// <summary>
        /// <b>入れ替わりのノイズで画面を占有する</b>（<c>transition:"swap"</c>・`canon/LEDGER.md` 0089）。
        ///
        /// dip 状態機械は使わない。黒も全画面の砂嵐も出さず、<b>映像の中の体験者（＝人形の
        /// シルエット）だけ</b>を砂で覆い、覆い切った 1 フレームで画面を差し替える。
        /// 差し替えの中身（素材・カメラ・左右分割・第 2 層）は <paramref name="onCovered"/> が持つ。
        ///
        /// ⚠ <b>失敗したら false を返す</b>（人形が居ない / カメラ姿勢が未著作 / 位置合わせが未完了）。
        ///   呼び出し側は乱れ遷移へ倒すこと — 黙って何も起きないと、体験者は画面が固まったと感じる。
        /// </summary>
        /// <param name="targetCamera">覆い切った瞬間に切り替えるカメラ（-1 = 変えない）。</param>
        /// <param name="minX">覆いを効かせる左端（枠 UV・0 = 制限しない。カットの <c>swapMinX</c>）。</param>
        public bool TakeSwapBegin(int targetCamera, float totalSec, SwapMorphLogic.Dir dir,
                                  Action? onCovered, float minX = 0f)
        {
            if (swapFx == null) return false;
            // 覆いの相手（映像の中の人）が写っているのは**いま画面に出ているカメラ**
            // （swap はカメラを動かさずに始まり、差し替えは覆い切った縁）。
            // その無人プレートを差分マスクの相手として束縛する。
            int plateCamera = registry != null ? registry.ActiveIndex : -1;
            bool ok = swapFx.Begin(dir, totalSec, plateCamera, () =>
            {
                if (targetCamera >= 0 && registry != null && registry.ActiveIndex != targetCamera)
                {
                    // 周回に数えない（インサートと同じ扱い）。
                    _commitSource = SwitchSource.Insert;
                    registry.SetActive(targetCamera);
                    _commitSource = SwitchSource.External;
                    audioCue?.Play();
                }
                onCovered?.Invoke();
            }, minX);
            if (ok) _logic.SetInsertActive(true);
            return ok;
        }

        /// <summary>
        /// <b>覆いを包み切ったまま保持する</b>（`canon/LEDGER.md` 0102・カットの <c>swapHold</c>）。
        /// 段は進まず、次のカットの <c>transition:"swap"</c> が縮む段から引き継ぐ。
        ///
        /// ⚠ <b>画面には触らない。</b> 覆いを立てるだけなので、映像・カメラ・左右分割はカットの
        /// ふつうの経路（<see cref="TakeHoldBegin"/> / <see cref="ApplySplit"/>）が持つ。
        /// </summary>
        /// <param name="cgCamera">人の代役を立てるときの構図のカメラ index。</param>
        /// <param name="minX">覆いを効かせる左端（枠 UV・0 = 制限しない）。</param>
        /// <returns>覆いを立てられたか。false でも体験は成立する（覆いが出ないだけ）。</returns>
        public bool TakeVeilHoldBegin(int cgCamera, float minX)
        {
            if (swapFx == null) return false;
            // ⚠⚠ 無人プレートは**このカットが映すカメラ**で選ぶ（2026-08-23）。
            //   ActiveIndex（いま画面に出ているカメラ）で選んでいたが、区間へ入った最初の
            //   フレームでは**まだ前の区間のカメラが出ている**（画面の切替は数十 ms 遅れる）。
            //   実測: 3 周目 A の覆いが立った 50ms 後にカメラ A へ切り替わっており、
            //   覆いは plate_C（別の部屋）を掴んでいた。別の部屋どうしを比べるので差分が
            //   箱いっぱいで飽和し、**右半分が真っ黒**になる。
            //   ⚠ ログの `mask=1` は「掴めたか」しか言わないので、間違ったプレートでも 1 が出る。
            int plateCamera = cgCamera >= 0
                ? cgCamera
                : (registry != null ? registry.ActiveIndex : -1);
            return swapFx.BeginHold(plateCamera, plateCamera, minX);
        }

        /// <summary>入れ替わりのノイズを途中で畳む（演出の中止・ランリセット・体験の終了）。</summary>
        public void CancelSwap() => swapFx?.Cancel();

        /// <summary>入れ替わりのノイズが走っているか。</summary>
        public bool SwapActive => swapFx != null && swapFx.Active;

        /// <summary>覆いを包み切ったまま保持しているか（<see cref="TakeVeilHoldBegin"/> 中）。</summary>
        public bool SwapHolding => swapFx != null && swapFx.Holding;

        /// <summary>
        /// カメラ切替の音を 1 発鳴らす（カットの <c>switchSfx</c>）。音源が無ければ無音。
        ///
        /// ⚠⚠ <b>ここは警告音つきで鳴る</b>（<c>canon/LEDGER.md</c> 0106）。この経路を通るのは
        /// <b>カメラを動かさない素材カット</b>＝ 実質「人形視点の差し込み」だけで、
        /// ゾーン切替・インサート・Web 固定は <see cref="SwitchAudioCue.Play"/> のまま。
        /// 素の音で鳴らしたい素材カットが出てきたら、<b>ここで分けずにカット側へ旗を足す</b>
        /// （呼び分けを増やすと「どの経路が警告つきか」が読めなくなる）。
        /// </summary>
        public void PlaySwitchSfx() => audioCue?.PlayAlert();

        /// <summary>
        /// enter インサート: 現在の映像から insert カメラへ dip-to-black で切り替える（Insert source ＝周回に数えない）。
        /// ゾーン自動切替を凍結する。
        ///
        /// **進行中の Zone dip が Down 相なら、そこへ相乗りする**（<see cref="InsertExitRedirect"/> と同じ機構）。
        /// 相乗りしないと、区間進入と同時に始まる演出が、いま始まったばかりのゾーン切替の暗転を打ち切って
        /// 自分の dip をやり直す＝**暗転が 2 回続けて出る**。企画書 2.3 の「差し替えを提示映像の切替の
        /// タイミングに同期させ、差し替えの知覚的検出を抑える」は、この相乗りが無いと成立しない。
        /// </summary>
        public void InsertBegin(int insertCamera, float downSec = -1f, float upSec = -1f, bool glitch = false)
        {
            _logic.SetInsertActive(true);
            if (HandoffDip(insertCamera, null, downSec, upSec, glitch)) return;
            StartDip(insertCamera, SwitchSource.Insert, downSec, upSec, glitch);
        }

        /// <summary>
        /// 進行中の遷移へ**割り込む**。進行中が無ければ false（呼び出し側が普通に <see cref="StartDip"/> する）。
        ///
        /// 規律は 1 行:「継ぎ目の遷移は、いま画面を取る側が所有する」。
        /// 体験者にとっての単位は『ゾーン切替』でも『演出の入り』でもなく**継ぎ目**で、
        /// 継ぎ目は 1 回だけ起き、その見え方は演出の作者が決めるのが正しい。
        ///
        /// 旧実装（相乗り）は前半（1 回にする）だけを満たし、後半（作者が決める）を捨てていた。
        /// しかも捨てるかどうかが「進行中が Down 相か Up 相か」＝実行時のフレームタイミングで決まるので、
        /// **同じ show.json が実行のたびに違う絵になる**（再現しないものは著作できない）。
        ///
        ///   Down（まだ暗くなっている途中）… 暗さを比率で保ったまま、残りの尺と見た目を割り込む側へ差し替える
        ///   Up  （明るくなっている途中）  … いまの暗さから Down へ**折り返す**。
        ///                                   旧実装は 0 から張り直していたので「黒 → 明るくなりかけ → また黒」に
        ///                                   なっていた（隠すべき継ぎ目を逆に目立たせる）。
        ///   瞬時（0/0）                  … 進行中の尺をそのまま使い、差し替え先だけ変える。
        ///                                   作者の「瞬時」は「自分から暗転を足さない」意図であって
        ///                                   「既にある暗転を打ち切る」意図ではない（打ち切ると明るさが飛ぶ）。
        /// </summary>
        private bool HandoffDip(int? redirect, Action? onBlack, float downSec, float upSec, bool glitch)
        {
            if (_dip == DipState.Idle) return false;

            if (redirect.HasValue) _blackRedirect = redirect.Value;
            if (onBlack != null) SetBlackAction(onBlack);

            if (downSec <= 0f && upSec <= 0f) return true;   // 瞬時: 進行中の遷移に乗る

            // いまの暗さ（0=明るい / 1=真っ黒）を保って、新しい尺・見た目へ乗り換える。
            float level = _dip == DipState.Down
                ? (_curDipDown <= 0f ? 1f : Mathf.Clamp01(_dipTimer / _curDipDown))
                : 1f - (_curDipUp <= 0f ? 1f : Mathf.Clamp01(_dipTimer / _curDipUp));
            _curDipDown = downSec >= 0f ? downSec : dipDownSec;
            _curDipUp = upSec >= 0f ? upSec : dipUpSec;
            _curDipGlitch = glitch;
            _dip = DipState.Down;
            _dipTimer = level * _curDipDown;
            return true;
        }

        /// <summary>
        /// 黒の瞬間に 1 回だけ呼ぶ処理を予約する。**未消化のものを上書きするときは報告する** —
        /// 上書きされたカットは一度も画面に出ないまま消えるので、演出層の
        /// 「捨てたら必ず報告する」（<see cref="TakeRunnerLogic.TakeDropped"/>）と同じ規律を遷移層にも通す。
        /// </summary>
        private void SetBlackAction(Action? onBlack)
        {
            if (_blackAction != null && onBlack != null) TransitionPreempted?.Invoke();
            _blackAction = onBlack;
        }

        /// <summary>
        /// 黒の瞬間の予約が、消化される前に次の予約で上書きされた（＝そのカットが画面に出なかった）。
        /// <see cref="TakeRunner"/> が警告ログへ流す。
        /// </summary>
        public event Action? TransitionPreempted;

        /// <summary>
        /// インサート表示を終え、復帰カメラ（最新ゾーン）へ dip-to-black で戻す。
        /// ゾーン凍結を解除する（dip 完了後にゾーン自動切替が再開する）。
        ///
        /// カメラを動かさずに凍結だけ解きたい場合は <see cref="TakeHoldEnd"/>。
        ///
        /// 旧実装にあった <c>asZone</c>（復帰を Zone source に偽装して周回へ数えさせる）は**廃止**。
        /// 周回は画面ではなく <see cref="ZoneCommitted"/>（時計）が駆動するようになり、インサート中の
        /// 実ゾーン移動はその時点で既に周回へ反映済みだから（段 B）。
        /// </summary>
        public void InsertReturn(int returnCamera, float downSec = -1f, float upSec = -1f, bool glitch = false,
                                 bool countEscalation = true)
        {
            _blackAction = null;   // 未消化の素材差し替えを次の dip へ持ち越さない
            // ⚠ 差し替え先の予約も一緒に捨てる。捨てないと、離脱時インサートが積んだ
            //    「黒の瞬間にこのカメラへ飛べ」が生き残り、**戻ったはずが insert のカメラへ飛ぶ**
            //    （_blackAction 側だけ塞がれていて、こちらが漏れていた）。
            _blackRedirect = -1;
            _logic.SetInsertActive(false);
            StartDip(returnCamera, SwitchSource.Insert, downSec, upSec, glitch, countEscalation);
        }

        /// <summary>
        /// 演出の**画面占有だけ**を解く（dip も切替もしない）。ライブ卓が既に画面を取っている状況で
        /// 演出を畳むときに使う（ここで <see cref="InsertReturn"/> を掛けると卓の cameraOverride を外してしまう）。
        /// </summary>
        public void TakeHoldEnd()
        {
            _blackAction = null;
            _blackRedirect = -1;   // InsertReturn と同じ理由（未消化の差し替え先を残さない）
            _logic.SetInsertActive(false);
        }

        /// <summary>Web cameraOverride による凍結中か（演出を畳むときに「カメラを返してよいか」の判定に使う）。</summary>
        public bool OverrideActive => _logic.OverrideActive;

        /// <summary>
        /// **いま体験者が居るゾーン**のカメラ index を返す（時計 <see cref="ZoneProgressionLogic"/> の確定値）。
        /// TakeRunner が演出終了時の復帰先を「復元でなく再計算」するために使う（不変条件 3）。
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

        /// <summary>
        /// dip を開始する。<paramref name="downSec"/> / <paramref name="upSec"/> が負なら
        /// インスペクタ既定（<see cref="dipDownSec"/> / <see cref="dipUpSec"/>）を使う。
        ///
        /// **尺は必ず引数で渡す**（旧 <c>SetNextTransition</c> の「次の切替に効く予約」方式は廃止）。
        /// 予約方式は「消化されなかった予約が無関係な次の切替に漏れる」事故を構造的に許していた。
        /// </summary>
        private void StartDip(int target, SwitchSource source, float downSec = -1f, float upSec = -1f,
                              bool glitch = false, bool countEscalation = true)
        {
            _curDipDown = downSec >= 0f ? downSec : dipDownSec;
            _curDipUp = upSec >= 0f ? upSec : dipUpSec;
            _curDipGlitch = glitch;
            _curDipCountGlitch = countEscalation;

            // ⚠⚠ **リレーは「装置が入力を切り替えた」音。切り替わっていないなら鳴らさない。**
            //
            // dip を打つ経路は 3 つあり、そのうち `TakeHoldBegin` は **同じカメラのまま**
            // （録画・無人プレート・素材を画面へ載せるだけ）で黒を通す。`InsertReturn` も
            // 戻り先が今のカメラなら同じ。ここで鳴らすと「切り替わっていないのにリレーが鳴る」ので、
            // **リレーが切替の合図であること自体が壊れる**。
            //
            // 2026-08-12 の実機実測: 画面の切替 9 回に対しリレーが **18 回**鳴っていた。
            // 切替音のクリップはこの日まで空だったので、この重複は表に出ていなかった
            // （**音を入れて初めて見えた欠陥**）。判定は `analyze-xp-log.py` の
            // 「カメラ切替の音 N 回（画面の切替 M 回）」で、N が M を大きく超えたら疑う。
            bool cameraChanges = registry == null || registry.ActiveIndex != target;

            // 「瞬時（cut）」は dip 状態機械に入れずその場で差し替える。
            // 旧実装は Down→黒→Up を必ず 1 フレームずつ通したため、尺 0 でも 1 フレーム真っ黒が出た。
            if (_curDipDown <= 0f && _curDipUp <= 0f)
            {
                _commitSource = source;
                registry?.SetActive(target);
                _commitSource = SwitchSource.External;
                Action? act = _blackAction;
                _blackAction = null;
                act?.Invoke();
                _curDipGlitch = false;
                ClearTransitionVisual();
                _dip = DipState.Idle;
                if (cameraChanges) audioCue?.Play();
                if (source == SwitchSource.Zone) PulseSwitchGlitch(0f);
                return;
            }

            _dipTarget = target;
            _dipSource = source;
            _dip = DipState.Down;
            _dipTimer = 0f;
            // dip の黒が視覚差替に先行 → J カット相当
            if (cameraChanges) audioCue?.Play();
            // ゾーン切替そのものへ乱れを重ねる（企画書 2.3「提示映像の切替と同様に…乱れを一時的に重畳」）。
            // 遷移 glitch とは独立で、黒の dip に乗せる形で使う。
            if (source == SwitchSource.Zone) PulseSwitchGlitch(_curDipDown + _curDipUp);
        }

        private void PulseSwitchGlitch(float sec)
        {
            if (_switchGlitch <= 0.001f || glitchFx == null) return;
            glitchFx.Pulse(_switchGlitch, sec > 0f ? sec : 0.12f);
        }

        /// <summary>
        /// ゾーン切替に重ねる乱れの強さを設定する（show.json <c>control.switchGlitch</c>・0 で無効）。
        /// <see cref="ShowControlClient"/> が焼き込み / 端末キャッシュ / ライブのいずれからも流す。
        /// </summary>
        public void SetSwitchGlitch(float level) => _switchGlitch = Mathf.Clamp01(level);

        /// <summary>単発の乱れを外から走らせる（カット頭のアクセント・卓からの手動発火）。</summary>
        public void PulseGlitch(float level, float sec) => glitchFx?.Pulse(level, sec);

        /// <summary>画を止める（カットの <c>hold</c>）。ライブも差し替え素材も一緒に凍る。</summary>
        public void HoldFrame(float sec) => feelFx?.Hold(sec);

        /// <summary>焼き付き（カットの <c>burn</c>）。いまの画が薄く残り、動いたものの跡だけが見える。</summary>
        public void BurnFrame(float amount, float sec) => feelFx?.Burn(amount, sec > 0f ? sec : 2.5f);

        /// <summary>
        /// 左右分割（カットの <c>splitX</c> / <c>splitFlip</c> / <c>splitFreeze</c>）。
        /// 凍らせるときは**その瞬間の 1 枚**を捕まえてから量を上げる（順序が逆だと 1 フレーム前の画で止まる）。
        /// </summary>
        public void ApplySplit(float splitX, bool flip, bool freeze)
        {
            if (feelFx == null) return;
            if (freeze) feelFx.CaptureFreezeFrame();
            feelFx.SetSplit(splitX, flip, freeze ? 1f : 0f);
        }

        /// <summary>凍結・焼き付きをすべて畳む（ラン開始・演出の中止・体験の終了）。</summary>
        public void ClearFeelFx()
        {
            // ⚠ 入れ替わりのノイズもここで畳む。**「画が止まったまま戻らない」の型**（この codebase が
            //   4 回踏んだ）と同じで、砂の人型が掴んだ人形を離さないまま次の体験者へ持ち越されうる。
            swapFx?.Cancel();
            feelFx?.ResetAll();
            // ⚠ 分割も必ず畳む。残すと**画が割れたまま・左半分が凍ったまま**次の体験者へ持ち越される。
            feelFx?.ClearSplit();
        }

        /// <summary>遷移の見た目（黒 or 乱れ）を 0 に戻す。</summary>
        private void ClearTransitionVisual()
        {
            SetDim(0f);
            glitchFx?.SetSustain(0f);
        }

        /// <summary>遷移の進み具合 (0-1) を、黒 or 乱れのどちらかへ流す。</summary>
        private void ApplyTransitionLevel(float t)
        {
            if (_curDipGlitch)
            {
                SetDim(0f);
                glitchFx?.SetSustain(t * TakeSchema.GlitchTransitionLevel, _curDipCountGlitch);
            }
            else
            {
                SetDim(t);
            }
        }

        // dip を進める dt の供給元。既定は Time.unscaledDeltaTime。
        // **EditMode テスト用の seam**（TakeRunner.SetTimeSource と同じ流儀）: EditMode では
        // unscaledDeltaTime が「エディタの実フレーム間隔」（フォーカス中 7〜16ms / 非フォーカス数百 ms）に
        // なるため、Update を数回叩くだけの配線テストが run ごとに通る / 落ちるを繰り返す
        // （2026-07-27 に TakeWiringTests / SwitchWiringTests で実際に発生）。
        private Func<float>? _deltaSource;

        /// <summary>dip 進行の dt を差し替える（EditMode テスト用。null で <c>Time.unscaledDeltaTime</c> に戻る）。</summary>
        public void SetDeltaSource(Func<float>? source) => _deltaSource = source;

        private float DipDeltaTime => _deltaSource != null ? _deltaSource() : Time.unscaledDeltaTime;

        private void AdvanceDip()
        {
            // unscaledDeltaTime で進める（StartupFader と同流儀）。timeScale=0 で黒凍結するのを防ぐ。
            _dipTimer += DipDeltaTime;
            if (_dip == DipState.Down)
            {
                float t = _curDipDown <= 0f ? 1f : Mathf.Clamp01(_dipTimer / _curDipDown);
                ApplyTransitionLevel(t);
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

                    // 素材カットの差し替えは黒の瞬間に行う（Up で新しい絵が立ち上がる）。
                    if (_blackAction != null)
                    {
                        Action act = _blackAction;
                        _blackAction = null;
                        act();
                    }

                    _dip = DipState.Up;
                    _dipTimer = 0f;
                }
            }
            else // Up
            {
                float t = _curDipUp <= 0f ? 1f : Mathf.Clamp01(_dipTimer / _curDipUp);
                ApplyTransitionLevel(1f - t);
                if (t >= 1f)
                {
                    ClearTransitionVisual();
                    _curDipGlitch = false;
                    _dip = DipState.Idle;
                }
            }
        }

        private void SetDim(float v) => _material?.SetFloat(SwitchDimId, Mathf.Clamp01(v));
    }
}
