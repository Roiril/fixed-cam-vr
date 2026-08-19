#nullable enable
using System;
using FixedCamVr.Streaming.Cg;
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>入れ替わりのノイズ</b>の実体（`canon/LEDGER.md` 0089）。
    /// <c>_SwapRect</c> / <c>_SwapCover</c> / <c>_SwapSolid</c> uniform の**唯一の writer**
    /// （<see cref="GlitchFx"/> が <c>_Glitch</c> を独占するのと同じ流儀）。
    ///
    /// 全画面の砂嵐で入れ替えるのをやめ、**映像の中の体験者だけ**を砂で覆って入れ替える。
    /// 進み方は <see cref="SwapMorphLogic"/>（純ロジック・テストあり）、形は
    /// <see cref="ShowCgLayer"/> が描く人形のシルエットそのもの。
    ///
    /// <b>この 3 つを同じ時計で動かすのが仕事</b>:
    ///   1. 人型の背丈（<see cref="ShowCgLayer.SetSwapHeight"/>）— 体験者の背丈 ⇄ 人形の背丈
    ///   2. 砂の被覆と実体化（uniform）
    ///   3. <b>画面の差し替え</b>（覆い切った 1 フレームで <c>onCovered</c> を 1 回だけ呼ぶ）
    ///
    /// ⚠ 3 が早いと体験者が砂の下ではなく画の中で消え、遅いと砂の中で背景が動く。
    ///   だから差し替えは <see cref="SwapMorphLogic.Sample.justCovered"/> の 1 フレームでしか行わない。
    /// ⚠ <b>人形を掴んだままにする</b>（<see cref="ShowCgLayer.HoldForSwap"/>）。「人形 → 人」は
    ///   人形を出さないカットへ移るのと同時に始まるので、掴まないと 1 コマも映らない。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SwapMorphFx : MonoBehaviour
    {
        private static readonly int SwapRectId = Shader.PropertyToID("_SwapRect");
        private static readonly int SwapCoverId = Shader.PropertyToID("_SwapCover");
        private static readonly int SwapSolidId = Shader.PropertyToID("_SwapSolid");
        private static readonly int SwapSeedId = Shader.PropertyToID("_SwapSeed");

        [Tooltip("スクリーンの Renderer。null なら同じ GameObject から取る。")]
        [SerializeField] private Renderer? screenRenderer;

        [Tooltip("人形の層。null なら同じ GameObject / シーンから探す。")]
        [SerializeField] private ShowCgLayer? cgLayer;

        [Tooltip("覆い切った瞬間の全画面の乱れ。null なら乱れは出ない（入れ替わり自体は成立する）。")]
        [SerializeField] private GlitchFx? glitchFx;

        private readonly SwapMorphLogic _logic = new SwapMorphLogic();
        private Material? _material;
        private Action? _onCovered;

        /// <summary>入れ替わりの前に出ていた人形の id（晴れる段でここへ戻す）。</summary>
        private string _dollActorId = "";

        /// <summary>いま人の姿を出しているか（人形へ戻すのは出していたときだけ）。</summary>
        private bool _humanShown;

        /// <summary>人の姿が用意されていないことは 1 回だけ言う（毎回言うとログが埋まる）。</summary>
        private bool _warnedNoHuman;

        /// <summary>入れ替わりが走っているか（テレメトリ用）。</summary>
        public bool Active => _logic.Active;

        /// <summary>進み 0..1（テレメトリ用）。</summary>
        public float Progress01 => _logic.Progress01;

        /// <summary>いまの向き（テレメトリ用）。</summary>
        public SwapMorphLogic.Dir Direction => _logic.Direction;

        /// <summary>いま砂が人型をどこまで埋めているか 0..1（テレメトリ用。**画に出た側**）。</summary>
        public float Cover { get; private set; }

        /// <summary>いまの人型の背丈 (m)（テレメトリ用。**画に出た側**）。</summary>
        public float HeightM { get; private set; }

        /// <summary>このランで入れ替わりが走った回数（テレメトリ用）。</summary>
        public int Count { get; private set; }

        /// <summary>
        /// <c>_SwapRect</c> を書けたか。**false なら砂は 1 画素も出ない**
        /// （人形がカメラの後ろ / 位置合わせ未完了 / 人形が居ない）。
        /// 「段は進んだのに画には何も出ていない」を捕まえるための観測。
        /// </summary>
        public bool RectResolved { get; private set; }

        private void Awake()
        {
            if (screenRenderer == null) screenRenderer = GetComponent<Renderer>();
            _material = screenRenderer != null ? screenRenderer.material : null;
            if (cgLayer == null) cgLayer = GetComponent<ShowCgLayer>();
            if (cgLayer == null) cgLayer = FindObjectOfType<ShowCgLayer>();
            if (glitchFx == null) glitchFx = GetComponent<GlitchFx>();
            ClearUniforms();
        }

        private void OnDisable() => Cancel();

        /// <summary>
        /// 入れ替わりを始める。<paramref name="onCovered"/> は<b>覆い切った 1 フレーム</b>で
        /// 1 回だけ呼ばれる（画面の差し替えをここで行う）。
        ///
        /// 人形の実体は既に <see cref="ShowCgLayer"/> が掴んでいる前提
        /// （「人 → 人形」は <see cref="TakeRunner"/> が先に <c>Apply</c> し、
        ///  「人形 → 人」は前のカットの人形をそのまま使う）。
        /// </summary>
        /// <returns>始められたか。人形が居なければ false（呼び出し側が乱れ遷移へ倒す）。</returns>
        public bool Begin(SwapMorphLogic.Dir dir, float totalSec, Action? onCovered)
        {
            if (cgLayer == null || !cgLayer.IsVisible || cgLayer.CurrentActorHeightM <= 0f)
            {
                Debug.LogWarning("[SwapMorphFx] 人形が居ないので入れ替わりのノイズを出せない" +
                                 "（カメラ姿勢が未著作 / 位置合わせが未完了 / actor が show.json に無い）");
                return false;
            }

            float human = cgLayer.VisitorHeightM();
            float doll = cgLayer.CurrentActorHeightM;
            bool toDoll = dir == SwapMorphLogic.Dir.ToDoll;
            _logic.Begin(dir, totalSec, toDoll ? human : doll, toDoll ? doll : human);

            _onCovered = onCovered;
            _dollActorId = cgLayer.CurrentActorId;
            _humanShown = false;
            Count++;
            cgLayer.HoldForSwap(true);

            // ⚠⚠ **人形を人の背丈へ引き伸ばしても人型には見えない**（2026-08-19 に絵で確かめた）。
            //   市松人形は頭が大きいので、拡大すると人ではなく**頭の巨大な塊**になる。
            //   ⇒ **背丈が動いている間の形は、どちらの向きでも人**にする。
            //     人 → 人形: 始めから人（砂は映像の中の体験者を覆うので、その時点で人の形が要る）
            //     人形 → 人: **覆い切ってから**人へ替える（砂が湧く間は人形を覆う必要がある）
            if (toDoll) SwapToHuman();

            // 1 フレーム目から正しい背丈で出す（Apply 直後の 1 コマだけ実寸で出るのを防ぐ）。
            cgLayer.SetSwapHeight(toDoll ? human : doll);
            cgLayer.SetGroundContact(toDoll ? 0f : 1f);
            Write(cover: 0f, solid: 0f);
            return true;
        }

        /// <summary>
        /// 途中で畳む（演出の中止・ランリセット・体験の終了）。
        /// <b>覆い切る前に畳んだら <c>onCovered</c> は呼ばない</b> — 呼ぶと砂が無い所で画面が差し替わる。
        /// </summary>
        public void Cancel()
        {
            _logic.Cancel();
            _onCovered = null;
            ClearUniforms();
            if (cgLayer == null) { _humanShown = false; return; }
            // 人の姿のまま畳むと、次のカットまで体験者の分身が立ち続ける。必ず人形へ戻す。
            RestoreDoll();
            cgLayer.SetSwapHeight(0f);
            cgLayer.SetGroundContact(1f);
            cgLayer.HoldForSwap(false);
        }

        private void LateUpdate()
        {
            if (!_logic.Active) return;

            // ⚠ 人形の位置は ShowCgLayer.LateUpdate が決める。**その後**に読まないと矩形が 1 フレーム遅れ、
            //   歩いている体験者の上で砂だけが遅れて付いてくる。実行順は Script Execution Order ではなく
            //   「同じ LateUpdate の中で cgLayer が先に居る」ことに依存させない —
            //   矩形は毎フレーム引き直すので、遅れても 1 フレームで、位置は次で追いつく。
            SwapMorphLogic.Sample s = _logic.Tick(Time.unscaledDeltaTime);
            Cover = s.cover;
            HeightM = s.heightM;

            // 砂に覆われている間だけ姿を替えられる（替わったことが 1 画素も見えない）。
            //   覆い切った縁   … 人形 → 人。ここから人の形で育つ
            //   縮み切った縁   … 人 → 人形。晴れると人形が残る
            if (s.justCovered && _logic.Direction == SwapMorphLogic.Dir.ToHuman) SwapToHuman();
            if (s.justSettling && _humanShown) RestoreDoll();

            if (cgLayer != null)
            {
                cgLayer.SetSwapHeight(s.heightM);
                cgLayer.SetGroundContact(s.ground);
            }
            Write(s.cover, s.solid);

            if (s.justCovered)
            {
                Action? act = _onCovered;
                _onCovered = null;
                act?.Invoke();
                // 人型の外で同時に起きる差し替え（左半分の凍結解除・人形の群れの消滅）を覆う。
                // **入れ替わりを隠すためではない** — そちらは既に砂が覆い切っている。
                glitchFx?.Pulse(SwapMorphLogic.VeilLevel, SwapMorphLogic.VeilSec);
            }

            if (!s.justFinished) return;

            // 終わり。人形の背丈と影を戻し、掴みを解く。
            // 「人形 → 人」なら掴みを解いた瞬間に保留していた Hide が走って人形が消える。
            ClearUniforms();
            if (cgLayer == null) return;
            RestoreDoll();
            cgLayer.SetSwapHeight(0f);
            cgLayer.SetGroundContact(1f);
            cgLayer.HoldForSwap(false);
        }

        /// <summary>人の姿へ替える（用意されていなければ人形のまま。理由は 1 回だけ言う）。</summary>
        private void SwapToHuman()
        {
            if (cgLayer == null || _humanShown) return;
            _humanShown = cgLayer.TrySwapToActor(TakeSchema.SwapHumanActorId);
            if (_humanShown || _warnedNoHuman) return;
            _warnedNoHuman = true;
            Debug.LogWarning($"[SwapMorphFx] actors[] に '{TakeSchema.SwapHumanActorId}' が無い" +
                             " → 人形を引き伸ばして代用する（人型には見えない）");
        }

        /// <summary>人形の姿へ戻す。</summary>
        private void RestoreDoll()
        {
            if (!_humanShown) return;
            _humanShown = false;
            cgLayer?.TrySwapToActor(_dollActorId);
        }

        private void Write(float cover, float solid)
        {
            if (_material == null) return;
            Vector4 rect = new Vector4(0.5f, 0.5f, 0.2f, 0f);
            RectResolved = cgLayer != null && cgLayer.TrySwapRect(out rect);
            if (!RectResolved) rect = new Vector4(0.5f, 0.5f, 0.2f, 0f);
            _material.SetVector(SwapRectId, rect);
            _material.SetFloat(SwapCoverId, Mathf.Clamp01(cover));
            _material.SetFloat(SwapSolidId, Mathf.Clamp01(solid));
            _material.SetFloat(SwapSeedId, Time.unscaledTime);
        }

        private void ClearUniforms()
        {
            Cover = 0f;
            HeightM = 0f;
            RectResolved = false;
            if (_material == null) return;
            _material.SetVector(SwapRectId, new Vector4(0.5f, 0.5f, 0.2f, 0f));
            _material.SetFloat(SwapCoverId, 0f);
            _material.SetFloat(SwapSolidId, 0f);
        }
    }
}
