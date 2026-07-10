#nullable enable
using TableDuoVr.Hands;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// ローカルプレイヤーのピンチ → Grabbable への掴み要求。
    /// TableDuoPlayer(owner) が生成する。ピンチ立ち上がりで最寄りの未保持 Grabbable に
    /// GrabRequest、立ち下がりで Release。裁定はサーバ（Grabbable 側）。
    ///
    /// UX 改善（2026-07-08）:
    /// - 対象選択は手首アンカーではなく**ピンチ点**（親指先・人差し指先の中点、HandLandmarks FK）基準。
    ///   手首基準だと指先から 8–10cm ズレた物を掴んでいた
    /// - **楽観的ローカルアタッチ**: ピンチ瞬間からローカルで手に吸着表示（Grabbable.ApplyLocalHoldPose）。
    ///   サーバ往復＋NetworkTransform 補間を待たないので「掴めるまで遅い・追従が遅れる」が消える。
    ///   サーバが確定しなければ ConfirmTimeout で吸着を解除（他者先着 = 即解除）
    /// - Release RPC は**無条件送信**（確定前の素早い離しでも送る。旧実装は IsHeldBy 確認待ちのため
    ///   確定前リリースが握り潰され、サーバ側で永久保持になるバグがあった）
    /// </summary>
    public sealed class PinchGrabInteractor : MonoBehaviour
    {
        // ピンチ点基準の掴み判定半径。手首基準時代の 0.18m は指先誤差の補償込みだったので縮小
        private const float GrabRadius = 0.12f;
        // 楽観的アタッチをサーバ確定なしで維持する上限（先着負け・不達の後始末）
        private const float ConfirmTimeout = 0.5f;

        private Transform? _seat;
        private ulong _localClientId;
        private readonly bool[] _wasPinching = new bool[2];
        private readonly Grabbable?[] _heldCandidate = new Grabbable?[2];
        private readonly Vector3[] _holdOffsetPos = new Vector3[2];
        private readonly Quaternion[] _holdOffsetRot = new Quaternion[2];
        private readonly float[] _holdStart = new float[2];
        private readonly Vector3[] _landmarks = new Vector3[HandLandmarks.Count];

        public void Initialize(Transform seat, ulong localClientId)
        {
            _seat = seat;
            _localClientId = localClientId;
        }

        private void Update()
        {
            if (_seat == null) return;
            var src = HandPoseSourceRegistry.Best;
            if (src == null || !src.IsValid) return;
            var pose = src.Current;

            Tick(0, pose.TrackedL && pose.PinchL, pose);
            Tick(1, pose.TrackedR && pose.PinchR, pose);
        }

        // NetworkTransform（非権威側）は Update で受信 state を適用する。保持者本人の 0 レイテンシ
        // 上書きは LateUpdate で行い、毎フレームこちらが最後に勝つ
        private void LateUpdate()
        {
            if (_seat == null) return;
            var src = HandPoseSourceRegistry.Best;
            if (src == null || !src.IsValid) return;
            var pose = src.Current;

            TickLocalHold(0, pose.TrackedL, pose.WristPosL, pose.WristRotL);
            TickLocalHold(1, pose.TrackedR, pose.WristPosR, pose.WristRotR);
        }

        private void Tick(int hand, bool pinching, AvatarPose pose)
        {
            if (pinching && !_wasPinching[hand])
            {
                Vector3 pinchLocal = PinchPointLocal(hand == 1, pose);
                Vector3 pinchWorld = _seat!.TransformPoint(pinchLocal);
                var target = FindNearestFree(pinchWorld);
                // 診断（[TDV-GRAB]）: ピンチ立ち上がりごとに最寄り駒の距離と半径内外を出す。
                // 「掴めない」の切り分け用（ピンチ未検出ならこのログ自体が出ない）
                LogNearestDiag(hand, pinchWorld, target);
                if (target != null)
                {
                    target.RequestGrabServerRpc((byte)hand);
                    _heldCandidate[hand] = target;
                    _holdStart[hand] = Time.time;
                    // 楽観的アタッチ用オフセット（サーバと同じ式・ローカル手 pose 基準）
                    Vector3 wristLocal = hand == 1 ? pose.WristPosR : pose.WristPosL;
                    Quaternion rotLocal = hand == 1 ? pose.WristRotR : pose.WristRotL;
                    Vector3 handPos = _seat.TransformPoint(wristLocal);
                    Quaternion handRot = _seat.rotation * rotLocal;
                    var inv = Quaternion.Inverse(handRot);
                    _holdOffsetPos[hand] = inv * (target.transform.position - handPos);
                    _holdOffsetRot[hand] = inv * target.transform.rotation;
                }
            }
            else if (!pinching && _wasPinching[hand])
            {
                var held = _heldCandidate[hand];
                if (held != null)
                {
                    // 無条件送信: サーバ確定前の素早い離しでも release を届ける
                    //（サーバは holder==sender を照合するので他者の保持を壊さない）
                    held.RequestReleaseServerRpc();
                }
                _heldCandidate[hand] = null;
            }
            _wasPinching[hand] = pinching;
        }

        /// <summary>楽観的ローカルアタッチの毎フレーム更新（0 レイテンシ表示）と後始末。</summary>
        private void TickLocalHold(int hand, bool tracked, Vector3 wristLocal, Quaternion wristRotLocal)
        {
            var target = _heldCandidate[hand];
            if (target == null) return;

            bool mine = target.IsHeldBy(_localClientId, (byte)hand);
            if (target.IsHeld && !mine)
            {
                _heldCandidate[hand] = null; // 先着負け → 即座に吸着解除（サーバ表示へ戻る）
                return;
            }
            if (!mine && Time.time - _holdStart[hand] > ConfirmTimeout)
            {
                _heldCandidate[hand] = null; // サーバ確定が来ない（不達等）→ 諦める
                return;
            }
            if (!tracked) return; // ロスト中はローカル上書きしない（サーバのフリーズ表示に任せる）

            Vector3 handPos = _seat!.TransformPoint(wristLocal);
            Quaternion handRot = _seat.rotation * wristRotLocal;
            target.ApplyLocalHoldPose(
                handPos + handRot * _holdOffsetPos[hand],
                handRot * _holdOffsetRot[hand]);
        }

        /// <summary>ピンチ点（親指先・人差し指先の中点）を席ローカルで返す。layout 未取得は手首で近似。</summary>
        private Vector3 PinchPointLocal(bool right, AvatarPose pose)
        {
            var layout = right ? HandSkeletonLayout.CapturedR : HandSkeletonLayout.CapturedL;
            Vector3 wrist = right ? pose.WristPosR : pose.WristPosL;
            Quaternion rot = right ? pose.WristRotR : pose.WristRotL;
            var bones = right ? pose.BonesR : pose.BonesL;
            if (HandLandmarks.Compute(layout, wrist, rot, bones, _landmarks))
            {
                return (_landmarks[2] + _landmarks[3]) * 0.5f; // thumbTip + indexTip
            }
            return wrist;
        }

        /// <summary>診断: ピンチ点から最寄り Grabbable までの距離（半径無関係）と半径内外をログ。</summary>
        private void LogNearestDiag(int hand, Vector3 pinchWorld, Grabbable? picked)
        {
            Grabbable? nearest = null;
            float bestSqr = float.PositiveInfinity;
            int total = 0, free = 0;
            foreach (var g in FindObjectsOfType<Grabbable>())
            {
                if (g == null) continue;
                total++;
                if (g.IsHeld) continue;
                free++;
                float sqr = (g.transform.position - pinchWorld).sqrMagnitude;
                if (sqr < bestSqr) { bestSqr = sqr; nearest = g; }
            }
            float dist = nearest != null ? Mathf.Sqrt(bestSqr) : -1f;
            Debug.Log($"[TDV-GRAB] hand{hand} pinch! pinchWorld={pinchWorld} grabbables total={total} free={free} " +
                      $"nearest={(nearest != null ? nearest.name : "none")} dist={dist:F3}m radius={GrabRadius:F3}m " +
                      $"→ {(picked != null ? "掴む" : "半径外/対象なし")}");
        }

        private Grabbable? FindNearestFree(Vector3 worldPos)
        {
            Grabbable? best = null;
            float bestSqr = GrabRadius * GrabRadius;
            // ピンチ立ち上がり時のみ（稀）に都度取得 → 動的/遅延 spawn の Grabbable も対象に入る
            // （生成時1回固定だと後から spawn したオブジェクトを永久に掴めない）
            foreach (var g in FindObjectsOfType<Grabbable>())
            {
                if (g == null || g.IsHeld) continue;
                float sqr = (g.transform.position - worldPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = g;
                }
            }
            return best;
        }
    }
}
