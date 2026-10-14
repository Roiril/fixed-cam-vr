#nullable enable
using System.Collections.Generic;
using UnityEngine;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// <b>Mixamo 人形（<c>Resources/ShowActors/Remy.prefab</c>）に歩行姿勢を手続きで与える</b>。
    /// PC 上の仮想体験検証（VirtualWalk）で、体験者の前を歩く NPC の見た目を作るためのもの。
    ///
    /// ⚠ <b>手続き生成にしている理由</b>: リポジトリに歩行クリップが無く、Mixamo からの取得はログインが要る。
    /// 検証用に動けば足りるので、ボーンの回転を式で直接決める（Animator は使わない）。
    /// 実演出用の歩きではない。
    ///
    /// <b>回転の作り方</b>: Mixamo のボーンはローカル軸の向きがボーンごとに違うので、ローカル X/Y/Z は回さない。
    /// <see cref="Bind"/> の時点で各ボーンの bind 回転を<b>ルート空間</b>で記録し、
    /// 毎フレーム「ルート空間の軸で作った差分 × bind 回転」を world 回転として入れ直す。
    /// 差分は親から子へ累積する（上腕の振りの上に肘の曲げが乗る）。
    ///
    /// <b>前提</b>: ルートは +Z を向き（Remy の足先は +Z 側）、<see cref="Bind"/> は bind ポーズ（T ポーズ）のうちに呼ぶ。
    /// 動かすのは Transform だけ。スキンメッシュ側の境界の更新は呼び出し側の責任。
    /// </summary>
    public sealed class NpcWalkPose
    {
        // ---- 歩行の振れ幅（度。位置はメートル）----
        private const float LegSwingDeg = 22f;
        private const float KneeStanceDeg = 4f;
        private const float KneeSwingDeg = 31f;     // 遊脚の最大 = 4 + 31 = 35
        private const float ToeOffDeg = 12f;        // 蹴り出し（つま先が下がる）
        private const float HeelStrikeDeg = 6f;     // 着地前（つま先が上がる）
        private const float ArmLowerDeg = 72f;      // T ポーズから腕を下ろす角度
        private const float ArmSwingDeg = 18f;
        private const float ElbowIdleDeg = 10f;
        private const float ElbowSwingDeg = 5f;     // 腕が前に出るほど曲げを足す
        private const float BobM = 0.015f;
        private const float SwayM = 0.012f;
        private const float HipsTwistDeg = 4f;
        private const float SpineCounterTotalDeg = 8f;   // 骨盤の捻りの 2 倍で逆に捻り、上体の捻りを 4° に収める
        private const float SpineLeanDeg = 2f;
        private const float BreathDeg = 0.6f;
        private const float BreathRate = 0.4f;      // 位相 1rad あたりの呼吸の位相
        private const float LookClampDeg = 70f;
        private const float NeckShare = 0.4f;       // 首 40% / 頭 60%

        private sealed class Bone
        {
            public readonly Transform T;
            public readonly Quaternion RelBind;     // ルート空間での bind 回転
            public Bone(Transform t, Transform root)
            {
                T = t;
                RelBind = Quaternion.Inverse(root.rotation) * t.rotation;
            }
        }

        private readonly Transform _root;
        private readonly Bone _hips;
        private readonly Vector3 _hipsBindLocalPos;
        private readonly Bone? _spine, _spine1, _spine2, _neck, _head;
        private readonly Bone? _lArm, _lFore, _rArm, _rFore;
        private readonly Bone? _lUpLeg, _lLeg, _lFoot, _rUpLeg, _rLeg, _rFoot;
        private readonly Vector3 _lArmLowerAxis, _rArmLowerAxis;   // 腕を体の横へ下ろす回転軸（ルート空間）

        /// <summary>頭のボーン（<c>mixamorig:Head</c>）。視線や顔の位置合わせ用。無ければ null。</summary>
        public Transform? Head => _head?.T;

        private NpcWalkPose(Transform root, Dictionary<string, Transform> bones, Transform hips)
        {
            _root = root;
            _hips = new Bone(hips, root);
            _hipsBindLocalPos = hips.localPosition;

            _spine = Make(root, bones, "spine");
            _spine1 = Make(root, bones, "spine1");
            _spine2 = Make(root, bones, "spine2");
            _neck = Make(root, bones, "neck");
            _head = Make(root, bones, "head");

            _lArm = Make(root, bones, "leftarm");
            _lFore = Make(root, bones, "leftforearm");
            _rArm = Make(root, bones, "rightarm");
            _rFore = Make(root, bones, "rightforearm");

            _lUpLeg = Make(root, bones, "leftupleg");
            _lLeg = Make(root, bones, "leftleg");
            _lFoot = Make(root, bones, "leftfoot");
            _rUpLeg = Make(root, bones, "rightupleg");
            _rLeg = Make(root, bones, "rightleg");
            _rFoot = Make(root, bones, "rightfoot");

            _lArmLowerAxis = LowerAxis(root, _lArm, _lFore);
            _rArmLowerAxis = LowerAxis(root, _rArm, _rFore);
        }

        private static Bone? Make(Transform root, Dictionary<string, Transform> bones, string key)
            => bones.TryGetValue(key, out Transform? t) ? new Bone(t, root) : null;

        // 腕→前腕のルート空間の向きを「真下」へ倒す回転軸。左右の符号をここで吸収する。
        private static Vector3 LowerAxis(Transform root, Bone? arm, Bone? fore)
        {
            if (arm != null && fore != null)
            {
                Vector3 dir = Quaternion.Inverse(root.rotation) * (fore.T.position - arm.T.position);
                if (dir.sqrMagnitude > 1e-8f)
                {
                    Vector3 axis = Vector3.Cross(dir.normalized, Vector3.down);
                    if (axis.sqrMagnitude > 1e-4f) return axis.normalized;
                }
            }
            return Vector3.forward;
        }

        // 名前の比較は ShowActorRig と同じ流儀（"mixamorig:LeftHand" → "lefthand"）。完全一致のみ。
        private static string Normalize(string name)
        {
            string n = name.ToLowerInvariant();
            int colon = n.LastIndexOf(':');
            if (colon >= 0) n = n.Substring(colon + 1);
            return n.Replace(" ", "").Replace(".", "");
        }

        /// <summary>
        /// <paramref name="root"/> 配下からボーンを探し、bind 回転を記録する。
        /// Hips が無ければ null。それ以外のボーンが欠けても動く（その部位だけ動かない）。
        /// </summary>
        public static NpcWalkPose? Bind(Transform root)
        {
            var bones = new Dictionary<string, Transform>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string n = Normalize(t.name);
                if (!bones.ContainsKey(n)) bones.Add(n, t);
            }
            if (!bones.TryGetValue("hips", out Transform? hips)) return null;
            return new NpcWalkPose(root, bones, hips);
        }

        /// <summary>
        /// 姿勢を決定論的に入れる（<c>Time.*</c> は使わない）。
        /// </summary>
        /// <param name="phaseRad">歩行位相（rad）。2π で 1 周 = 2 歩。呼び出し側が進んだ距離から進める。
        /// 止まっているとき呼吸の揺れも位相から作るので、位相を固定すると呼吸も止まる。</param>
        /// <param name="walk01">0 = 立ち止まり、1 = 歩行。連続的に混ぜる。</param>
        /// <param name="lookYawDeg">体に対する首と頭の向き（度、正が右）。±70° に収める。</param>
        public void Apply(float phaseRad, float walk01, float lookYawDeg = 0f)
        {
            float w = Mathf.Clamp01(walk01);
            float s = Mathf.Sin(phaseRad);
            float c = Mathf.Cos(phaseRad);
            float look = Mathf.Clamp(lookYawDeg, -LookClampDeg, LookClampDeg);
            Quaternion rootRot = _root.rotation;

            // ---- 腰: 上下 2 回 / 周、横揺れ、捻り ----
            float bob = BobM * w * Mathf.Cos(2f * phaseRad);
            float sway = SwayM * w * c;                    // 支持脚側（φ=π で左）へ寄る。+X が右
            Vector3 worldOffset = rootRot * (Vector3.right * sway + Vector3.up * bob);
            Transform? hipsParent = _hips.T.parent;
            _hips.T.localPosition = _hipsBindLocalPos
                + (hipsParent != null ? hipsParent.InverseTransformVector(worldOffset) : worldOffset);

            float hipsYaw = HipsTwistDeg * w * s;          // 左脚が前に出るとき左腰が前へ
            Quaternion accHips = Drive(_hips, rootRot, Yaw(hipsYaw), Quaternion.identity);

            // ---- 背骨: 骨盤と逆に捻る。前傾と呼吸 ----
            float counter = -SpineCounterTotalDeg * w * s;
            float breath = BreathDeg * (1f - w) * Mathf.Sin(phaseRad * BreathRate);
            Quaternion accUpper = accHips;
            accUpper = Drive(_spine, rootRot, Yaw(counter * 0.35f) * Pitch(SpineLeanDeg * w), accUpper);
            accUpper = Drive(_spine1, rootRot, Yaw(counter * 0.35f) * Pitch(breath), accUpper);
            accUpper = Drive(_spine2, rootRot, Yaw(counter * 0.30f) * Pitch(breath * 0.6f), accUpper);

            // ---- 首と頭 ----
            Quaternion accNeck = Drive(_neck, rootRot, Yaw(look * NeckShare), accUpper);
            Drive(_head, rootRot, Yaw(look * (1f - NeckShare)), accNeck);

            // ---- 腕: 脚と逆に振る ----
            float lArmFwd = -ArmSwingDeg * w * s;
            DriveArm(_lArm, _lFore, rootRot, _lArmLowerAxis, lArmFwd, w, accUpper);
            DriveArm(_rArm, _rFore, rootRot, _rArmLowerAxis, -lArmFwd, w, accUpper);

            // ---- 脚: 右は左と半周ずれる ----
            DriveLeg(_lUpLeg, _lLeg, _lFoot, rootRot, s, Mathf.Max(0f, c), w, accHips);
            DriveLeg(_rUpLeg, _rLeg, _rFoot, rootRot, -s, Mathf.Max(0f, -c), w, accHips);
        }

        // 上腕を横へ下ろしてから前後に振り、肘を曲げる。振り（前方が正）はルート空間の右軸まわり。
        private static void DriveArm(Bone? arm, Bone? fore, Quaternion rootRot, Vector3 lowerAxis,
                                     float fwdDeg, float w, Quaternion accParent)
        {
            Quaternion lower = Quaternion.AngleAxis(ArmLowerDeg, lowerAxis);
            Quaternion accArm = Drive(arm, rootRot, Swing(fwdDeg) * lower, accParent);
            float fwd01 = Mathf.Max(0f, fwdDeg / ArmSwingDeg);
            float elbow = ElbowIdleDeg + ElbowSwingDeg * w * fwd01;
            Drive(fore, rootRot, Swing(elbow), accArm);       // 前腕は前へ曲げる
        }

        // sinPhase: この脚の sin(位相)（正で前へ振る）。swing01: この脚の cos(位相) の正の部分。
        // 脚が前へ振り出している間（腿が真下を通る付近）に 1 となり、膝を最も曲げる。
        private static void DriveLeg(Bone? up, Bone? leg, Bone? foot, Quaternion rootRot,
                                     float sinPhase, float swing01, float w, Quaternion accHips)
        {
            float thighFwd = LegSwingDeg * w * sinPhase;
            float knee = w * (KneeStanceDeg + KneeSwingDeg * swing01 * swing01);

            Quaternion accUp = Drive(up, rootRot, Swing(thighFwd), accHips);
            Quaternion accLeg = Drive(leg, rootRot, Swing(-knee), accUp);   // 膝は後ろへ曲がる

            // 足首: 腿と膝の傾きを打ち消して足裏を水平に保つ。後ろ脚で蹴り出し、前脚で踵から着く。
            float n = sinPhase;
            float toe = w * (ToeOffDeg * Mathf.Max(0f, -n) - HeelStrikeDeg * Mathf.Max(0f, n));
            Drive(foot, rootRot, Pitch(thighFwd - knee + toe), accLeg);
        }

        // 累積回転 acc = own * accParent を作り、world 回転 = root * acc * bind を入れる。bone が null でも acc は返す。
        private static Quaternion Drive(Bone? b, Quaternion rootRot, Quaternion own, Quaternion accParent)
        {
            Quaternion acc = own * accParent;
            if (b != null) b.T.rotation = rootRot * (acc * b.RelBind);
            return acc;
        }

        // ルート空間 +X（右）まわり。正で、下がっている肢は後ろへ、直立した体は前へ倒れ、つま先は下がる。
        private static Quaternion Pitch(float deg) => Quaternion.AngleAxis(deg, Vector3.right);

        // 下がっている肢を前方へ deg 度振る（Pitch の逆向き）。
        private static Quaternion Swing(float forwardDeg) => Quaternion.AngleAxis(-forwardDeg, Vector3.right);

        // ルート空間 +Y まわり。正で右回り（上から見て時計回り）。
        private static Quaternion Yaw(float deg) => Quaternion.AngleAxis(deg, Vector3.up);
    }
}
