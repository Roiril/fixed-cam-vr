#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// <b>頭のヨーだけを緩急つきで追う根。</b> 子に置いた面は、見上げても見下ろしても
    /// その場に残り、<b>眼に貼り付かない</b>（<see cref="YawFollowLogic"/> ＝ 本編のスクリーンと
    /// 同じ法則）。
    ///
    /// ⚠⚠ <b>これが HMD 内の面の既定の追従。</b> 頭の子に置いて局所座標だけ決める
    /// （＝ head-lock）と、上下に振っても面が眼から離れず読みづらい。2026-08-13 に
    /// 題字がその形で赤入れを受け（<c>canon/LEDGER.md</c> 0029）、2026-08-20 に
    /// <b>体験前の注意書き（TitleNotice）と終幕の報告（OutroReport）</b>も同じ指摘を受けた。
    ///
    /// ⚠ <b>視界を覆う黒はこれに乗せない。</b> 覆いは頭に貼り付いたままでなければ、
    /// 振り向いた瞬間に縁が視界へ入って現実が細く覗く（<see cref="TitleScreen"/> の絶対条件）。
    /// 乗せるのは<b>読ませるもの</b>だけ。
    ///
    /// ⚠ <b>頭が解決できないときは何もしない。</b> 親（CenterEyeAnchor 直下）の局所姿勢が
    /// そのまま残るので、従来の head-lock として振る舞う。面が消えるより黙って劣化する方がよい。
    ///
    /// 値は <see cref="ScreenAnchor"/> / <see cref="TitleScreen"/> と対。
    /// <b>ここが唯一の供給元</b>で、あちらは const をここから引く
    /// （片方だけ変えると体験の中で追従の癖が 2 種類になる）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class HeadYawFollow : MonoBehaviour
    {
        /// <summary>止まっている状態から動き出す閾値 (度)。微小な首の揺れを吸収する。</summary>
        public const float YawDeadzoneDeg = 0.5f;

        /// <summary>頭の手前どこで止まるか (度)。<b>0 ＝ 頭の正面ちょうどを目指して、そこで止まる。</b></summary>
        public const float YawTrailDeg = 0f;

        /// <summary>追従の緩急 (s)。</summary>
        public const float SmoothTimeSec = 0.30f;

        /// <summary>全視野が流れる速さの上限 (度/秒)。上げると酔う。</summary>
        public const float MaxYawSpeedDegPerSec = 110f;

        /// <summary>これ以上置いていかれたら上限を上げて追いつく (度)。</summary>
        public const float CatchUpThresholdDeg = 45f;

        /// <summary>逆走ガードの倍率。</summary>
        public const float CatchUpBoost = 2f;

        /// <summary>これを超える dt は着脱・pause 明けとみなし、種を置き直して 1 フレーム進めない。</summary>
        public const float ResumeGapSec = 0.5f;

        /// <summary>頭が見つからないときの探し直しの間隔 (s)。毎フレーム探すと只では済まない。</summary>
        private const float ResolveRetrySec = 1f;

        [Tooltip("頭。null ならシーンの CenterEyeAnchor を拾う。居なければ追従しない（従来の head-lock）。")]
        [SerializeField] private Transform? head;

        private readonly YawFollowLogic _yaw = new YawFollowLogic();
        private bool _seeded;
        private float _resolveWait;

        /// <summary>頭を解決できているか（false なら親の局所姿勢のまま ＝ head-lock）。</summary>
        public bool HasHead => head != null;

        /// <summary>
        /// 追従の根を <paramref name="parent"/> の子として作る。面はこの戻り値の下へぶら下げる。
        /// </summary>
        public static HeadYawFollow Attach(Transform parent, string name = "YawFollowRoot")
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            return go.AddComponent<HeadYawFollow>();
        }

        /// <summary>
        /// いまの頭のヨーへ置き直す（速度は 0）。<b>面が出る直前に呼ぶ</b> —
        /// 呼ばないと、前に消えたときのヨーから緩慢に寄ってくるので、
        /// 出てきた面が視界の外から流れ込む。
        /// </summary>
        public void SnapToHead()
        {
            // ⚠ ここで探し直さない。毎フレーム呼ばれうるので、Find は LateUpdate 側の
            //    間引きに任せる（見つかっていなければ、見つかった次のフレームに種が入る）。
            if (head == null)
            {
                _seeded = false;
                return;
            }
            _yaw.Reseat(head.eulerAngles.y);
            _seeded = true;
            ApplyPose(_yaw.CurrentYaw);
        }

        private void Awake() => ResolveHead();

        private void ResolveHead()
        {
            if (head != null) return;
            var anchor = GameObject.Find("CenterEyeAnchor");
            if (anchor != null) head = anchor.transform;
        }

        private void LateUpdate()
        {
            if (head == null)
            {
                _resolveWait += Time.unscaledDeltaTime;
                if (_resolveWait >= ResolveRetrySec)
                {
                    _resolveWait = 0f;
                    ResolveHead();
                }
                return;
            }

            float dt = Time.unscaledDeltaTime;
            float headYaw = head.eulerAngles.y;

            // 着脱・pause 明けの巨大 dt は SmoothDamp をスナップさせる。種を置き直して進めない。
            if (!_seeded || dt > ResumeGapSec)
            {
                _yaw.Reseat(_seeded ? _yaw.CurrentYaw : headYaw);
                _seeded = true;
                ApplyPose(_yaw.CurrentYaw);
                return;
            }

            ApplyPose(_yaw.Step(headYaw, dt, YawDeadzoneDeg, YawTrailDeg, SmoothTimeSec,
                                MaxYawSpeedDegPerSec, CatchUpThresholdDeg, CatchUpBoost));
        }

        /// <summary>位置は頭に付いてくるが、<b>姿勢はヨーだけ</b>（頭のピッチ・ロールは入れない）。</summary>
        private void ApplyPose(float yaw)
        {
            if (head == null) return;
            transform.SetPositionAndRotation(head.position, Quaternion.Euler(0f, yaw, 0f));
        }
    }
}
