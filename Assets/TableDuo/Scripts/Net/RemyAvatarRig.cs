#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 人側フルアバター = Mixamo Remy をトラッキングで駆動する。
    /// 追跡は頭＋手首＋指のみなので: 体幹/脚は固定の座位ポーズ、頭は受信回転で駆動、
    /// 腕は肩固定の 2 ボーン IK で手首ゴールへ、手首向きは受信回転、指は受信 bone をリターゲット（P3）。
    /// 受信 pose（席ローカル）→ ワールドは seat フレームで変換する。
    /// </summary>
    public sealed class RemyAvatarRig
    {
        private readonly Transform _seat;   // RemoteAvatarView（席フレーム＝pose のローカル基準）
        private readonly Transform _root;   // Remy インスタンス root

        private readonly Transform? _head;
        private readonly Transform? _lArm, _lFore, _lHand;
        private readonly Transform? _rArm, _rFore, _rHand;

        // bind 補正（席空間）: boneWorld = seat.rotation * receivedRot * B のとき received=I で bind に戻る
        private readonly Quaternion _headB, _lHandB, _rHandB;

        // 腕の座位ベース localRotation。解析 IK は後乗算で累積するため、毎フレ解く前にここへ戻す
        private readonly Quaternion _lArmBase, _lForeBase, _rArmBase, _rForeBase;

        public RemyAvatarRig(Transform seat, GameObject prefab)
        {
            _seat = seat;
            var go = Object.Instantiate(prefab, seat, worldPositionStays: false);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.SetActive(true);
            _root = go.transform;

            // 画面外/エディタ手動レンダーでもボーン姿勢に追従して再スキンさせる
            foreach (var smr in go.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
            }

            _head = Find("mixamorig:Head");
            _lArm = Find("mixamorig:LeftArm");
            _lFore = Find("mixamorig:LeftForeArm");
            _lHand = Find("mixamorig:LeftHand");
            _rArm = Find("mixamorig:RightArm");
            _rFore = Find("mixamorig:RightForeArm");
            _rHand = Find("mixamorig:RightHand");

            ApplySeatedPose();

            // 腕ベース姿勢を記録（IK の毎フレ・リストア基準）
            _lArmBase = _lArm != null ? _lArm.localRotation : Quaternion.identity;
            _lForeBase = _lFore != null ? _lFore.localRotation : Quaternion.identity;
            _rArmBase = _rArm != null ? _rArm.localRotation : Quaternion.identity;
            _rForeBase = _rFore != null ? _rFore.localRotation : Quaternion.identity;

            // 頭が席原点（目線アンカー）に来るよう root を下げる（座位ポーズ適用後の頭位置で計算）
            if (_head != null)
            {
                Vector3 headLocal = seat.InverseTransformPoint(_head.position);
                _root.localPosition -= headLocal;
            }

            // bind 補正を確定（配置後の各 bone のワールド向きを席空間へ）。手はまだ bind 向き（外向き）なのでここで確定
            Quaternion seatInv = Quaternion.Inverse(seat.rotation);
            _headB = _head != null ? seatInv * _head.rotation : Quaternion.identity;
            _lHandB = _lHand != null ? seatInv * _lHand.rotation : Quaternion.identity;
            _rHandB = _rHand != null ? seatInv * _rHand.rotation : Quaternion.identity;

            // 初期＝休めポーズを適用。トラッキング前/ロスト中の腕が T 字（真横・手が外向き）で固まるのを防ぐ。
            // 受信 pose が来れば Drive が上書きする。bind 補正確定後に呼ぶこと（handB が bind 基準）
            ApplyRestPose();
        }

        /// <summary>卓上に手を置く自然な座位の休めポーズ（前方やや下・指=前/手のひら=下）。未トラッキング初期姿勢。</summary>
        private static readonly Vector3 RestWristL = new(-0.20f, -0.44f, 0.30f);
        private static readonly Vector3 RestWristR = new(0.20f, -0.44f, 0.30f);
        private static readonly Quaternion RestWristRotL = Quaternion.Euler(12f, 90f, 0f);
        private static readonly Quaternion RestWristRotR = Quaternion.Euler(12f, -90f, 0f);

        /// <summary>両腕を休めポーズへ（IK で手首を卓上へ・手首向きを前方へ）。構築時の初期姿勢に使う。</summary>
        public void ApplyRestPose()
        {
            SolveArm(true, RestWristL, RestWristRotL, true, _lArm, _lFore, _lHand, _lHandB, _lArmBase, _lForeBase);
            SolveArm(false, RestWristR, RestWristRotR, true, _rArm, _rFore, _rHand, _rHandB, _rArmBase, _rForeBase);
        }

        /// <summary>受信 pose を反映（頭・腕 IK・手首向き）。ロスト手は最後の姿勢で凍結。</summary>
        public void Drive(AvatarPose t)
        {
            if (_head != null)
            {
                _head.rotation = _seat.rotation * t.HeadRot * _headB;
            }
            SolveArm(true, t.WristPosL, t.WristRotL, t.TrackedL, _lArm, _lFore, _lHand, _lHandB, _lArmBase, _lForeBase);
            SolveArm(false, t.WristPosR, t.WristRotR, t.TrackedR, _rArm, _rFore, _rHand, _rHandB, _rArmBase, _rForeBase);
        }

        private void SolveArm(bool left, Vector3 wristLocal, Quaternion wristRotLocal, bool tracked,
            Transform? arm, Transform? fore, Transform? hand, Quaternion handB,
            Quaternion armBase, Quaternion foreBase)
        {
            if (!tracked || arm == null || fore == null || hand == null) return; // ロスト=凍結
            // 累積を避けるため毎回ベース姿勢から解き直す（IK は現姿勢からの相対解）
            arm.localRotation = armBase;
            fore.localRotation = foreBase;
            Vector3 goal = _seat.TransformPoint(wristLocal);
            // 肘ヒント: 下・外・後ろ（座位で手は前方机上）
            Vector3 poleLocal = new Vector3(left ? -0.6f : 0.6f, -0.7f, -0.5f);
            Vector3 pole = arm.position + _seat.TransformDirection(poleLocal);
            TwoBoneIK.Solve(arm, fore, hand, goal, pole);
            hand.rotation = _seat.rotation * wristRotLocal * handB;
        }

        /// <summary>
        /// 座位の固定ポーズ。bind は T ポーズ（直立）。下半身を「各ボーンを目標ワールド方向へ向ける」
        /// 方式で着座させる（Mixamo のローカル軸に依存しない＝当て推量を排除）。
        /// 太もも≒水平前方・すね≒真下・足≒床に水平。親→子の順に向けるので子の更新位置で解ける。
        /// </summary>
        private void ApplySeatedPose()
        {
            SeatLeg("mixamorig:LeftUpLeg", "mixamorig:LeftLeg", "mixamorig:LeftFoot", "mixamorig:LeftToeBase", -1f);
            SeatLeg("mixamorig:RightUpLeg", "mixamorig:RightLeg", "mixamorig:RightFoot", "mixamorig:RightToeBase", +1f);

            // 前腕を軽く曲げて IK 初期姿勢の縮退（肩-手首が一直線）を防ぐ。IK が毎フレ base から解き直すので向きは大まかでよい
            RotateLocal(_lFore, Quaternion.Euler(0f, 25f, 0f));
            RotateLocal(_rFore, Quaternion.Euler(0f, -25f, 0f));
        }

        /// <summary>片脚を座位へ。side: アバター左=-1 / 右=+1（太ももを左右へ少し開く）。</summary>
        private void SeatLeg(string upLegN, string legN, string footN, string toeN, float side)
        {
            var upLeg = Find(upLegN);
            var leg = Find(legN);
            var foot = Find(footN);
            var toe = Find(toeN);
            Vector3 fwd = _root.forward, up = _root.up, right = _root.right;
            // 太もも: ほぼ水平前方（わずかに下げ・外へ開く）
            AimBone(upLeg, leg, fwd - up * 0.15f + right * (side * 0.12f));
            // すね: 真下（わずかに前）
            AimBone(leg, foot, -up + fwd * 0.05f);
            // 足: 前方・床に水平
            AimBone(foot, toe, fwd - up * 0.05f);
        }

        /// <summary>bone→child のワールド方向が worldDir に向くよう bone をワールド回転する（ローカル軸に非依存）。</summary>
        private static void AimBone(Transform? bone, Transform? child, Vector3 worldDir)
        {
            if (bone == null || child == null) return;
            Vector3 cur = child.position - bone.position;
            if (cur.sqrMagnitude < 1e-8f || worldDir.sqrMagnitude < 1e-8f) return;
            bone.rotation = Quaternion.FromToRotation(cur.normalized, worldDir.normalized) * bone.rotation;
        }

        private static void RotateLocal(Transform? t, Quaternion delta)
        {
            if (t != null) t.localRotation = t.localRotation * delta;
        }

        private Transform? Find(string name) => FindRecursive(_root, name);

        private static Transform? FindRecursive(Transform root, string name)
        {
            if (root.name == name) return root;
            for (int i = 0; i < root.childCount; i++)
            {
                var f = FindRecursive(root.GetChild(i), name);
                if (f != null) return f;
            }
            return null;
        }
    }
}
