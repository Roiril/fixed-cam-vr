#nullable enable
using TableDuoVr.Hands;
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」のペン（marker_circle / marker_segment）・消しゴム（BEAR_eraser）を保持中、
    /// <b>位置と回転を両方</b>確定するサイドカー。旧 MarkerHoldTilt（回転のみ・手の高さで俯角を決める帯）を置換。
    ///
    /// 設計（VR 一般解へ寄せる）: 手首 pose と「ピンチ点」（親指先・人差し指先の中点、HandLandmarks FK）から
    /// ペンの前方を作り（手のピッチ・ヨー・ひねりに自然応答）、指先近くを軸に回す。面クランプでペン先が
    /// 紙を貫かない。ピンチ点は One Euro フィルタ、前方は指数 slerp で平滑（<see cref="PenGripLogic"/> が純計算）。
    ///
    /// 適用（旧 MarkerHoldTilt と同じ）: サーバ=保持中つねに / 非サーバ=保持者本人のみ楽観上書き。
    /// <c>DefaultExecutionOrder(120)</c> + LateUpdate で Grabbable.Update / PinchGrabInteractor.LateUpdate の
    /// <b>後</b>に必ず勝つ。ペンは physics:false なのでリリース後は確定姿勢のまま静止する。
    ///
    /// 手 pose の取得経路:
    /// - サーバ: <see cref="Grabbable"/>.HolderSeat + <see cref="ConnectionManager"/>.TryGetPose/GetHandLayout
    ///   （リモート手も本人の layout で FK。HandSkeletonLayout.CapturedL/R はローカル手専用なので使わない）
    /// - 保持者ローカル: <see cref="HandPoseSourceRegistry"/>.Best（最新トラッキング）+ PinchGrabInteractor.LocalSeat
    ///   + HandSkeletonLayout.CapturedL/R（＝ローカル手 = 正）
    /// FK が取れない（layout/bones 不在・トラッキングロスト）ときは上書きせず、既存の
    /// Grabbable / PinchGrabInteractor の手首相対追従（＝旧来の掴み式）へフォールバックする。
    ///
    /// 値（mode / tipDistance / tableTopY / padTransform）は TableDuoSceneSetup が SerializedObject で焼く。
    /// 調整定数（GripBack/ExtraPitch/しきい値/フィルタ係数）は const（シーン YAML 焼き付きで 0 に化ける罠を回避）。
    /// </summary>
    [DefaultExecutionOrder(120)]
    public sealed class ToolGripDriver : NetworkBehaviour
    {
        public enum Mode
        {
            Pen,  // ペン: 手首→ピンチ線から俯角を作りペン先で描く
            Flat, // 消しゴム: yaw のみ手追従・底面を面近くに置く
        }

        // --- 調整定数（要実機調整。SerializeField にしない） ---
        private const float GripBack = 0.045f;      // ピンチ点からペン先までの前方距離（指先の 4.5cm 先＝ペン先を指より突き出す）
        private const float ExtraPitchDeg = 15f;    // 手首→ピンチ線からさらに下へ倒す俯角
        private const float MaxDownDeg = 80f;        // 下向き成分の上限（LookRotation 縮退回避）
        private const float HoldDropM = 0.02f;       // フラットモードでピンチ点の下 2cm を底面に置く
        private const float DirSlerpRate = 20f;      // 前方 dir の指数 slerp レート（大＝速い追従）
        private const float EuroMinCutoff = 1.5f;    // One Euro 最小 cutoff（Hz）
        private const float EuroBeta = 15f;          // One Euro 速度ゲイン（m/s 単位の微分に掛かる → 数十が実用域）
        private const float EuroDCutoff = 1f;        // One Euro 微分 cutoff（Hz）

        [Tooltip("Pen=ペン（俯角付き）/ Flat=消しゴム（yaw のみ）")]
        [SerializeField] private Mode mode = Mode.Pen;
        [Tooltip("ペン原点→ペン先距離（m）。scale 1.3 の marker で 0.0923。消しゴムは 0（原点=底面）")]
        [SerializeField] private float tipDistance = 0.0923f;
        [Tooltip("天板上面 Y（=topY）。パッドの外なら面高はこれ")]
        [SerializeField] private float tableTopY;
        [Tooltip("描画パッド（BEAR_pad）。この上にペン先があるときは面高が tableTopY+パッド上面ぶん上がる")]
        [SerializeField] private Transform? padTransform;

        private Grabbable? _grab;
        private bool _wasHeld;   // サーバのリリースエッジ検知用
        private bool _applying;  // 適用中か（開始エッジで平滑状態をリセット）
        private bool _dirInit;   // _smoothDir をシードしたか
        private Vector3 _smoothDir = Vector3.forward;
        private readonly OneEuroFilter _pinchFilter = new(EuroMinCutoff, EuroBeta, EuroDCutoff);
        private readonly Vector3[] _lm = new Vector3[HandLandmarks.Count];

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            if (_grab == null)
                Debug.LogWarning($"[TableDuo] ToolGripDriver on {name}: Grabbable がありません（グリップ無効）");
        }

        private void LateUpdate()
        {
            if (_grab == null) return;

            // サーバ: 保持解放エッジでツールを寝かせて置く（heading 維持・pitch 0・底面を面へ接地）
            if (IsServer)
            {
                bool held = _grab.IsHeld;
                if (!held && _wasHeld) LayFlatOnRelease();
                _wasHeld = held;
            }

            // 適用条件: サーバは保持中つねに / 非サーバは「保持者本人」だけ（楽観表示）
            var nm = NetworkManager.Singleton;
            bool apply = IsServer
                ? _grab.IsHeld
                : (_grab.IsHeld && nm != null && _grab.HolderClientId == nm.LocalClientId);
            if (!apply)
            {
                _applying = false;
                return;
            }

            // 手 pose とピンチ点を解決。取れない or ピンチ FK 不能ならフォールバック（既存追従に委ねる）
            if (!ResolveHandPose(out Vector3 wristWorld, out Vector3 pinchWorld, out bool pinchValid) || !pinchValid)
            {
                _applying = false; // 次に取れた時に平滑を張り直す
                return;
            }

            if (!_applying)
            {
                _pinchFilter.Reset();
                _dirInit = false;
                _applying = true;
            }

            float dt = Time.deltaTime;
            Vector3 pinchF = _pinchFilter.Filter(pinchWorld, dt);
            if (mode == Mode.Pen) ApplyPen(wristWorld, pinchF, dt);
            else ApplyFlat(wristWorld, pinchF, dt);
        }

        private void ApplyPen(Vector3 wristWorld, Vector3 pinchF, float dt)
        {
            Vector3 dir = PenGripLogic.ComputePenDir(
                wristWorld, pinchF, ExtraPitchDeg, MaxDownDeg, _smoothDir, out _);
            SmoothDir(dir, dt);

            Vector3 tipXZ = pinchF + _smoothDir * GripBack;      // 面高はペン先 XZ で評価（パッド境界の精度）
            float surfaceY = SurfaceYAt(tipXZ);
            Vector3 pos = PenGripLogic.ComposeTipPose(pinchF, _smoothDir, GripBack, tipDistance, surfaceY, out _);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(_smoothDir, Vector3.up));
        }

        private void ApplyFlat(Vector3 wristWorld, Vector3 pinchF, float dt)
        {
            float surfaceY = SurfaceYAt(pinchF);
            Vector3 pos = PenGripLogic.ComposeFlatPose(wristWorld, pinchF, HoldDropM, surfaceY, _smoothDir, out Vector3 fwd);
            SmoothDir(fwd, dt);
            transform.SetPositionAndRotation(pos, Quaternion.LookRotation(_smoothDir, Vector3.up));
        }

        /// <summary>前方 dir を指数 slerp（1-exp(-rate·dt)）で追従。開始直後はシードして跳ねを防ぐ。</summary>
        private void SmoothDir(Vector3 dir, float dt)
        {
            if (!_dirInit)
            {
                _smoothDir = dir;
                _dirInit = true;
                return;
            }
            float k = 1f - Mathf.Exp(-DirSlerpRate * Mathf.Max(dt, 0f));
            var s = Vector3.Slerp(_smoothDir, dir, k);
            if (s.sqrMagnitude > 1e-8f) _smoothDir = s.normalized;
        }

        /// <summary>解放時（サーバのみ）: heading を保ったまま pitch 0（寝かせる）+ 原点 y を面高へ接地。</summary>
        private void LayFlatOnRelease()
        {
            Vector3 f = _dirInit ? _smoothDir : transform.forward;
            f.y = 0f;
            Vector3 heading = f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
            var p = transform.position;
            p.y = SurfaceYAt(p); // 原点＝底面なので y を面高に載せると接地する。XZ は触らない
            transform.SetPositionAndRotation(p, Quaternion.LookRotation(heading, Vector3.up));
        }

        /// <summary>保持者の手首ワールド pose とピンチ点を解決する。pinchValid=ピンチ点 FK が成立したか。</summary>
        private bool ResolveHandPose(out Vector3 wristWorld, out Vector3 pinchWorld, out bool pinchValid)
        {
            wristWorld = default;
            pinchWorld = default;
            pinchValid = false;
            if (_grab == null) return false;

            bool right = _grab.HolderHand == 1;

            if (IsServer)
            {
                var cm = ConnectionManager.Instance;
                Transform? seat = _grab.HolderSeat;
                if (cm == null || seat == null) return false;
                if (!cm.TryGetPose(_grab.HolderClientId, out AvatarPose pose)) return false;
                var layout = cm.GetHandLayout(_grab.HolderClientId, right);
                return FromPose(pose, layout, seat, right, out wristWorld, out pinchWorld, out pinchValid);
            }
            else
            {
                var src = HandPoseSourceRegistry.Best;
                Transform? seat = PinchGrabInteractor.LocalSeat;
                if (src == null || !src.IsValid || seat == null) return false;
                var layout = right ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL; // ローカル手 = 正
                return FromPose(src.Current, layout, seat, right, out wristWorld, out pinchWorld, out pinchValid);
            }
        }

        // 席ローカル pose + layout から手首ワールド pose とワールドピンチ点を作る。
        // HandLandmarks.Compute は wrist/rot を渡した座標系で landmark を返す（席ローカル）→ TransformPoint で world 化。
        private bool FromPose(AvatarPose pose, HandSkeletonLayout? layout, Transform seat, bool right,
            out Vector3 wristWorld, out Vector3 pinchWorld, out bool pinchValid)
        {
            wristWorld = default;
            pinchWorld = default;
            pinchValid = false;

            bool tracked = right ? pose.TrackedR : pose.TrackedL;
            if (!tracked) return false;

            Vector3 wristLocal = right ? pose.WristPosR : pose.WristPosL;
            Quaternion wristRot = right ? pose.WristRotR : pose.WristRotL;
            var bones = right ? pose.BonesR : pose.BonesL;
            wristWorld = seat.TransformPoint(wristLocal);

            if (HandLandmarks.Compute(layout, wristLocal, wristRot, bones, _lm))
            {
                Vector3 pinchLocal = (_lm[2] + _lm[3]) * 0.5f; // thumbTip + indexTip
                pinchWorld = seat.TransformPoint(pinchLocal);
                pinchValid = true;
            }
            return true;
        }

        /// <summary>指定ワールド位置での面高。パッドのローカル XZ 内なら天板＋パッド上面、外なら天板。</summary>
        private float SurfaceYAt(Vector3 worldPos)
        {
            if (padTransform != null)
            {
                Vector3 local = padTransform.InverseTransformPoint(worldPos);
                if (Mathf.Abs(local.x) <= PadPaintLogic.HalfX && Mathf.Abs(local.z) <= PadPaintLogic.HalfZ)
                    return tableTopY + PadPaintLogic.SurfaceLocalY;
            }
            return tableTopY;
        }
    }
}
