#nullable enable
using System;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// カメラ切替の時間軸ガード（クールダウン / 最小滞在 / cue 中凍結 / 手動優先）の純ロジック。
    /// MonoBehaviour（<see cref="CameraSwitchDirector"/>）から分離して EditMode テスト可能にする。時刻は引数で受ける。
    ///
    /// ルール（計画 2026-07-19_viewer-ux.md）:
    ///   - switchCooldownSec: 切替直後の再切替をロック（Murch の最小ショット長）
    ///   - minDwellSec: ゾーン切替要求はペンディング → 滞在が続いたら適用（一瞬の通過で切らない）
    ///   - cue 再生中の自動切替は凍結（要求はペンディング、cue 終了後に最新を適用）
    ///   - 手動切替はクールダウンのみ尊重・dwell 無視。手動後 manualHoldSec は自動切替を抑止（手動優先）
    /// </summary>
    public sealed class SwitchDirectorLogic
    {
        private float _cooldownSec;
        private float _minDwellSec;
        private float _manualHoldSec;

        private int _current;
        private float _lastSwitchTime = float.NegativeInfinity;
        private float _lastManualTime = float.NegativeInfinity;
        private bool _cueActive;

        // ゾーン自動切替の単一保留（最新の目標だけを保持し、dwell/cue/manualHold の解除待ちで適用）。
        private bool _hasPendingZone;
        private int _pendingZone;
        private float _pendingSince;

        /// <summary>現在アクティブとして把握しているカメラ index。</summary>
        public int Current => _current;

        /// <summary>保留中の自動切替要求があるか（＝抑止中。HUD 表示用）。</summary>
        public bool HasPendingZone => _hasPendingZone;

        /// <summary>保留中の目標カメラ index（<see cref="HasPendingZone"/> が false のとき無効）。</summary>
        public int PendingZone => _pendingZone;

        /// <summary>cue 再生中フラグ（自動切替を凍結する）。</summary>
        public bool CueActive => _cueActive;

        public void Configure(float cooldownSec, float minDwellSec, float manualHoldSec)
        {
            _cooldownSec = Mathf.Max(0f, cooldownSec);
            _minDwellSec = Mathf.Max(0f, minDwellSec);
            _manualHoldSec = Mathf.Max(0f, manualHoldSec);
        }

        /// <summary>現在カメラを与えて初期化する。時間ガードは「直後の切替を許す」状態に戻す。</summary>
        public void Reset(int current)
        {
            _current = current;
            _lastSwitchTime = float.NegativeInfinity;
            _lastManualTime = float.NegativeInfinity;
            _hasPendingZone = false;
        }

        public void SetCueActive(bool active) => _cueActive = active;

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
        /// ゾーン自動切替要求。即 commit せず保留に積む（dwell/cue/manualHold は <see cref="Tick"/> で評価）。
        /// 現在カメラと同じ目標なら保留を解消する（一瞬の通過で行って戻る = 切らない）。
        /// </summary>
        public void RequestZone(int target, float now)
        {
            if (target == _current)
            {
                _hasPendingZone = false;
                return;
            }
            // 目標が変わったら dwell を測り直す。同一目標の継続は since を保つ（滞在が積み上がる）。
            if (!_hasPendingZone || _pendingZone != target)
            {
                _hasPendingZone = true;
                _pendingZone = target;
                _pendingSince = now;
            }
        }

        /// <summary>
        /// 毎フレーム評価。保留がクールダウン・cue 凍結・manualHold・dwell の全ゲートを通過したら commit する。
        /// </summary>
        public bool Tick(float now, out int commitTarget)
        {
            commitTarget = _current;
            if (!_hasPendingZone) return false;
            if (_pendingZone == _current) { _hasPendingZone = false; return false; }
            if (_cueActive) return false;                                   // cue 中は凍結（保留は保つ）
            if (now - _lastSwitchTime < _cooldownSec) return false;         // クールダウン
            if (now - _lastManualTime < _manualHoldSec) return false;       // 手動優先の抑止
            if (now - _pendingSince < _minDwellSec) return false;           // 最小滞在
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
        ///   - External: 上記いずれでもない registry への直接切替（後方互換の catch-all）
        /// </summary>
        public enum SwitchSource { Zone, Manual, Override, External }

        private static readonly int SwitchDimId = Shader.PropertyToID("_SwitchDim");

        [Header("References")]
        [Tooltip("切替対象の CameraStreamRegistry。null なら何もしない。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("cue 再生中判定に使う ScreenOverlayController。null なら同 GameObject から取得。")]
        [SerializeField] private ScreenOverlayController? overlay;

        [Tooltip("切替開始と同時に鳴らす音マスク。null なら無音。")]
        [SerializeField] private SwitchAudioCue? audioCue;

        [Header("Timing (現場調整可)")]
        [Tooltip("切替直後の再切替ロック秒（最小ショット長）。")]
        [SerializeField, Min(0f)] private float switchCooldownSec = 2f;

        [Tooltip("ゾーン自動切替の最小滞在秒（一瞬の通過で切らない）。")]
        [SerializeField, Min(0f)] private float minDwellSec = 2f;

        [Tooltip("手動切替後この秒数は自動切替を抑止する（手動優先）。")]
        [SerializeField, Min(0f)] private float manualHoldSec = 8f;

        [Header("Dip-to-black transition")]
        [Tooltip("黒へ落とす時間 (秒)。この終端でソースを差し替える。")]
        [SerializeField, Min(0f)] private float dipDownSec = 0.07f;

        [Tooltip("黒から立ち上げる時間 (秒)。")]
        [SerializeField, Min(0f)] private float dipUpSec = 0.10f;

        private readonly SwitchDirectorLogic _logic = new();
        private Material? _material;
        private bool _subscribed;

        private enum DipState { Idle, Down, Up }
        private DipState _dip = DipState.Idle;
        private float _dipTimer;
        private int _dipTarget;
        private SwitchSource _dipSource = SwitchSource.External;

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

        /// <summary>保留中の自動切替があり抑止されているか（HUD 表示用）。</summary>
        public bool SwitchSuppressed => _logic.HasPendingZone;

        /// <summary>dip-to-black 演出の実行中か（HUD 表示用）。</summary>
        public bool Dipping => _dip != DipState.Idle;

        private void Awake()
        {
            if (registry == null) registry = GetComponentInParent<CameraStreamRegistry>();
            if (registry == null) registry = FindObjectOfType<CameraStreamRegistry>();
            if (overlay == null) overlay = GetComponent<ScreenOverlayController>();
            if (audioCue == null) audioCue = GetComponent<SwitchAudioCue>();
            var r = GetComponent<Renderer>();
            _material = r != null ? r.material : null;
            _logic.Configure(switchCooldownSec, minDwellSec, manualHoldSec);
        }

        private void OnEnable()
        {
            if (registry != null && !_subscribed)
            {
                _logic.Reset(registry.ActiveIndex);
                registry.ActiveChanged += OnRegistryActiveChanged;
                _subscribed = true;
            }
            SetDim(0f);
        }

        private void OnDisable()
        {
            if (registry != null && _subscribed) registry.ActiveChanged -= OnRegistryActiveChanged;
            _subscribed = false;
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

        /// <summary>巡回 Next（手動）。</summary>
        public void Next()
        {
            if (registry == null || registry.Count == 0) return;
            RequestManual(CameraStreamRegistry.WrapIndex(registry.ActiveIndex + 1, registry.Count));
        }

        /// <summary>巡回 Prev（手動）。</summary>
        public void Prev()
        {
            if (registry == null || registry.Count == 0) return;
            RequestManual(CameraStreamRegistry.WrapIndex(registry.ActiveIndex - 1, registry.Count));
        }

        /// <summary>絶対指定の手動切替（キーボード数字キー等）。</summary>
        public void RequestManual(int target)
        {
            if (registry == null) return;
            _logic.Configure(switchCooldownSec, minDwellSec, manualHoldSec);
            if (_dip != DipState.Idle) return; // dip 中は新規切替を始めない（cooldown でも弾かれる）
            if (_logic.RequestManual(target, Time.time, out int commit)) StartDip(commit, SwitchSource.Manual);
        }

        /// <summary>ゾーン自動切替の要求（PlayerZoneTracker から）。dwell/cue/manualHold ガード後に適用。</summary>
        public void RequestZone(int target)
        {
            _logic.Configure(switchCooldownSec, minDwellSec, manualHoldSec);
            _logic.RequestZone(target, Time.time);
        }

        private void Update()
        {
            _logic.SetCueActive(overlay != null && overlay.Current != null);

            if (_dip != DipState.Idle) { AdvanceDip(); return; }

            if (_logic.Tick(Time.time, out int commit)) StartDip(commit, SwitchSource.Zone);
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
            _dipTimer += Time.deltaTime;
            if (_dip == DipState.Down)
            {
                float t = dipDownSec <= 0f ? 1f : Mathf.Clamp01(_dipTimer / dipDownSec);
                SetDim(t);
                if (t >= 1f)
                {
                    // 全黒でソース差替 → ActiveChanged → OnRegistryActiveChanged で
                    // NotifyExternalSwitch + SwitchCommitted(_dipSource) を発火。
                    _commitSource = _dipSource;
                    registry?.SetActive(_dipTarget);
                    _commitSource = SwitchSource.External;
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
