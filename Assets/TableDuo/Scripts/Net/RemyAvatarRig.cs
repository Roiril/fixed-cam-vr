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

        // 手首写像 W: 受信 wristRot は「OVR 手アンカー」の向きで、アンカーは掌に整列していない
        // （実録画 FK の実測: 右手アンカー空間で指≈(-0.27,-0.57,0.78)・グリップ様に傾いた軸。
        //   ±X/+Y のような綺麗な軸という過去 2 回の仮定はどちらも誤りだった — memory
        //   table_duo_wrist_anchor_basis 参照）。そこで基準を**ハードコードせず**、
        //   ・Remy 側: bind 実ジオメトリ（手首→中指方向・手の甲法線）をボーンローカルで実測
        //   ・アンカー側: 受信 HandSkeletonLayout（送信者の手 bind）を FK して同じ 2 方向を実測
        // し、W = anchorBasis * inv(remyBasis) を毎セッション実データから導出する。
        // layout が届くまでは実録画由来の既定アンカー基準（下の DefaultAnchor*）で近似する。
        private Quaternion _lW, _rW;
        private HandSkeletonLayout? _lWSrc, _rWSrc; // W の導出元 layout（差し替わったら再導出）

        // Remy 手ボーンローカルの実測ジオメトリ（bind 時に確定）
        private readonly Vector3 _lFL, _lBL, _rFL, _rBL;
        private readonly bool _lGeomOk, _rGeomOk;

        // アンカー基準の既定値（実機録画 tdv_handrec_real_20260610.bin の FK 実測。layout 未着時のみ使用）
        private static readonly Vector3 DefaultAnchorF_R = new(-0.27f, -0.57f, 0.78f);
        private static readonly Vector3 DefaultAnchorB_R = new(-0.14f, 0.81f, 0.57f);
        private static readonly Vector3 DefaultAnchorF_L = new(0.27f, -0.57f, 0.78f);   // 右のミラー（未実測）
        private static readonly Vector3 DefaultAnchorB_L = new(0.14f, 0.81f, 0.57f);

        // 現在採用中のアンカー基準（rest ポーズ・診断で参照）
        private Vector3 _lAnchorF = DefaultAnchorF_L, _lAnchorB = DefaultAnchorB_L;
        private Vector3 _rAnchorF = DefaultAnchorF_R, _rAnchorB = DefaultAnchorB_R;

        // 腕の座位ベース localRotation。解析 IK は後乗算で累積するため、毎フレ解く前にここへ戻す
        private readonly Quaternion _lArmBase, _lForeBase, _rArmBase, _rForeBase;

        // ── 上体リーン（HMD 頭位置追従・骨盤ヒンジ）＋ 鎖骨アシスト ＋ 手首ツイスト分散 ──
        // 骨盤ヒンジ: Mixamo 実データ（Sitting Laughing）解析で「自然な着座リーン＝骨盤前傾＋太もも逆回転
        // で足接地維持、背骨はほぼ真っ直ぐ」と判明（背骨曲げ方式は猫背に見えた）。計画:
        // .claude/plans/2026-07-08_remy-hip-hinge-lean.md
        private readonly Transform? _hips, _lUpLeg, _rUpLeg;   // 骨盤 + 太もも（接地補正）
        private readonly Transform? _lShoulder, _rShoulder;   // 鎖骨（Mixamo Shoulder）
        private readonly Transform? _spine0, _spine1, _spine2; // 背骨チェーン（腰は固定）
        private Quaternion _hipsBase, _lUpLegBase, _rUpLegBase;
        private readonly Quaternion _lShoulderBase, _rShoulderBase;
        private readonly Quaternion _spine0Base, _spine1Base, _spine2Base;
        private readonly Quaternion _lRelBind, _rRelBind;      // bind の前腕→手 相対向き（ツイスト基準）
        private readonly float _lArmLen, _rArmLen;             // 上腕+前腕 長（鎖骨アシスト判定）
        private Vector3 _leanTargetOffset;                     // firstPerson の root 後退ぶんの補正
        private const float MaxLeanDeg = 45f;      // 上体リーンの総回転上限
        private const float HipShare = 0.70f;      // 骨盤ヒンジの主動割合
        private const float SpineShare = 0.25f;    // 腰椎（Spine 下段）の追従割合
        private const float ChestShare = 0.05f;    // 胸椎（Spine1/2）の追従割合（ほぼ真っ直ぐ）
        private const float TwistShare = 0.5f;     // 手首ツイストの前腕負担率
        private const float MaxClavicleDeg = 25f;  // 鎖骨アシストの回転上限

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
            _hips = Find("mixamorig:Hips");
            _lUpLeg = Find("mixamorig:LeftUpLeg");
            _rUpLeg = Find("mixamorig:RightUpLeg");
            _lShoulder = Find("mixamorig:LeftShoulder");
            _rShoulder = Find("mixamorig:RightShoulder");
            _spine0 = Find("mixamorig:Spine");
            _spine1 = Find("mixamorig:Spine1");
            _spine2 = Find("mixamorig:Spine2");

            // 手首ツイスト分散用: bind の「前腕→手」相対向き（純 bind のうちに確定）
            _lRelBind = (_lFore != null && _lHand != null)
                ? Quaternion.Inverse(_lFore.rotation) * _lHand.rotation : Quaternion.identity;
            _rRelBind = (_rFore != null && _rHand != null)
                ? Quaternion.Inverse(_rFore.rotation) * _rHand.rotation : Quaternion.identity;
            // 鎖骨アシスト用: 腕の全長（上腕+前腕）
            _lArmLen = (_lArm != null && _lFore != null && _lHand != null)
                ? Vector3.Distance(_lArm.position, _lFore.position) + Vector3.Distance(_lFore.position, _lHand.position) : 0f;
            _rArmLen = (_rArm != null && _rFore != null && _rHand != null)
                ? Vector3.Distance(_rArm.position, _rFore.position) + Vector3.Distance(_rFore.position, _rHand.position) : 0f;

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
            // 上体リーン/鎖骨アシストのベース姿勢（毎フレ・リストア基準）。
            // 骨盤・太ももは ApplySeatedPose が既に座位へ回した後の値を基準にする（上で ApplySeatedPose 済み）
            _hipsBase = _hips != null ? _hips.localRotation : Quaternion.identity;
            _lUpLegBase = _lUpLeg != null ? _lUpLeg.localRotation : Quaternion.identity;
            _rUpLegBase = _rUpLeg != null ? _rUpLeg.localRotation : Quaternion.identity;
            _lShoulderBase = _lShoulder != null ? _lShoulder.localRotation : Quaternion.identity;
            _rShoulderBase = _rShoulder != null ? _rShoulder.localRotation : Quaternion.identity;
            _spine0Base = _spine0 != null ? _spine0.localRotation : Quaternion.identity;
            _spine1Base = _spine1 != null ? _spine1.localRotation : Quaternion.identity;
            _spine2Base = _spine2 != null ? _spine2.localRotation : Quaternion.identity;

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
            // Remy 側の手ジオメトリ（ボーンローカル）を bind のうちに実測 → 既定アンカー基準で W を初期化
            _lGeomOk = TryMeasureRemyHand(_lHand, _lFingers, right: false, out _lFL, out _lBL);
            _rGeomOk = TryMeasureRemyHand(_rHand, _rFingers, right: true, out _rFL, out _rBL);
            _lW = _lGeomOk ? MakeW(_lFL, _lBL, _lAnchorF, _lAnchorB) : _lHandB;
            _rW = _rGeomOk ? MakeW(_rFL, _rBL, _rAnchorF, _rAnchorB) : _rHandB;

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
            // 上体リーンの頭目標も同じだけ後退させる（補正しないと常時 4cm 前傾に引かれる）
            _leanTargetOffset = new Vector3(0f, 0f, -0.04f);
        }

        // 観戦一人称視点で頭を潰した際の元スケール（戻す用）
        private Vector3? _headScaleOrig;

        /// <summary>観戦の一人称視点用: 頭ボーンを潰す/戻す（当人の目にカメラを置くと頭メッシュに埋まるため）。
        /// firstPerson 構築時の頭潰しと同じ手法だが、実行時に外部（SpectatorController）から切替できる。
        /// Drive は頭の rotation しか触らないので scale の潰しは持続する。</summary>
        public void SetHeadCollapsed(bool collapsed)
        {
            if (_head == null) return;
            if (collapsed)
            {
                _headScaleOrig ??= _head.localScale;
                _head.localScale = Vector3.one * 0.01f;
            }
            else if (_headScaleOrig.HasValue)
            {
                _head.localScale = _headScaleOrig.Value;
            }
        }

        /// <summary>卓上に手を置く自然な座位の休めポーズ（前方やや下・指=前/手のひら=下）。未トラッキング初期姿勢。</summary>
        private static readonly Vector3 RestWristL = new(-0.20f, -0.25f, 0.30f);
        private static readonly Vector3 RestWristR = new(0.20f, -0.25f, 0.30f);

        /// <summary>両腕を休めポーズへ（IK で手首を卓上へ・手首向きを前方へ）。構築時の初期姿勢に使う。
        /// wristRot はアンカー基準に依存するため定数ではなく「指=前やや下・甲=上」から逆算する。</summary>
        public void ApplyRestPose()
        {
            SolveArm(true, RestWristL, RestRotFor(_lAnchorF, _lAnchorB), true, _lArm, _lFore, _lHand, _lArmBase, _lForeBase);
            SolveArm(false, RestWristR, RestRotFor(_rAnchorF, _rAnchorB), true, _rArm, _rFore, _rHand, _rArmBase, _rForeBase);
        }

        /// <summary>「指=前方やや下・手の甲=上」になる wristRot をアンカー基準から逆算する。</summary>
        private static Quaternion RestRotFor(Vector3 anchorF, Vector3 anchorB)
        {
            var desiredF = new Vector3(0f, -0.21f, 0.98f); // 指: 前・やや下（旧 Euler 12° 相当）
            return Quaternion.LookRotation(desiredF, Vector3.up)
                * Quaternion.Inverse(Quaternion.LookRotation(anchorF, anchorB));
        }

        /// <summary>受信 pose を反映（頭・腕 IK・手首向き・指リターゲット）。ロスト手は最後の姿勢で凍結。
        /// layoutL/R は送信元本人の手 bind（RemoteAvatarView.ResolveLayout）。null なら identity 近似。</summary>
        public void Drive(AvatarPose t, HandSkeletonLayout? layoutL = null, HandSkeletonLayout? layoutR = null)
        {
            // 上体リーン（骨盤ヒンジ）: 骨盤を前傾＋太もも逆回転で足接地を保ち、頭を HMD 位置へ追わせる。
            // 背骨は僅かにしか曲げない（猫背回避）。肩・腕が骨盤ごと前へ出るのでリーチも改善する。
            // 腕 IK・頭回転より先に解く（肩/頭位置が変わるため）
            ApplyHipHingeLean(t.HeadPos);
            if (_head != null)
            {
                _head.rotation = _seat.rotation * t.HeadRot * _headB;
            }
            // アンカー基準を「layout の骨長 × live bone 回転」の FK で毎フレーム実測して W を導出する。
            // bind ポーズ FK は不可 — 実機ランタイムは bone0(wrist) が bind と違う定回転を持つ
            //（2026-07-07 実測: live bone0 = Y180。bind FK だと手首が 180° ヨー逆になる）。
            // live FK は白手（RemoteHandView）の描画と同じ情報源なので仮定ゼロで一致する
            if (t.TrackedL) UpdateW(left: true, layoutL ?? HandSkeletonLayout.CapturedL, t.BonesL);
            if (t.TrackedR) UpdateW(left: false, layoutR ?? HandSkeletonLayout.CapturedR, t.BonesR);
            SolveArm(true, t.WristPosL, t.WristRotL, t.TrackedL, _lArm, _lFore, _lHand, _lArmBase, _lForeBase);
            SolveArm(false, t.WristPosR, t.WristRotR, t.TrackedR, _rArm, _rFore, _rHand, _rArmBase, _rForeBase);
            if (t.TrackedL) DriveFingers(t.BonesL, _lFingers, _lFingerBind, layoutL ?? HandSkeletonLayout.CapturedL, t.WristRotL, left: true);
            if (t.TrackedR) DriveFingers(t.BonesR, _rFingers, _rFingerBind, layoutR ?? HandSkeletonLayout.CapturedR, t.WristRotR, left: false);

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
                    Vector3 fExpect = t.WristRotR * _rAnchorF; // アンカー基準（layout 導出 or 既定実測値）
                    float fAngle = Vector3.Angle(fActual, fExpect);
                    float bAngle = -1f;
                    if (idx != null && pnk != null)
                    {
                        Vector3 sV = seatInv * (idx.position - pnk.position);
                        Vector3 bActual = Vector3.Cross(sV, fActual).normalized;
                        Vector3 bExpect = t.WristRotR * _rAnchorB;
                        bAngle = Vector3.Angle(bActual, bExpect);
                    }
                    Vector3 handLocal = _seat.InverseTransformPoint(hand.position);
                    Debug.Log($"[TDV-REMY] R fAngle={fAngle:F0} bAngle={bAngle:F0} " +
                              $"layoutW={(_rWSrc != null)} handLocal={handLocal:F2} target={t.WristPosR:F2} " +
                              $"reach={(handLocal - t.WristPosR).magnitude:F3}");
                }
            }
        }

        private float _nextDiag;

        // 各 Mixamo 指ボーン（ovrIdx）の OVR 子ボーン index（layout ParentIndex から構築）。指先の向き先。
        private int[]? _lOvrChild, _rOvrChild;
        private HandSkeletonLayout? _lChildSrc, _rChildSrc;

        /// <summary>
        /// 指を「FK した OVR 関節位置へ Mixamo 指ボーンを向ける」位置ベースで駆動する（軸規約非依存）。
        /// 回転移植（HandRetarget）は OVR/Mixamo のボーンローカル軸が揃っている前提で、素手トラッキングと
        /// コントローラ駆動ハンドポーズで曲げ軸が 90° 違う（2026-07-07 実測: 素手=local X 軸／コントローラ=local Z 軸）
        /// ため横曲がりに破綻する。FK 位置は物理的なのでどちらの規約でも指先が手のひら側へ正しく来る。
        /// layout（骨長）が無い環境（観戦 PC が相手 layout 未受信 等）は回転移植へフォールバック。
        /// </summary>
        private void DriveFingers(Quaternion[] boneRots, Transform?[] fingers, Quaternion[] mixBind,
            HandSkeletonLayout? layout, Quaternion wristRotLocal, bool left)
        {
            // layout 骨長が無ければ位置 FK 不能 → 従来の回転移植で近似
            if (layout == null || layout.BoneCount < 17 || FkLive(layout, boneRots) == 0)
            {
                for (int i = 0; i < fingers.Length; i++)
                {
                    if (fingers[i] == null) continue;
                    var ovrBind = (layout != null && i < layout.BoneCount)
                        ? layout.BindLocalRot[i] : Quaternion.identity;
                    fingers[i]!.localRotation = HandRetarget.Solve(boneRots[i], ovrBind, mixBind[i]);
                }
                return;
            }

            int[] ovrChild = EnsureOvrChild(left, layout);
            // アンカー空間の方向 → 席ワールド方向。W は指親（手）に含まれるので手首向きだけで写せる
            Quaternion anchorToSeat = _seat.rotation * wristRotLocal;

            for (int i = 0; i < fingers.Length; i++)
            {
                var bone = fingers[i];
                if (bone == null || bone.childCount == 0) continue;
                int c = ovrChild[i];
                if (c < 0 || !FkDone[i] || !FkDone[c]) continue;
                Vector3 dirAnchor = FkPos[c] - FkPos[i];
                if (dirAnchor.sqrMagnitude < 1e-10f) continue;
                Vector3 worldDir = anchorToSeat * dirAnchor;
                AimBone(bone, bone.GetChild(0), worldDir); // 末端ボーン(Index4 等)を的に向ける
            }
        }

        /// <summary>layout の ParentIndex から「各 Mixamo 指ボーン(ovrIdx) → その OVR 子ボーン index」を構築。</summary>
        private int[] EnsureOvrChild(bool left, HandSkeletonLayout layout)
        {
            var cache = left ? _lOvrChild : _rOvrChild;
            var src = left ? _lChildSrc : _rChildSrc;
            if (cache != null && ReferenceEquals(src, layout)) return cache;

            var fingers = left ? _lFingers : _rFingers;
            var child = new int[AvatarPose.BonesPerHand];
            for (int i = 0; i < child.Length; i++) child[i] = -1;
            int n = Mathf.Min(layout.BoneCount, AvatarPose.BonesPerHand);
            // 各マップ済み指ボーン i について、parent==i の最初のボーンを子とする（次節 or 指先マーカー）
            for (int i = 0; i < child.Length; i++)
            {
                if (fingers[i] == null) continue;
                for (int j = 0; j < n; j++)
                {
                    if (layout.ParentIndex[j] == i) { child[i] = j; break; }
                }
            }
            if (left) { _lOvrChild = child; _lChildSrc = layout; }
            else { _rOvrChild = child; _rChildSrc = layout; }
            return child;
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

        /// <summary>W = 「Remy 手ボーンローカル → OVR アンカーローカル」の回転。両側とも同じ実測基底
        /// （指方向 f・手の甲法線 b、左手は cross 符号を反転して物理的な甲に統一）から構築する。</summary>
        private static Quaternion MakeW(Vector3 remyF, Vector3 remyB, Vector3 anchorF, Vector3 anchorB)
            => Quaternion.LookRotation(anchorF, anchorB)
               * Quaternion.Inverse(Quaternion.LookRotation(remyF, remyB));

        /// <summary>手首→中指付け根を指方向 f、(index1-pinky1)×f を甲法線 b とする実測基底
        /// （左手は cross の掌性で符号が反転するため -b に統一 = 常に物理的な手の甲）。</summary>
        private static bool TryHandBasis(Vector3 wrist, Vector3? mid, Vector3? idx, Vector3? pnk,
            bool right, out Vector3 f, out Vector3 b)
        {
            f = Vector3.zero; b = Vector3.zero;
            if (mid == null || idx == null || pnk == null) return false;
            f = mid.Value - wrist;
            if (f.sqrMagnitude < 1e-8f) return false;
            f.Normalize();
            b = Vector3.Cross(idx.Value - pnk.Value, f);
            if (b.sqrMagnitude < 1e-8f) return false;
            b.Normalize();
            if (!right) b = -b; // 左手は掌性で cross が掌側を向く → 甲へ統一
            return true;
        }

        /// <summary>Remy 手の bind 実ジオメトリをボーンローカルで実測する（構築時 1 回）。</summary>
        private static bool TryMeasureRemyHand(Transform? hand, Transform?[] fingers, bool right,
            out Vector3 fL, out Vector3 bL)
        {
            fL = Vector3.zero; bL = Vector3.zero;
            if (hand == null) return false;
            var mid = fingers[9]; var idx = fingers[6]; var pnk = fingers[16];
            if (!TryHandBasis(hand.position, mid != null ? mid.position : null,
                    idx != null ? idx.position : null, pnk != null ? pnk.position : null,
                    right, out Vector3 fW, out Vector3 bW))
            {
                return false;
            }
            Quaternion boneInv = Quaternion.Inverse(hand.rotation);
            fL = boneInv * fW;
            bL = boneInv * bW;
            return true;
        }

        /// <summary>layout の骨長 × live bone 回転の FK でアンカー空間の実基底を測り、W を更新する。
        /// layout が無い間は既定実測値のまま。</summary>
        private void UpdateW(bool left, HandSkeletonLayout? layout, Quaternion[] liveRots)
        {
            if (layout == null) return;
            bool geomOk = left ? _lGeomOk : _rGeomOk;
            if (!geomOk) return; // Remy 側ジオメトリ不明なら旧フォールバックのまま

            if (!TryAnchorBasisFromLayout(layout, liveRots, !left, out Vector3 f0, out Vector3 b0)) return;
            if (left)
            {
                _lWSrc = layout; _lAnchorF = f0; _lAnchorB = b0;
                _lW = MakeW(_lFL, _lBL, f0, b0);
            }
            else
            {
                _rWSrc = layout; _rAnchorF = f0; _rAnchorB = b0;
                _rW = MakeW(_rFL, _rBL, f0, b0);
            }
        }

        // FK 作業バッファ（毎フレーム呼ぶので GC ゼロ化）
        private static readonly Vector3[] FkPos = new Vector3[AvatarPose.BonesPerHand];
        private static readonly Quaternion[] FkRot = new Quaternion[AvatarPose.BonesPerHand];
        private static readonly bool[] FkDone = new bool[AvatarPose.BonesPerHand];

        /// <summary>layout（骨長 BindLocalPos・ParentIndex）× live bone 回転をアンカー空間へ FK し、
        /// FkPos/FkDone を埋める。戻り値は解けたボーン数（0=失敗）。ParentIndex は親→子順とは限らないので反復で解く。</summary>
        private static int FkLive(HandSkeletonLayout layout, Quaternion[] liveRots)
        {
            int n = Mathf.Min(layout.BoneCount, Mathf.Min(liveRots.Length, AvatarPose.BonesPerHand));
            if (n < 17) return 0;
            var pos = FkPos; var rot = FkRot; var done = FkDone;
            System.Array.Clear(done, 0, done.Length);
            for (int pass = 0; pass < n; pass++)
            {
                bool progressed = false;
                for (int i = 0; i < n; i++)
                {
                    if (done[i]) continue;
                    int p = layout.ParentIndex[i];
                    if (p < 0 || p >= n)
                    {
                        pos[i] = layout.BindLocalPos[i];
                        rot[i] = liveRots[i];
                        done[i] = true; progressed = true;
                    }
                    else if (done[p])
                    {
                        rot[i] = rot[p] * liveRots[i];
                        pos[i] = pos[p] + rot[p] * layout.BindLocalPos[i];
                        done[i] = true; progressed = true;
                    }
                }
                if (!progressed) break;
            }
            return n;
        }

        /// <summary>FK 結果（FkPos/FkDone）から wrist(0)/index1(6)/middle1(9)/pinky1(16) の実基底を測る。</summary>
        private static bool TryAnchorBasisFromLayout(HandSkeletonLayout layout, Quaternion[] liveRots,
            bool right, out Vector3 f0, out Vector3 b0)
        {
            f0 = Vector3.zero; b0 = Vector3.zero;
            if (layout.BoneCount < 17) return false;
            if (FkLive(layout, liveRots) == 0) return false;
            if (!FkDone[0] || !FkDone[6] || !FkDone[9] || !FkDone[16]) return false;
            return TryHandBasis(FkPos[0], FkPos[9], FkPos[6], FkPos[16], right, out f0, out b0);
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

            // 鎖骨アシスト: 腕長で届かない目標のときだけ、鎖骨を目標方向へ回して肩を数 cm 差し出す
            ApplyClavicleAssist(left, arm, goal);

            // 肘ヒント: 下・外・後ろ（座位で手は前方机上）
            Vector3 poleLocal = new Vector3(left ? -0.6f : 0.6f, -0.7f, -0.5f);
            Vector3 pole = arm.position + _seat.TransformDirection(poleLocal);
            TwoBoneIK.Solve(arm, fore, hand, goal, pole);
            hand.rotation = _seat.rotation * wristRotLocal * (left ? _lW : _rW);

            // 手首ツイスト分散: 手のねじり（回内/回外）の半分を前腕に負わせ、皮膚の絞れを緩和
            DistributeWristTwist(left, fore, hand);
        }

        /// <summary>
        /// 骨盤ヒンジで上体をリーンさせ、頭ボーンを HMD 位置（席ローカル）へ追わせる。
        /// Mixamo 実データ（Sitting Laughing）に倣い、回転の大半を骨盤（Hips）に集中し、太ももを
        /// 逆回転して足の接地を保ち、背骨は僅かにしか曲げない（猫背回避）。毎フレ・ベース姿勢から解き直す。
        /// </summary>
        private void ApplyHipHingeLean(Vector3 headPosLocal)
        {
            if (_hips == null || _head == null) return;
            // ベースへ戻す（回転の累積防止）
            _hips.localRotation = _hipsBase;
            if (_lUpLeg != null) _lUpLeg.localRotation = _lUpLegBase;
            if (_rUpLeg != null) _rUpLeg.localRotation = _rUpLegBase;
            if (_spine0 != null) _spine0.localRotation = _spine0Base;
            if (_spine1 != null) _spine1.localRotation = _spine1Base;
            if (_spine2 != null) _spine2.localRotation = _spine2Base;

            // 頭→目標の回転量を骨盤位置まわりで測る（骨盤が回れば上体全体が付いてくる）
            Vector3 pivot = _hips.position;
            Vector3 cur = _head.position - pivot;
            Vector3 target = _seat.TransformPoint(headPosLocal + _leanTargetOffset) - pivot;
            if (cur.sqrMagnitude < 1e-8f || target.sqrMagnitude < 1e-8f) return;

            Quaternion full = Quaternion.FromToRotation(cur, target);
            full.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            ang = Mathf.Clamp(ang, -MaxLeanDeg, MaxLeanDeg);
            if (Mathf.Abs(ang) < 0.1f || float.IsNaN(axis.x)) return;

            // (1) 骨盤を主動で回す（total の HipShare）。root なので脚も一緒に回る → (2) で補正
            Quaternion hipStep = Quaternion.AngleAxis(ang * HipShare, axis);
            _hips.rotation = hipStep * _hips.rotation;
            // (2) 太ももを骨盤回転の逆で戻し、足の接地（太ももの向き）を保つ＝股関節ヒンジ
            Quaternion hipUndo = Quaternion.Inverse(hipStep);
            if (_lUpLeg != null) _lUpLeg.rotation = hipUndo * _lUpLeg.rotation;
            if (_rUpLeg != null) _rUpLeg.rotation = hipUndo * _rUpLeg.rotation;
            // (3) 背骨は僅かに追従（腰椎 SpineShare・胸椎 ChestShare）。背中はほぼ真っ直ぐのまま
            if (_spine0 != null)
                _spine0.rotation = Quaternion.AngleAxis(ang * SpineShare, axis) * _spine0.rotation;
            if (_spine1 != null)
                _spine1.rotation = Quaternion.AngleAxis(ang * ChestShare, axis) * _spine1.rotation;
            if (_spine2 != null)
                _spine2.rotation = Quaternion.AngleAxis(ang * ChestShare, axis) * _spine2.rotation;
        }

        /// <summary>目標が腕長を超えるときだけ、鎖骨を目標方向へ回して肩（上腕根本）を差し出す。</summary>
        private void ApplyClavicleAssist(bool left, Transform arm, Vector3 goal)
        {
            var shoulder = left ? _lShoulder : _rShoulder;
            float armLen = left ? _lArmLen : _rArmLen;
            if (shoulder == null || armLen <= 0f) return;
            shoulder.localRotation = left ? _lShoulderBase : _rShoulderBase; // 累積防止

            float dist = Vector3.Distance(arm.position, goal);
            float deficit = dist - armLen * 0.95f; // 95% を超えたあたりから効かせ始める
            if (deficit <= 0f) return;

            Vector3 curDir = arm.position - shoulder.position;
            Vector3 wantDir = goal - shoulder.position;
            if (curDir.sqrMagnitude < 1e-8f || wantDir.sqrMagnitude < 1e-8f) return;

            // 不足量に比例して効かせる（15cm 不足で最大角）
            float w = Mathf.Clamp01(deficit / 0.15f);
            Quaternion full = Quaternion.FromToRotation(curDir, wantDir);
            full.ToAngleAxis(out float ang, out Vector3 axis);
            if (ang > 180f) ang -= 360f;
            ang = Mathf.Clamp(ang * w, -MaxClavicleDeg, MaxClavicleDeg);
            if (Mathf.Abs(ang) < 0.1f || float.IsNaN(axis.x)) return;
            shoulder.rotation = Quaternion.AngleAxis(ang, axis) * shoulder.rotation;
        }

        /// <summary>手首のねじり（bind 相対の前腕軸まわりツイスト成分）の一部を前腕へ移す。
        /// 手のワールド向きは変えない（前腕を軸回転→手を再固定）ので見た目のねじれだけが分散する。</summary>
        private void DistributeWristTwist(bool left, Transform fore, Transform hand)
        {
            Quaternion handWorld = hand.rotation;
            Vector3 axisWorld = hand.position - fore.position;
            if (axisWorld.sqrMagnitude < 1e-8f) return;
            Vector3 aL = (Quaternion.Inverse(fore.rotation) * axisWorld).normalized;

            // bind 相対からのねじれ量（前腕ローカル）
            Quaternion rel = Quaternion.Inverse(fore.rotation) * handWorld;
            Quaternion d = rel * Quaternion.Inverse(left ? _lRelBind : _rRelBind);
            if (d.w < 0f) d = new Quaternion(-d.x, -d.y, -d.z, -d.w);

            // swing-twist 分解: 回転ベクトル成分を軸へ射影した成分がツイスト
            Vector3 v = new(d.x, d.y, d.z);
            Vector3 proj = Vector3.Dot(v, aL) * aL;
            var twist = new Quaternion(proj.x, proj.y, proj.z, d.w);
            float m = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y + twist.z * twist.z + twist.w * twist.w);
            if (m < 1e-6f) return;
            twist = new Quaternion(twist.x / m, twist.y / m, twist.z / m, twist.w / m);

            // 前腕にツイストの一部を負わせ、手はワールド向きへ再固定。
            // 軸は前腕→手（手首関節）を通るので手の位置は動かない
            Quaternion share = Quaternion.Slerp(Quaternion.identity, twist, TwistShare);
            fore.rotation = fore.rotation * share;
            hand.rotation = handWorld;
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
