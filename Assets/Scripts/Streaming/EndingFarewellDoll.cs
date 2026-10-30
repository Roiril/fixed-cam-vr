#nullable enable
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 帰還の別れの人形の実行体（<c>canon/LEDGER.md</c> 0291）。段の判定は <see cref="EndingFarewellLogic"/>、
    /// 描くのは <see cref="ShowCgLayer"/> の別れのモード。
    ///
    /// 締めのライブのカットで、入れ替わり（人形 → 人）が終わって CG 層が空いた縁に人形を 1 体出す。
    /// シーンには焼かない。<see cref="TakeRunner"/> が起動時に載せる（<see cref="EndingFrameCapture"/> と同じ）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class EndingFarewellDoll : MonoBehaviour
    {
        private readonly EndingFarewellLogic _logic = new EndingFarewellLogic();
        private TakeRunner? _runner;
        private ShowCgLayer? _cg;
        private bool _beginAccepted;
        private EndingFarewellLogic.Phase _lastPhase = EndingFarewellLogic.Phase.Idle;

        /// <summary>いまの段（テレメトリ・写真の窓が読む）。</summary>
        public EndingFarewellLogic.Phase Phase => _logic.Current;

        /// <summary>写真を撮ってよい（体験者の方を向いて不透明に立っている）。</summary>
        public bool Facing => _logic.Current == EndingFarewellLogic.Phase.Face && _cg != null && _cg.FarewellPhotoReady;

        public bool Completed => _logic.Current == EndingFarewellLogic.Phase.Done;
        public bool ShotCaptured { get; private set; }

        public void NotifyShotCaptured()
        {
            if (!Facing) return;
            ShotCaptured = true;
            _logic.NotifyShotCaptured();
        }

        /// <summary>帰還写真には所定位置で向き合った人と人形が必要（0293）。</summary>
        public bool AllowsPlainReleasedShot => false;

        /// <summary>この体験者で見つめる段まで届いた回数（テレメトリの集計用・ラン開始で 0）。</summary>
        public int FacedCount { get; private set; }

        /// <summary>消え切るまで届いた回数（同上）。</summary>
        public int CompletedCount { get; private set; }

        /// <summary>段が変わるたびに上がる（段, 人形の足元 uv, 体験者の足元 uv）。観測専用。</summary>
        public event System.Action<EndingFarewellLogic.Phase, Vector2, Vector2>? PhaseChanged;

        public void Bind(TakeRunner runner, ShowCgLayer? cg)
        {
            _runner = runner;
            _cg = cg;
        }

        /// <summary>体験者の交代。出ていれば畳む。</summary>
        public void ResetRun()
        {
            if (_cg != null && _cg.FarewellActive) _cg.Hide();
            _logic.Reset();
            _beginAccepted = false;
            ShotCaptured = false;
            FacedCount = 0;
            CompletedCount = 0;
            _lastPhase = EndingFarewellLogic.Phase.Idle;
        }

        private void LateUpdate()
        {
            if (_runner == null) return;
            bool endingActive = _runner.EndingTakeActive;
            bool ready = _runner.EndingReleasedLiveReady;
            bool cgFree = _cg != null && !_cg.IsVisible && !_cg.FarewellActive;
            bool anchored = _cg != null && _cg.FarewellAnchored;
            bool showing = _cg != null && _cg.FarewellActive;

            EndingFarewellLogic.Step step = _logic.Tick(Time.deltaTime, endingActive, ready, cgFree,
                                                        _beginAccepted, anchored, showing);
            if (step.begin)
            {
                string actor = _runner.EndingDollActorId;
                _beginAccepted = _cg != null && _cg.BeginFarewell(actor, _runner.ActiveStepCameraIndex);
                if (!_beginAccepted)
                    Debug.LogWarning($"[EndingFarewellDoll] 別れの人形 '{actor}' を出せない" +
                                     "（姿勢・較正・位置合わせ・actors[] のどれかが無い）→ 写真を撮らず再試行を待つ");
            }
            if (_cg != null && _cg.FarewellActive) _cg.SetFarewellSample(step.sample);
            if (step.hide && _cg != null && _cg.FarewellActive) _cg.Hide();
            if (_logic.Current == EndingFarewellLogic.Phase.Unavailable && _lastPhase == EndingFarewellLogic.Phase.Anchoring
                && anchored == false)
                Debug.LogWarning("[EndingFarewellDoll] 別れの人形の立ち位置が解けない（頭の追跡か較正が無い）→ 出さない");
            if (_logic.Current == EndingFarewellLogic.Phase.Idle)
            {
                _beginAccepted = false;
                ShotCaptured = false;
            }

            if (_logic.Current == _lastPhase) return;
            if (_logic.Current == EndingFarewellLogic.Phase.Face) FacedCount++;
            if (_logic.Current == EndingFarewellLogic.Phase.Done && _lastPhase == EndingFarewellLogic.Phase.Retreat)
                CompletedCount++;
            _lastPhase = _logic.Current;
            EndingFarewellLogic.Anchor a = _cg != null ? _cg.FarewellAnchor : default;
            PhaseChanged?.Invoke(_logic.Current, a.dollUv, a.visitorUv);
        }
    }
}
