#nullable enable
using System;
using FixedCamVr.Streaming;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// 毎フレーム head の位置を見て、最も優先度の高い包含ゾーンを選び、
    /// CameraStreamRegistry のアクティブカメラを切り替える。
    /// 境界フリッカ防止に「現ゾーンは shrink 判定 / 他ゾーンは正規判定」のヒステリシスを掛ける。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PlayerZoneTracker : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("切替対象の CameraStreamRegistry。null の場合 Update は何もしない。")]
        [SerializeField] private CameraStreamRegistry? registry;

        [Tooltip("OVRCameraRig 配下の CenterEyeAnchor を割り当てる。null の場合は Camera.main.transform を使用。")]
        [SerializeField] private Transform? headTransform;

        [Tooltip("評価対象のゾーン一覧。配列順は同優先度時のタイブレーク（先頭優先）に使われる。")]
        [SerializeField] private PlayerZone[] zones = Array.Empty<PlayerZone>();

        [Header("Behavior")]
        [Tooltip("現ゾーンから出たと判定する余白 (m)。境界での切替フリッカを防ぐ。各 halfExtent を超える値を入れても 0 でクランプされる。")]
        [SerializeField, Min(0f)] private float hysteresisShrink = 0.15f;

        [Tooltip("評価間隔 (秒)。0 なら毎フレーム。GC アロケは無いので 0 でも安全。")]
        [SerializeField, Min(0f)] private float updateInterval = 0.05f;

        [Tooltip("どのゾーンにも入っていない時、直近のゾーンを維持する。false なら index 据え置き（無動作）。")]
        [SerializeField] private bool keepLastWhenOutside = true;

        [Header("Debug")]
        [Tooltip("ゾーン切替時に Debug.Log を出力する。")]
        [SerializeField] private bool logChanges = true;

        private PlayerZone? _current;
        private float _accum;

        /// <summary>直近に選択されたゾーン。未選択時は null。</summary>
        public PlayerZone? CurrentZone => _current;

        private void Reset()
        {
            registry = FindObjectOfType<CameraStreamRegistry>();
            var cam = Camera.main;
            if (cam != null) headTransform = cam.transform;
        }

        private void Update()
        {
            if (registry == null) return;
            if (zones.Length == 0) return;

            _accum += Time.deltaTime;
            if (_accum < updateInterval) return;
            // updateInterval 単位で減算することで、毎フレーム差分の取りこぼしを抑える。
            // updateInterval == 0 のときは毎フレーム評価とし、_accum を 0 に戻す。
            _accum = updateInterval > 0f ? _accum - updateInterval : 0f;

            Transform? head = headTransform;
            if (head == null)
            {
                var cam = Camera.main;
                if (cam == null) return;
                head = cam.transform;
            }

            PlayerZone? picked = Pick(head.position);
            if (picked == null) return;
            if (ReferenceEquals(picked, _current)) return;

            _current = picked;
            registry.SetActive(picked.CameraIndex);

            if (logChanges)
            {
                Debug.Log($"[PlayerZoneTracker] zone={picked.Label} -> camera index={picked.CameraIndex}");
            }
        }

        /// <summary>
        /// 現ゾーンを優先（shrink 判定）し、はみ出していれば優先度最大の包含ゾーンを採用。
        /// 何にも入っていなければ keepLastWhenOutside の方針で current を返すか null を返す。
        /// 同優先度のときは zones 配列の先頭側が選ばれる（厳密な比較は <c>&gt;</c> なので先勝ち）。
        /// 純粋関数として書かれており、テストから reflection で直接呼ばれる。
        /// </summary>
        private PlayerZone? Pick(Vector3 head)
        {
            // 1) 直近ゾーンが縮小 AABB 内ならそのまま維持（フリッカ防止）。
            if (_current != null && _current.Contains(head, hysteresisShrink))
            {
                return _current;
            }

            // 2) 包含ゾーンの中で最も Priority の高いものを採用。同値は先勝ち。
            PlayerZone? best = null;
            int bestPriority = int.MinValue;
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                if (z == null) continue;
                if (!z.Contains(head)) continue;
                if (z.Priority > bestPriority)
                {
                    best = z;
                    bestPriority = z.Priority;
                }
            }

            if (best != null) return best;

            // 3) どこにも入っていない → ポリシーに従う。
            return keepLastWhenOutside ? _current : null;
        }
    }
}
