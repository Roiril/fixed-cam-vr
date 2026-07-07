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

        // 手首写像 W: 受信 wristRot は「OVR 手アンカー」の向き（identity=掌上・指-X、白手 bind と同一）。
        // Remy の手ボーン軸はこれと無関係なので、bind 時の実ジオメトリ（手首→中指方向・手の甲法線）から
        // 「ボーンローカル → アンカーローカル」の回転 W を構築し hand.rotation = seat * wristRot * W とする。
        // 定数の当て推量（Euler オフセット）はしない — 2026-07-07 に Z180 仮説が校正プレビューで棄却された。
        private readonly Quaternion _lHandW, _rHandW;

        // 腕の座位ベース localRotation。解析 IK は後乗算で累積するため、毎フレ解く前にここへ戻す
        private readonly Quaternion _lArmBase, _lForeBase, _rArmBase, _rForeBase;

        // 指リターゲット（P3）: OVR legacy BoneId → Mixamo 指ボーン。
        // 対応が無い OVR bone（thumb0=大菱形骨, pinky0=中手骨, forearm_stub 等）は null で無視。
        // mixBind は構築時（bind ポーズ）の localRotation。HandRetarget.Solve でバインド差分リターゲット
        private readonly Transform?[] _lFingers = new Transform?[AvatarPose.BonesPerHand];
        private readonly Transform?[] _rFingers = new Transform?[AvatarPose.BonesPerHand];
        private readonly Quaternion[] _lFingerBind = new Quaternion[AvatarPose.BonesPerHand];
        private readonly Quaternion[] _rFingerBind = new Quaternion[AvatarPose.BonesPerHand];

        /// <param name="firstPerson">一人称自己アバター用。頭ボーンを潰して視界を塞がず、
        /// 胴が前傾でカメラに食い込まないよう root を後方へずらす。駆動（Drive）はローカル pose で呼ぶ。</param>
        public RemyAvatarRig(Transform seat, GameObject prefab, bool firstPerson = false)
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

            // 指ボーンのマップと bind localRotation を確保（bind ポーズのうちに取ること —
            // ApplySeatedPose/RestPose は腕までしか触らないが、順序依存を作らないようここで確定）
            MapFingers(isLeft: true, _lFingers, _lFingerBind);
            MapFingers(isLeft: false, _rFingers, _rFingerBind);

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
            // 手首写像 W（bind の実ジオメトリから構築。腕/指がまだ bind のこの時点で測る）
            _lHandW = ComputeWristMap(_lHand, _lFingers, right: false, seat, _lHandB);
            _rHandW = ComputeWristMap(_rHand, _rFingers, right: true, seat, _rHandB);

            // 初期＝休めポーズを適用。トラッキング前/ロスト中の腕が T 字（真横・手が外向き）で固まるのを防ぐ。
            // 受信 pose が来れば Drive が上書きする。bind 補正確定後に呼ぶこと（handB が bind 基準）
            ApplyRestPose();

            if (firstPerson) ApplyFirstPersonView(seat);
        }

        /// <summary>
        /// 一人称自己アバター化: 頭ボーンを潰して顔/髪/頭蓋が一人称カメラ（頭位置）を塞がないようにし、
        /// 前傾時に胸がニアクリップを貫かないよう body を数 cm 後方へずらす。頭は Drive で回り続けるが不可視。
        /// </summary>
        private void ApplyFirstPersonView(Transform seat)
        {
            if (_head != null)
            {
                // 頭に skin された頂点を頭ボーン原点へ収縮させる（VR 自己アバターの定番）。
                // 完全 0 は行列縮退で警告が出る環境があるため極小値
                _head.localScale = Vector3.one * 0.01f;
            }
            // body を席ローカル -Z（後方）へ 4cm ずらす（前傾時に胸がニアクリップを貫くのを防ぐ）。
            // _root は seat の子なので localPosition は席ローカル。潰した頭は視界に影響しない
            _root.localPosition += new Vector3(0f, 0f, -0.04f);
        }

        /// <summary>卓上に手を置く自然な座位の休めポーズ（前方やや下・指=前/手のひら=下）。未トラッキング初期姿勢。</summary>
        private static readonly Vector3 RestWristL = new(-0.20f, -0.44f, 0.30f);
        private static readonly Vector3 RestWristR = new(0.20f, -0.44f, 0.30f);
        private static readonly Quaternion RestWristRotL = Quaternion.Euler(12f, 90f, 0f);
        private static readonly Quaternion RestWristRotR = Quaternion.Euler(12f, -90f, 0f);

        /// <summary>両腕を休めポーズへ（IK で手首を卓上へ・手首向きを前方へ）。構築時の初期姿勢に使う。</summary>
        public void ApplyRestPose()
        {
            SolveArm(true, RestWristL, RestWristRotL, true, _lArm, _lFore, _lHand, _lArmBase, _lForeBase);
            SolveArm(false, RestWristR, RestWristRotR, true, _rArm, _rFore, _rHand, _rArmBase, _rForeBase);
        }

        /// <summary>受信 pose を反映（頭・腕 IK・手首向き・指リターゲット）。ロスト手は最後の姿勢で凍結。
        /// layoutL/R は送信元本人の手 bind（RemoteAvatarView.ResolveLayout）。null なら identity 近似。</summary>
        public void Drive(AvatarPose t, HandSkeletonLayout? layoutL = null, HandSkeletonLayout? layoutR = null)
        {
            if (_head != null)
            {
                _head.rotation = _seat.rotation * t.HeadRot * _headB;
            }
            SolveArm(true, t.WristPosL, t.WristRotL, t.TrackedL, _lArm, _lFore, _lHand, _lArmBase, _lForeBase);
            SolveArm(false, t.WristPosR, t.WristRotR, t.TrackedR, _rArm, _rFore, _rHand, _rArmBase, _rForeBase);
            if (t.TrackedL) DriveFingers(t.BonesL, _lFingers, _lFingerBind, layoutL ?? HandSkeletonLayout.CapturedL);
            if (t.TrackedR) DriveFingers(t.BonesR, _rFingers, _rFingerBind, layoutR ?? HandSkeletonLayout.CapturedR);

            // 実行時診断（WireTap 記録中のみ・1Hz）: 手首写像 W が実機データでも成立しているかを
            // 「wristRot から期待される指/甲方向」vs「実際にメッシュへ適用された方向」の角度差で刻む。
            // fAngle/bAngle が小さければ手首向きの数式は正しく、残る違和感は位置・指・体格側と切り分けられる
            if (WireTapRecorder.DiagnosticsEnabled && t.TrackedR && Time.unscaledTime >= _nextDiag)
            {
                _nextDiag = Time.unscaledTime + 1f;
                var hand = _rHand;
                var mid = _rFingers[9];
                var idx = _rFingers[6];
                var pnk = _rFingers[16];
                if (hand != null && mid != null)
                {
                    Quaternion seatInv = Quaternion.Inverse(_seat.rotation);
                    Vector3 fActual = seatInv * (mid.position - hand.position).normalized;
                    Vector3 fExpect = t.WristRotR * Vector3.right; // アンカー実測: 右手 identity=指+X
                    float fAngle = Vector3.Angle(fActual, fExpect);
                    float bAngle = -1f;
                    if (idx != null && pnk != null)
                    {
                        Vector3 sV = seatInv * (idx.position - pnk.position);
                        Vector3 bActual = Vector3.Cross(sV, fActual).normalized;
                        Vector3 bExpect = t.WristRotR * Vector3.up; // 甲+Y
                        bAngle = Vector3.Angle(bActual, bExpect);
                    }
                    Vector3 handLocal = _seat.InverseTransformPoint(hand.position);
                    Debug.Log($"[TDV-REMY] R fAngle={fAngle:F0} bAngle={bAngle:F0} " +
                              $"handLocal={handLocal:F2} target={t.WristPosR:F2} " +
                              $"reach={(handLocal - t.WristPosR).magnitude:F3}");
                }
            }
        }

        private float _nextDiag;

        /// <summary>
        /// 同期 bone（OVR legacy ローカル回転）を Mixamo 指ボーンへバインド差分リターゲット。
        /// ovrBind は受信側デバイスの手 layout（正準バインド）。無い環境（PC 観戦等）は identity で
        /// 近似（曲がりの絶対量に若干のオフセットが乗るが動きは伝わる）。
        /// </summary>
        private void DriveFingers(Quaternion[] boneRots, Transform?[] fingers, Quaternion[] mixBind,
            HandSkeletonLayout? layout)
        {
            for (int i = 0; i < fingers.Length; i++)
            {
                var f = fingers[i];
                if (f == null) continue;
                var ovrBind = (layout != null && i < layout.BoneCount)
                    ? layout.BindLocalRot[i] : Quaternion.identity;
                f.localRotation = HandRetarget.Solve(boneRots[i], ovrBind, mixBind[i]);
            }
        }

        /// <summary>
        /// OVR legacy BoneId（HandBoneTable 順）→ Mixamo 指ボーン Transform の対応を確保する。
        /// legacy 24: wrist=0, forearm=1, thumb0..3=2..5, index1..3=6..8, middle1..3=9..11,
        /// ring1..3=12..14, pinky0..3=15..18, tips=19..23。Mixamo は各指 1..3 節のみ
        /// （thumb0/pinky0=中手骨と指先マーカーは対応なし → null で無視）。
        /// </summary>
        private void MapFingers(bool isLeft, Transform?[] fingers, Quaternion[] bind)
        {
            string side = isLeft ? "Left" : "Right";
            (int ovrIdx, string mixName)[] map =
            {
                (3, "HandThumb1"), (4, "HandThumb2"), (5, "HandThumb3"),
                (6, "HandIndex1"), (7, "HandIndex2"), (8, "HandIndex3"),
                (9, "HandMiddle1"), (10, "HandMiddle2"), (11, "HandMiddle3"),
                (12, "HandRing1"), (13, "HandRing2"), (14, "HandRing3"),
                (16, "HandPinky1"), (17, "HandPinky2"), (18, "HandPinky3"),
            };
            int found = 0;
            foreach (var (ovrIdx, mixName) in map)
            {
                var t = Find($"mixamorig:{side}{mixName}");
                fingers[ovrIdx] = t;
                bind[ovrIdx] = t != null ? t.localRotation : Quaternion.identity;
                if (t != null) found++;
            }
            if (found == 0)
            {
                Debug.LogWarning($"[TableDuo] Remy {side} 指ボーンが見つからない（mixamorig:{side}HandThumb1 等）— 指は bind 固定のまま");
            }
        }

        /// <summary>
        /// bind 実ジオメトリから「Remy 手ボーンローカル → OVR アンカーローカル」の回転 W を構築する。
        /// アンカー基準（白手 bind 実測・[[table_duo_l0_desktop_test]] のキービジュアル校正と同じ知見）:
        ///   右手 identity = 指-X・掌上（手の甲=-Y）/ 左手 = 指+X・掌上（ミラー）。
        /// Remy 側は「手首→中指付け根」を指方向、「(index1-pinky1)×指方向」を手の甲法線として測る
        /// （bind=Tポーズで手の甲は上向き、で符号を確定）。指ボーンが見つからない場合は旧式
        /// （seat 基準 bind 直乗せ）へフォールバック。
        /// </summary>
        private static Quaternion ComputeWristMap(Transform? hand, Transform?[] fingers, bool right,
            Transform seat, Quaternion handBFallback)
        {
            if (hand == null) return handBFallback;
            var mid = fingers[9];  // middle1
            var idx = fingers[6];  // index1
            var pnk = fingers[16]; // pinky1
            if (mid == null) return handBFallback;

            Vector3 fW = mid.position - hand.position; // 指方向（world）
            if (fW.sqrMagnitude < 1e-8f) return handBFallback;
            fW.Normalize();
            Vector3 sW = (idx != null && pnk != null) ? (idx.position - pnk.position) : seat.forward;
            Vector3 bW = Vector3.Cross(sW, fW); // 手の甲法線候補
            if (bW.sqrMagnitude < 1e-8f) return handBFallback;
            bW.Normalize();
            if (Vector3.Dot(bW, seat.up) < 0f) bW = -bW; // bind（Tポーズ）では手の甲=上

            // ボーンローカルへ
            Quaternion boneInv = Quaternion.Inverse(hand.rotation);
            Vector3 fL = boneInv * fW;
            Vector3 bL = boneInv * bW;

            // アンカー基準（白手 bind の**実測**・2026-07-07 [TDV-CALIB] でゴーストメッシュ実ボーンから確定）:
            // 右手 identity = 指+X・手の甲+Y（掌下）。左は右のミラー＝指-X・甲+Y。
            // （旧 memory の「指-X・掌上」は両軸とも実測と 180° 逆＝誤りだった）
            Vector3 f0 = right ? Vector3.right : Vector3.left;
            Vector3 b0 = Vector3.up;
            return Quaternion.LookRotation(f0, b0) * Quaternion.Inverse(Quaternion.LookRotation(fL, bL));
        }

        private void SolveArm(bool left, Vector3 wristLocal, Quaternion wristRotLocal, bool tracked,
            Transform? arm, Transform? fore, Transform? hand,
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
            hand.rotation = _seat.rotation * wristRotLocal * (left ? _lHandW : _rHandW);
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
