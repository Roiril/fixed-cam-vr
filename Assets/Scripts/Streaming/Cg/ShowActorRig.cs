#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming.Cg
{
    /// <summary>
    /// CG 人形（Humanoid リグ）の駆動。**腕だけ**を体験者のハンドトラッキングで動かし、
    /// 手が取れない間は体側へ垂らした idle ポーズへ合流する。
    ///
    /// 全身アニメーションは持たない（＝ FBX の bind ポーズ = Mixamo なら T ポーズのまま脚と胴は硬直）。
    /// これは手抜きではなく設計選択で、(1) 直立不動の人形の方が演出として強い、
    /// (2) AnimatorController を要求しないので **どの humanoid FBX でも即動く**、の 2 点による。
    /// idle アニメを足したくなったら Animator に Controller を挿すだけでよい（このスクリプトは
    /// LateUpdate 相当で腕だけ上書きするので共存する）。
    ///
    /// 駆動は <see cref="ShowCgLayer"/> が毎 LateUpdate に <see cref="Drive"/> を呼ぶ（実行順に依存しない）。
    /// 設計の正本: <c>.claude/plans/2026-07-27_cg-actor-hand-tracking.md</c>。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ShowActorRig : MonoBehaviour
    {
        [Tooltip("Humanoid の Animator。null なら同 GameObject / 子から探す。")]
        [SerializeField] private Animator? animator;

        [Tooltip("腕を垂らした idle での手首位置（腕の長さに対する比）。1 = 真下いっぱい。")]
        [SerializeField, Range(0.6f, 1f)] private float idleReach = 0.92f;

        [Tooltip("idle で腕を体から離す量（腕の長さに対する比）。0 だと体にめり込む。")]
        [SerializeField, Range(0f, 0.4f)] private float idleSpread = 0.10f;

        private Transform? _lUpper, _lLower, _lHand, _rUpper, _rLower, _rHand, _head;
        private bool _prepared;
        private float _measuredHeightM = 1.7f;
        private float _lWeight, _rWeight;
        private Vector3 _lSmoothed, _rSmoothed;
        private bool _seeded;

        /// <summary>腕を駆動できるリグ（Humanoid の肩〜手が揃っている）か。</summary>
        public bool HasRig => _lUpper != null && _lLower != null && _lHand != null
                              && _rUpper != null && _rLower != null && _rHand != null;

        /// <summary>プレハブ実寸の身長 (m)。show.json の heightM へ合わせる倍率の計算に使う。</summary>
        public float MeasuredHeightM => _measuredHeightM;

        /// <summary>頭のワールド位置（手の相対ベクトルを乗せる基準点）。</summary>
        public Vector3 HeadWorldPosition =>
            _head != null ? _head.position : transform.position + transform.up * (_measuredHeightM * 0.9f);

        private void Awake() => Prepare();

        /// <summary>ボーンと実寸を解決する（多重呼び出し安全）。</summary>
        public void Prepare()
        {
            if (_prepared) return;
            _prepared = true;

            if (animator == null) animator = GetComponentInChildren<Animator>();
            // Humanoid（Avatar 付き）なら正規のボーン写像が使える。
            if (animator != null && animator.isHuman)
            {
                _lUpper = animator.GetBoneTransform(HumanBodyBones.LeftUpperArm);
                _lLower = animator.GetBoneTransform(HumanBodyBones.LeftLowerArm);
                _lHand = animator.GetBoneTransform(HumanBodyBones.LeftHand);
                _rUpper = animator.GetBoneTransform(HumanBodyBones.RightUpperArm);
                _rLower = animator.GetBoneTransform(HumanBodyBones.RightLowerArm);
                _rHand = animator.GetBoneTransform(HumanBodyBones.RightHand);
                _head = animator.GetBoneTransform(HumanBodyBones.Head);
            }
            // Generic リグ（Mixamo の既定取り込み等）でも動かす。**手を名前で見つけて親を 2 つ遡る**
            // だけで肘・肩が取れる（mixamorig:LeftHand → LeftForeArm → LeftArm）。
            // Humanoid を要求すると共有アセットの取り込み設定を変えさせることになるので、そうしない。
            if (!HasRig)
            {
                _lHand = _lHand != null ? _lHand : FindHand(isLeft: true);
                _rHand = _rHand != null ? _rHand : FindHand(isLeft: false);
                if (_lHand != null && _lHand.parent != null)
                {
                    _lLower = _lHand.parent;
                    _lUpper = _lLower.parent;
                }
                if (_rHand != null && _rHand.parent != null)
                {
                    _rLower = _rHand.parent;
                    _rUpper = _rLower.parent;
                }
            }
            if (_head == null) _head = FindBone(n => n.Contains("head") && !n.Contains("top") && !n.Contains("end"));

            _measuredHeightM = MeasureHeight();
        }

        // 名前で手のボーンを探す（指と "…_end" は除外する）。区切り文字と接頭辞は無視して比較する。
        private Transform? FindHand(bool isLeft)
        {
            string side = isLeft ? "left" : "right";
            string abbrev = isLeft ? "_l" : "_r";
            return FindBone(n =>
                n.Contains("hand")
                && !n.Contains("index") && !n.Contains("thumb") && !n.Contains("middle")
                && !n.Contains("ring") && !n.Contains("pinky") && !n.Contains("end")
                && (n.Contains(side) || n.EndsWith(abbrev)));
        }

        private Transform? FindBone(System.Func<string, bool> match)
        {
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                int colon = n.LastIndexOf(':');            // "mixamorig:LeftHand" → "lefthand"
                if (colon >= 0) n = n.Substring(colon + 1);
                n = n.Replace(" ", "").Replace(".", "");
                if (match(n)) return t;
            }
            return null;
        }

        /// <summary>表示のたびに平滑化の履歴を捨てる（前回の姿勢から引きずらない）。</summary>
        public void ResetPose()
        {
            _lWeight = 0f;
            _rWeight = 0f;
            _seeded = false;
        }

        /// <summary>
        /// 1 フレーム分の駆動。<paramref name="actorYawDeg"/> は人形の体の向き（ワールド yaw）。
        /// リグが無い（Humanoid でない / 代用の箱）なら何もしない。
        /// </summary>
        public void Drive(in ShowBodyInput body, float actorYawDeg, float dt)
        {
            if (!HasRig) return;

            // 体格比 = 人形の実効身長（実寸 × 現在の縮尺）/ 体験者の頭高（床 = 人形の足元 y）。
            float actorHeightM = _measuredHeightM * Mathf.Abs(transform.lossyScale.y);
            float headHeightM = body.HasHead ? body.HeadPos.y - transform.position.y : 1.6f;
            float scale = ActorArmLogic.BodyScale(actorHeightM, headHeightM);

            DriveArm(_lUpper!, _lLower!, _lHand!, -1f, body.HasHead && body.LeftValid, body.LeftHandPos,
                     body, actorYawDeg, scale, dt, ref _lWeight, ref _lSmoothed);
            DriveArm(_rUpper!, _rLower!, _rHand!, +1f, body.HasHead && body.RightValid, body.RightHandPos,
                     body, actorYawDeg, scale, dt, ref _rWeight, ref _rSmoothed);
            _seeded = true;
        }

        private void DriveArm(Transform upper, Transform lower, Transform hand, float side,
                              bool valid, Vector3 handWorld, in ShowBodyInput body,
                              float actorYawDeg, float scale, float dt,
                              ref float weight, ref Vector3 smoothed)
        {
            Vector3 shoulder = upper.position;
            float upperLen = Vector3.Distance(shoulder, lower.position);
            float lowerLen = Vector3.Distance(lower.position, hand.position);
            float armLen = Mathf.Max(1e-3f, upperLen + lowerLen);

            Vector3 idle = shoulder
                           + Vector3.down * (armLen * idleReach)
                           + transform.right * (side * armLen * idleSpread)
                           + transform.forward * (armLen * 0.04f);

            weight = ActorArmLogic.Blend(weight, valid, dt);
            Vector3 desired = idle;
            if (weight > 0f && body.HasHead)
            {
                Vector3 mapped = ActorArmLogic.MapHandToActor(handWorld, body.HeadPos, body.HeadYawDeg,
                                                              HeadWorldPosition, actorYawDeg, scale);
                desired = Vector3.Lerp(idle, mapped, weight);
            }

            smoothed = _seeded ? ActorArmLogic.Smooth(smoothed, desired, dt) : desired;

            // 肘は体の後ろ下・やや外へ倒す（前に突き出すと人間の腕に見えない）。
            Vector3 pole = shoulder + (-transform.forward * 0.6f + Vector3.down * 0.8f
                                       + transform.right * (side * 0.25f)).normalized * armLen;

            TwoBoneIk.Solve(shoulder, smoothed, pole, upperLen, lowerLen,
                            out Vector3 joint, out Vector3 end);

            AlignBone(upper, lower.position - upper.position, joint - upper.position);
            AlignBone(lower, hand.position - lower.position, end - lower.position);
        }

        private static void AlignBone(Transform bone, Vector3 fromDir, Vector3 toDir)
        {
            if (fromDir.sqrMagnitude < 1e-8f || toDir.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(fromDir, toDir) * bone.rotation;
        }

        /// <summary>
        /// 実寸の身長 (m)。**ボーンの最高点**（Mixamo なら HeadTop_End）を使う。
        /// SkinnedMeshRenderer の bounds は Unity が大きめに膨らませるため、そのまま使うと
        /// 人間サイズのモデルが 4m 超と出る（実測）。ボーン位置なら膨らみの影響を受けない。
        /// </summary>
        private float MeasureHeight()
        {
            float rootY = transform.position.y;
            float top = rootY;
            bool any = false;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t == transform) continue;
                if (t.position.y > top) top = t.position.y;
                any = true;
            }
            float scaleY = Mathf.Abs(transform.lossyScale.y) > 1e-4f ? Mathf.Abs(transform.lossyScale.y) : 1f;
            if (any && top - rootY > 0.05f) return (top - rootY) / scaleY;

            // ボーンが無い（単一メッシュ）モデルは renderer bounds へフォールバックする。
            bool anyR = false;
            Bounds b = new Bounds(transform.position, Vector3.zero);
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!anyR) { b = r.bounds; anyR = true; }
                else b.Encapsulate(r.bounds);
            }
            return anyR ? Mathf.Max(0.1f, b.size.y / scaleY) : 1.7f;
        }
    }
}
