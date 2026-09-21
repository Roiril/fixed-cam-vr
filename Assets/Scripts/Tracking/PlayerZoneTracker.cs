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

        [Tooltip("切替を一本化する CameraSwitchDirector。割当時はゾーン切替をここ経由で要求する" +
                 "（最小滞在・クールダウン・cue 凍結ガード）。null なら registry を直接叩く（後方互換）。")]
        [SerializeField] private CameraSwitchDirector? director;

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
        private ShowRunDirector? _runDirector;

        /// <summary>最後の選択を保持せず、指定カメラの実領域への包含を返す。領域なしは未観測。</summary>
        public bool? CameraPresenceAt(int camera, Vector3 position)
        {
            if (float.IsNaN(position.x) || float.IsInfinity(position.x)
                || float.IsNaN(position.y) || float.IsInfinity(position.y)
                || float.IsNaN(position.z) || float.IsInfinity(position.z)) return null;
            bool defined = false;
            foreach (PlayerZone? zone in zones)
            {
                if (zone == null || !zone.isActiveAndEnabled || zone.CameraIndex != camera) continue;
                defined = true;
                if (zone.Contains(position)) return true;
            }
            return defined ? false : (bool?)null;
        }

        /// <summary>直近に選択されたゾーン。未選択時は null。</summary>
        public PlayerZone? CurrentZone => _current;

        /// <summary>
        /// いま生きているゾーン配列（読み取り用）。区間の長さを測るのに
        /// <b>同じカメラの矩形すべて</b>が要る（<see cref="ZoneSpanMath"/>）—— 貪欲分解は
        /// 1 つのカメラを複数の矩形に割ることがあり、入った矩形だけでは区間の長さにならない。
        /// </summary>
        public PlayerZone[] Zones => zones;

        /// <summary>
        /// Pick 確定でゾーンが変わった時に発火する（旧ゾーン, 新ゾーン）。
        /// 初回取得時は旧ゾーンが null。周回カウント（LapCounter）等の外部消費者向け。
        /// 発火は registry.SetActive と同じ箇所（cameraIndex 未変化のゾーン間移動でも発火する点に注意）。
        /// </summary>
        public event Action<PlayerZone?, PlayerZone>? ZoneChanged;

        /// <summary>
        /// ランタイムでゾーン配列を差し替える（ZoneLayoutApplier が layout / frame 変更時に呼ぶ）。
        /// 配列順は同優先度時のタイブレーク（先頭優先）に効く。_current はリセットして次 Update で再評価させる。
        /// </summary>
        public void SetZonesRuntime(PlayerZone[] newZones)
        {
            zones = newZones ?? Array.Empty<PlayerZone>();
            _current = null;
        }

        /// <summary>ランタイムでヒステリシス幅を差し替える（layout.hysteresisM の反映用）。</summary>
        public void SetHysteresisShrink(float value) => hysteresisShrink = Mathf.Max(0f, value);

        /// <summary>
        /// 現在ゾーンの記憶（_current）を無効化し、次 Update で現在位置から再 Pick させる。
        /// 同一ゾーンに滞在したままでも通常経路（<c>director.RequestZone</c> → dwell → Zone commit）で
        /// ゾーンカメラへ復帰させるため（_current 不変だと Pick が現ゾーンを維持し続ける）。
        /// </summary>
        public void InvalidateCurrent() => _current = null;

        // 再有効化のたびに現在ゾーンを再取得する。Web cameraOverride 中は ShowControlClient が
        // 本コンポーネントを enabled=false にし、解除で enabled=true に戻す。その OnEnable で記憶ゾーンを
        // 無効化しておくと、同一ゾーン滞在のままでも次 Update で現在位置から再 Pick し、override カメラへの
        // 表示固着を解消できる（初回 enable では _current は既に null なので無害）。
        private void OnEnable() => InvalidateCurrent();
        private void OnDisable() => _runDirector?.NotifyClosingAreaPresence(null);

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

            if (_runDirector == null) _runDirector = FindObjectOfType<ShowRunDirector>();
            bool? inClosingArea = CameraPresenceAt(0, head.position);
            var headDevice = UnityEngine.XR.InputDevices.GetDeviceAtXRNode(UnityEngine.XR.XRNode.Head);
            if (headDevice.isValid && headDevice.TryGetFeatureValue(UnityEngine.XR.CommonUsages.isTracked,
                    out bool tracked) && !tracked) inClosingArea = null;
            _runDirector?.NotifyClosingAreaPresence(inClosingArea);

            PlayerZone? picked = Pick(head.position);
            if (picked == null) return;
            if (ReferenceEquals(picked, _current)) return;

            PlayerZone? previous = _current;
            _current = picked;
            // Director があればゾーン切替として要求（滞在ガード後に適用）。無ければ従来どおり即時切替。
            if (director != null) director.RequestZone(picked.CameraIndex);
            else registry.SetActive(picked.CameraIndex);
            ZoneChanged?.Invoke(previous, picked);

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
            // 判定の中身は純ロジック（ZonePickLogic）へ委譲する。Web シミュレータの JS ミラーと
            // 同じセマンティクスを共有し、drift を golden トレース比較で機械検出できるようにするため
            // （計画 2026-07-25_show-simulator.md 段 S1）。ここは PlayerZone ↔ Box の詰め替えだけ。
            var boxes = new ZonePickLogic.Box[zones.Length];
            int current = -1;
            for (int i = 0; i < zones.Length; i++)
            {
                PlayerZone? z = zones[i];
                if (z == null)
                {
                    // null 要素は「絶対に含まれない箱」にして index の対応を崩さない。
                    boxes[i] = ZonePickLogic.Box.Aabb(0f, 0f, 0f, -1f, -1f, -1f, 0, int.MinValue);
                    continue;
                }
                if (ReferenceEquals(z, _current)) current = i;
                Vector3 c = z.Center, h = z.HalfExtents;
                // ゾーンの向きは yaw のみ（CourseFrame が与えるのも yaw だけ）。
                float yaw = z.Rotation.eulerAngles.y;
                boxes[i] = ZonePickLogic.Box.WithYaw(c.x, c.y, c.z, h.x, h.y, h.z, yaw, z.CameraIndex, z.Priority);
            }

            int picked = ZonePickLogic.Pick(boxes, head.x, head.y, head.z,
                                            current, hysteresisShrink, keepLastWhenOutside);
            if (picked < 0) return keepLastWhenOutside ? _current : null;
            return zones[picked];
        }
    }
}
