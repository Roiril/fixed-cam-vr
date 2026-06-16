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

            // bind 補正を確定（配置後の各 bone のワールド向きを席空間へ）
            Quaternion seatInv = Quaternion.Inverse(seat.rotation);
            _headB = _head != null ? seatInv * _head.rotation : Quaternion.identity;
            _lHandB = _lHand != null ? seatInv * _lHand.rotation : Quaternion.identity;
            _rHandB = _rHand != null ? seatInv * _rHand.rotation : Quaternion.identity;
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
            // 累積を避けるため毎回ベース姿勢から解く（解析 IK は後乗算）
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
        /// 座位の固定ポーズ。Mixamo bind は T ポーズ。脚を曲げて着座させ、腕は IK の縮退回避に軽く曲げておく。
        /// ※ Mixamo の bone ローカル軸は直感的でないため、値は preview スクショで詰める前提の初期値。
        /// </summary>
        private void ApplySeatedPose()
        {
            // 脚（股 ~90° 前・膝 ~90° 曲げ・足を水平に）。軸/符号は要 preview 調整
            SetLocalEuler("mixamorig:LeftUpLeg", new Vector3(90f, 0f, 0f));
            SetLocalEuler("mixamorig:RightUpLeg", new Vector3(90f, 0f, 0f));
            SetLocalEuler("mixamorig:LeftLeg", new Vector3(-95f, 0f, 0f));
            SetLocalEuler("mixamorig:RightLeg", new Vector3(-95f, 0f, 0f));
            SetLocalEuler("mixamorig:LeftFoot", new Vector3(20f, 0f, 0f));
            SetLocalEuler("mixamorig:RightFoot", new Vector3(20f, 0f, 0f));

            // 前腕を軽く曲げて IK の axis0 縮退を防ぐ（IK が毎フレ上書きするので向きは何でもよい）
            RotateLocal(_lFore, Quaternion.Euler(0f, 25f, 0f));
            RotateLocal(_rFore, Quaternion.Euler(0f, -25f, 0f));
        }

        private void SetLocalEuler(string boneName, Vector3 deltaEuler)
        {
            var t = Find(boneName);
            if (t != null) t.localRotation = t.localRotation * Quaternion.Euler(deltaEuler);
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
