#nullable enable
using Unity.Netcode;
using UnityEngine;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」のペン（marker_circle / marker_segment）を保持中、ペン先が自然に斜め下を向く演出。
    /// 机（描画パッド面 or 天板）が邪魔なら寝て（水平）、持ち上げるほど傾き、最大 45° まで俯く
    /// （<see cref="MarkerTiltLogic"/> の純計算）。位置は触らず回転だけ上書きする。
    ///
    /// 適用（回転上書き）は保持追従の**後**に走らせる必要がある（<see cref="Grabbable"/>.Update /
    /// PinchGrabInteractor.LateUpdate が先に姿勢を決める）ため <c>DefaultExecutionOrder(120)</c> + LateUpdate。
    /// - サーバ: 保持中は毎フレーム傾ける（NetworkTransform で全 peer へ複製）
    /// - 保持者本人のクライアント: 楽観表示のため同じ計算をローカル上書き（サーバ複製の遅延を隠す）
    /// - リリース（サーバのみ）: heading を保ったまま pitch 0（寝かせる）+ 底面を面へ接地して静置する。
    ///
    /// Grabbable と同じ GameObject に付ける。ペンは physics:false（Rigidbody 無し）= リリース後は
    /// この確定姿勢のまま静止し続ける。値は TableDuoSceneSetup が SerializedObject で焼き込む。
    /// </summary>
    [DefaultExecutionOrder(120)]
    public sealed class MarkerHoldTilt : NetworkBehaviour
    {
        // 最大俯角（水平からの下向き角）。ユーザー仕様 45° 固定
        private const float MaxPitchDeg = 45f;

        [Tooltip("原点→ペン先距離（m）。scale 1.3 の marker で 0.0923")]
        [SerializeField] private float tipDistance = 0.0923f;
        [Tooltip("天板上面 Y（=topY）。パッドの外なら面高はこれ")]
        [SerializeField] private float tableTopY;
        [Tooltip("描画パッド（BEAR_pad）。この上にペン先があるときは面高が tableTopY+パッド上面ぶん上がる")]
        [SerializeField] private Transform? padTransform;

        private Grabbable? _grab;
        private bool _wasHeld;       // サーバのリリースエッジ検知用
        private bool _applying;      // 適用中（傾け中）か。開始エッジで平滑状態をリセット
        private float _smoothPitch;
        private Vector3 _lastHeading = Vector3.forward;

        private void Awake()
        {
            _grab = GetComponent<Grabbable>();
            if (_grab == null)
                Debug.LogWarning($"[TableDuo] MarkerHoldTilt on {name}: Grabbable がありません（傾き無効）");
        }

        private void LateUpdate()
        {
            if (_grab == null) return;

            // サーバ: 保持解放エッジでペンを寝かせて置く（heading 維持・pitch 0・底面を面へ接地）
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

            if (!_applying)
            {
                SeedHeading();
                _smoothPitch = 0f;
                _applying = true;
            }
            ApplyTilt();
        }

        /// <summary>現在の forward の水平成分を heading の初期値に据える（縮退時は +Z）。</summary>
        private void SeedHeading()
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            _lastHeading = f.sqrMagnitude > 1e-8f ? f.normalized : Vector3.forward;
        }

        private void ApplyTilt()
        {
            float surfaceY = SurfaceYAt(transform.position);
            float target = MarkerTiltLogic.ComputeAllowedPitchDeg(
                transform.position.y, surfaceY, tipDistance, MaxPitchDeg);
            _smoothPitch = MarkerTiltLogic.SmoothPitch(_smoothPitch, target, Time.deltaTime);
            Vector3 dir = MarkerTiltLogic.ComposeTiltedForward(
                transform.forward, _smoothPitch, _lastHeading, out _lastHeading);
            if (dir.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
        }

        private void LayFlatOnRelease()
        {
            Vector3 f = transform.forward;
            f.y = 0f;
            Vector3 heading = f.sqrMagnitude > 1e-8f ? f.normalized : _lastHeading;
            Vector3 dir = MarkerTiltLogic.ComposeTiltedForward(transform.forward, 0f, heading, out _lastHeading);
            if (dir.sqrMagnitude > 1e-8f)
                transform.rotation = Quaternion.LookRotation(dir, Vector3.up);
            // 原点＝バレル底面なので position.y をそのまま面高に載せると接地する。XZ は触らない
            var p = transform.position;
            p.y = SurfaceYAt(p);
            transform.position = p;
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
