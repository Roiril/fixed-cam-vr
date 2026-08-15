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

        // 可動域。⚠ **SerializeField にしない** — 既存プレハブに焼かれていない値は 0 で読まれ、
        // 腕が一切動かなくなる（unity-prefab-fields の罠）。人形ごとに変えたくなったら
        // show.json の actor 定義へ出す。
        //
        // ⚠ 45°（`ActorArmLogic.DefaultMaxSwingDeg`）は**写真から押し出した旧 Ichimatsu** の値。
        // あちらは袖が腕とほぼ一体で、離すと布が引き伸ばされて手が突き出た。いまの人形は
        // 袖の重みを腕の軸からの距離で付けてあり、**腕を上下にしか振らない**ので、
        // Blender で水平から ±45°（＝ここでの角度で 45〜135°）まで破綻しないことを絵で確認した。
        // 90° = 腕が水平。ユーザー指示（2026-08-15）「完全にとは言わないものの、けっこう追従」。
        private const float MaxSwingDeg = 90f;
        // 肘はほぼ伸ばしたままにする。人形の腕は白磁の一本で、曲げると袖の中で布を突き破る。
        private const float MaxReachRatio = 0.95f;

        private Transform? _lUpper, _lLower, _lHand, _rUpper, _rLower, _rHand, _head;
        private bool _prepared;
        private float _measuredHeightM = 1.7f;
        private float _lWeight, _rWeight;
        private Vector3 _lSmoothed, _rSmoothed;
        private bool _seeded;

        /// <summary>
        /// 片腕 1 フレーム分の実測値。**「腕が違和感なく動くか」は絵だけでは判定できない**ので
        /// （伸び切っているのか、たまたまその角度なのかが見分けられない）、駆動の中で分かる値を出す。
        /// 診断専用で、駆動そのものには使わない。
        /// </summary>
        public readonly struct ArmDiag
        {
            /// <summary>肩ボーンのワールド位置。</summary>
            public readonly Vector3 Shoulder;
            /// <summary>写像で求めた手首の目標（届くかどうかは別）。</summary>
            public readonly Vector3 Target;
            /// <summary>IK が実際に置いた手首。<see cref="Target"/> と離れていれば届いていない。</summary>
            public readonly Vector3 Wrist;
            /// <summary>上腕 + 前腕（この人形の腕の長さ）。</summary>
            public readonly float ArmLength;
            /// <summary>人形の体の前方向（<see cref="ForwardM"/> の基準）。</summary>
            public readonly Vector3 Forward;

            public ArmDiag(Vector3 shoulder, Vector3 target, Vector3 wrist, float armLength, Vector3 forward)
            {
                Shoulder = shoulder; Target = target; Wrist = wrist;
                ArmLength = armLength; Forward = forward;
            }

            /// <summary>目標へ届かなかった距離 (m)。0 なら目標どおり。</summary>
            public float DeficitM => Vector3.Distance(Target, Wrist);

            /// <summary>腕の伸び率（肩→手首 / 腕長）。1.0 が伸び切り。</summary>
            public float Extension => ArmLength > 1e-4f ? Vector3.Distance(Shoulder, Wrist) / ArmLength : 0f;

            /// <summary>目標が腕の届く範囲を超えていた割合（1.0 = ちょうど届く）。</summary>
            public float TargetReachRatio =>
                ArmLength > 1e-4f ? Vector3.Distance(Shoulder, Target) / ArmLength : 0f;

            /// <summary>
            /// 手首の前後（m・+ が体の前）。**大きく負なら手が背中側へ回っている**。
            /// 体の向きが体験者に追いつけていないと、ここに出る。
            /// </summary>
            public float ForwardM => Vector3.Dot(Wrist - Shoulder, Forward);
        }

        /// <summary>直近の <see cref="Drive"/> での左腕の実測値（診断用）。</summary>
        public ArmDiag LeftDiag { get; private set; }

        /// <summary>直近の <see cref="Drive"/> での右腕の実測値（診断用）。</summary>
        public ArmDiag RightDiag { get; private set; }

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
        /// <param name="playerBodyYawDeg">
        /// 体験者の**体**の向き。省略すると頭の向きを使う（＝首を振ると手が振り回される）ので、
        /// 呼び出し側は <see cref="ActorArmLogic.SmoothYawDeg"/> で鈍らせた値を渡すこと。
        /// </param>
        public void Drive(in ShowBodyInput body, float actorYawDeg, float dt,
                          float? playerBodyYawDeg = null)
        {
            if (!HasRig) return;

            // 縮尺は腕の**長さ**の比で取る（背丈の比ではない）。人形と体験者で頭身が違っても、
            // 「腕を伸ばし切った」が人形でも伸ばし切りになるのはこちら。
            float headHeightM = body.HasHead ? body.HeadPos.y - transform.position.y : 1.6f;
            float bodyYaw = playerBodyYawDeg ?? body.HeadYawDeg;

            LeftDiag = DriveArm(_lUpper!, _lLower!, _lHand!, -1f, body.HasHead && body.LeftValid,
                                body.LeftHandPos, body, actorYawDeg, bodyYaw, headHeightM, dt,
                                ref _lWeight, ref _lSmoothed);
            RightDiag = DriveArm(_rUpper!, _rLower!, _rHand!, +1f, body.HasHead && body.RightValid,
                                 body.RightHandPos, body, actorYawDeg, bodyYaw, headHeightM, dt,
                                 ref _rWeight, ref _rSmoothed);
            _seeded = true;
        }

        private ArmDiag DriveArm(Transform upper, Transform lower, Transform hand, float side,
                                 bool valid, Vector3 handWorld, in ShowBodyInput body,
                                 float actorYawDeg, float playerBodyYawDeg, float headHeightM, float dt,
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
                // 体験者の肩から手へのベクトルを、腕の長さの比で人形の肩へ移す。
                // 人形の肩は実ボーン（測れる）、体験者の肩は頭からの人体比率で推定する。
                Vector3 playerShoulder = ActorArmLogic.EstimateShoulder(
                    body.HeadPos, playerBodyYawDeg, side, headHeightM);
                float scale = ActorArmLogic.ArmScale(armLen, headHeightM);
                Vector3 mapped = ActorArmLogic.MapHandToActor(handWorld, playerShoulder, playerBodyYawDeg,
                                                              shoulder, actorYawDeg, scale);
                // **腕は上下にしか振らない**（2026-08-15 ユーザー指示「腕は上下するだけでいい」）。
                // 先に前後を落としてから角度を測る（逆順だと倒したぶん角度が変わって上限を超える）。
                mapped = ActorArmLogic.LimitToVerticalSwing(shoulder, mapped, transform.forward,
                                                            idle - shoulder);
                // 人形の可動域へ収める。**人形は人間ほど腕が動かない**。
                mapped = ActorArmLogic.LimitToDollRange(shoulder, mapped, idle - shoulder, armLen,
                                                        MaxSwingDeg, MaxReachRatio);
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

            return new ArmDiag(shoulder, smoothed, end, armLen, transform.forward);
        }

        private static void AlignBone(Transform bone, Vector3 fromDir, Vector3 toDir)
        {
            if (fromDir.sqrMagnitude < 1e-8f || toDir.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(fromDir, toDir) * bone.rotation;
        }

        /// <summary>
        /// ボーンが頭頂に届いていないと判断する、メッシュ上端との差の割合。
        ///
        /// SkinnedMeshRenderer の bounds は Unity が膨らませるので、その分は許す。
        /// 実測（2026-08-05）: **Remy は 11%**（HeadTop_End まであるのに髪と服で膨らむ）、
        /// **市松人形は 42%**（Head が頭の中ほどまでしか無い）。あいだを取って 25%。
        /// ⚠ 10% にすると Remy が境界に乗って縮尺が揺れる（実際に揺れた）。
        /// </summary>
        private const float BoneTopSlack = 0.25f;

        /// <summary>
        /// 実寸の身長 (m)。**ボーンの最高点**（Mixamo なら HeadTop_End）を基本とする。
        /// bounds は Unity が大きめに膨らませるため、そのまま使うと人間サイズのモデルが
        /// 4m 超と出る（実測）。ボーン位置なら膨らみの影響を受けない。
        ///
        /// ⚠ ただし**頭頂までボーンがある人形ばかりではない**。市松人形（自作リグ）は Head が
        /// 頭の中ほどにあるだけで、ボーンで測ると実寸を 3 割近く小さく見積もる。その縮尺で
        /// show.json の heightM 1.6m に合わせると **2.27m の巨人**になった（実測）。
        /// メッシュの上端がボーンより大きく上にあるときは、メッシュ側を身長とみなす。
        /// </summary>
        private float MeasureHeight()
        {
            float rootY = transform.position.y;
            float scaleY = Mathf.Abs(transform.lossyScale.y) > 1e-4f ? Mathf.Abs(transform.lossyScale.y) : 1f;

            float boneTop = rootY;
            bool anyBone = false;
            foreach (Transform t in GetComponentsInChildren<Transform>(true))
            {
                if (t == transform) continue;
                if (t.position.y > boneTop) boneTop = t.position.y;
                anyBone = true;
            }

            bool anyR = false;
            Bounds b = new Bounds(transform.position, Vector3.zero);
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                if (!anyR) { b = r.bounds; anyR = true; }
                else b.Encapsulate(r.bounds);
            }
            float meshTop = anyR ? b.max.y : rootY;

            if (anyBone && boneTop - rootY > 0.05f)
            {
                float boneH = boneTop - rootY;
                // 頭頂までボーンがあるか。無ければメッシュの高さを採る。
                if (!anyR || meshTop <= boneTop + boneH * BoneTopSlack) return boneH / scaleY;
                return Mathf.Max(boneH, meshTop - rootY) / scaleY;
            }

            // ボーンが無い（単一メッシュ）モデル。
            return anyR ? Mathf.Max(0.1f, b.size.y / scaleY) : 1.7f;
        }
    }
}
