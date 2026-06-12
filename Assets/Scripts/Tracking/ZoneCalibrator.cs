#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace FixedCamVr.Tracking
{
    /// <summary>
    /// Quest コントローラでカメラ切替ゾーン（PlayerZone の AABB）を実地調整する。
    ///
    /// 入力は OvrControllerBridge から <see cref="Feed"/> で転送される（このアセンブリは
    /// OVRInput に依存しない）。操作系:
    ///   両グリップ 1 秒長押し … 校正モード切替（Bridge 側で検知）
    ///   A / B … 対象ゾーンを次 / 前へ
    ///   左スティック … ゾーン中心を XZ 移動
    ///   右スティック … ゾーンの halfExtents を XZ 伸縮
    ///   右トリガ押しながら … 微調整（速度 1/5）
    ///   X … 保存（persistentDataPath/zone_calibration.json、以後の起動で自動適用）
    ///   Y … authored 値へリセット + 保存ファイル削除
    ///
    /// 校正モード中はゾーンの床フットプリントをカメラ index 別の色で表示する
    /// （選択中はブリンク）。マテリアルは Sprites/Default（URP 互換・Cull Off・透過）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneCalibrator : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("調整対象のゾーン一覧。空なら Awake で FindObjectsOfType する。")]
        [SerializeField] private PlayerZone[] zones = Array.Empty<PlayerZone>();

        [Tooltip("HMD（CenterEyeAnchor）。床マーカー表示用。null なら Camera.main。")]
        [SerializeField] private Transform? headTransform;

        [Header("Behavior")]
        [Tooltip("起動と同時に校正モードへ入る（通常は両グリップ長押しで入る）。")]
        [SerializeField] private bool startInCalibration = false;

        [Tooltip("保存ファイル名（Application.persistentDataPath 直下）。")]
        [SerializeField] private string saveFileName = "zone_calibration.json";

        [Tooltip("中心移動・伸縮の速度 (m/s)。")]
        [SerializeField, Min(0.01f)] private float moveSpeed = 0.6f;

        [Tooltip("微調整時（右トリガ押下中）の速度 (m/s)。")]
        [SerializeField, Min(0.01f)] private float fineSpeed = 0.12f;

        [Tooltip("halfExtents の下限 (m)。これ未満には縮められない。")]
        [SerializeField, Min(0.01f)] private float minHalfExtent = 0.15f;

        /// <summary>Bridge から毎フレーム渡される校正入力。</summary>
        public struct CalibInput
        {
            public Vector2 move;     // 左スティック（中心 XZ）
            public Vector2 size;     // 右スティック（halfExtents XZ）
            public bool nextZone;    // A
            public bool prevZone;    // B
            public bool save;        // X
            public bool reset;       // Y
            public bool fine;        // 右トリガ
        }

        [Serializable]
        private class Entry { public string name = ""; public Vector3 center; public Vector3 halfExtents; }

        [Serializable]
        private class SaveData { public List<Entry> entries = new(); }

        /// <summary>校正モード中か。Bridge はこれを見て通常入力を抑止する。</summary>
        public bool IsActive { get; private set; }

        private (Vector3 center, Vector3 half)[] _authored = Array.Empty<(Vector3, Vector3)>();
        private int _selected;
        private GameObject? _vizRoot;
        private Transform[] _vizQuads = Array.Empty<Transform>();
        private Material[] _vizMats = Array.Empty<Material>();
        private Transform? _headMarker;

        // カメラ index 別の色（A=緑 / B=青 / C=橙…）。index がパレットを超えたら循環。
        private static readonly Color[] Palette =
        {
            new(0.3f, 1f, 0.5f), new(0.35f, 0.6f, 1f), new(1f, 0.7f, 0.3f),
            new(1f, 0.4f, 0.6f), new(0.7f, 0.5f, 1f),
        };

        private string SavePath => Path.Combine(Application.persistentDataPath, saveFileName);

        private void Awake()
        {
            if (zones.Length == 0) zones = FindObjectsOfType<PlayerZone>();

            // authored 値（シーンに保存されている配置）を控えてから、保存済み校正を適用
            _authored = new (Vector3, Vector3)[zones.Length];
            for (int i = 0; i < zones.Length; i++)
                _authored[i] = (zones[i].Center, zones[i].HalfExtents);

            LoadAndApply();
        }

        private void Start()
        {
            if (startInCalibration) SetActive(true);
        }

        public void Toggle() => SetActive(!IsActive);

        private void SetActive(bool on)
        {
            if (IsActive == on) return;
            IsActive = on;
            if (on)
            {
                BuildViz();
                Debug.Log("[ZoneCalib] 校正モード ON — A/B=ゾーン選択, 左スティック=移動, 右スティック=サイズ, " +
                          "右トリガ=微調整, X=保存, Y=リセット, 両グリップ長押し=終了");
            }
            else
            {
                TearDownViz();
                Debug.Log("[ZoneCalib] 校正モード OFF");
            }
        }

        /// <summary>Bridge から毎フレーム呼ばれる（校正モード中のみ）。</summary>
        public void Feed(in CalibInput input)
        {
            if (!IsActive || zones.Length == 0) return;

            if (input.nextZone) _selected = (_selected + 1) % zones.Length;
            if (input.prevZone) _selected = (_selected - 1 + zones.Length) % zones.Length;
            if (input.nextZone || input.prevZone)
                Debug.Log($"[ZoneCalib] 選択: {zones[_selected].Label} (cam {zones[_selected].CameraIndex})");

            if (input.save) { Save(); return; }
            if (input.reset) { ResetToAuthored(); return; }

            var z = zones[_selected];
            float spd = (input.fine ? fineSpeed : moveSpeed) * Time.deltaTime;

            Vector3 center = z.Center;
            Vector3 half = z.HalfExtents;
            // スティックのデッドゾーン（ドリフト対策）
            Vector2 mv = input.move.sqrMagnitude > 0.01f ? input.move : Vector2.zero;
            Vector2 sz = input.size.sqrMagnitude > 0.01f ? input.size : Vector2.zero;
            if (mv == Vector2.zero && sz == Vector2.zero) return;

            center += new Vector3(mv.x, 0f, mv.y) * spd;
            half.x = Mathf.Max(minHalfExtent, half.x + sz.x * spd);
            half.z = Mathf.Max(minHalfExtent, half.z + sz.y * spd);
            z.SetRuntimeBounds(center, half);
        }

        private void Update()
        {
            if (IsActive) UpdateViz();
        }

        // ---- 永続化 -------------------------------------------------------------

        private void Save()
        {
            var data = new SaveData();
            foreach (var z in zones)
                data.entries.Add(new Entry { name = z.name, center = z.Center, halfExtents = z.HalfExtents });
            try
            {
                File.WriteAllText(SavePath, JsonUtility.ToJson(data, prettyPrint: true));
                Debug.Log($"[ZoneCalib] 保存した: {SavePath}（次回起動から自動適用）");
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ZoneCalib] 保存失敗: {ex.Message}");
            }
        }

        private void LoadAndApply()
        {
            try
            {
                if (!File.Exists(SavePath)) return;
                var data = JsonUtility.FromJson<SaveData>(File.ReadAllText(SavePath));
                if (data?.entries == null) return;
                int applied = 0;
                foreach (var e in data.entries)
                {
                    foreach (var z in zones)
                    {
                        if (z.name != e.name) continue;
                        z.SetRuntimeBounds(e.center, e.halfExtents);
                        applied++;
                        break;
                    }
                }
                Debug.Log($"[ZoneCalib] 保存済み校正を適用: {applied}/{zones.Length} zones ({SavePath})");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[ZoneCalib] 校正ロード失敗（authored 値で続行）: {ex.Message}");
            }
        }

        private void ResetToAuthored()
        {
            for (int i = 0; i < zones.Length && i < _authored.Length; i++)
                zones[i].SetRuntimeBounds(_authored[i].center, _authored[i].half);
            try { if (File.Exists(SavePath)) File.Delete(SavePath); }
            catch (Exception ex) { Debug.LogWarning($"[ZoneCalib] 保存ファイル削除失敗: {ex.Message}"); }
            Debug.Log("[ZoneCalib] authored 値へリセット（保存ファイルも削除）");
        }

        // ---- 可視化（床フットプリント）-------------------------------------------

        private void BuildViz()
        {
            TearDownViz();
            _vizRoot = new GameObject("[ZoneCalibViz]");
            _vizQuads = new Transform[zones.Length];
            _vizMats = new Material[zones.Length];

            // Sprites/Default: URP でも動く・Cull Off（Quad の向きを問わない）・頂点色透過
            var shader = Shader.Find("Sprites/Default");
            for (int i = 0; i < zones.Length; i++)
            {
                var quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                quad.name = $"ZoneViz_{zones[i].Label}";
                quad.transform.SetParent(_vizRoot.transform, worldPositionStays: false);
                quad.transform.rotation = Quaternion.Euler(90f, 0f, 0f); // 床に寝かせる
                var col = quad.GetComponent<Collider>();
                if (col != null) Destroy(col);

                var mat = new Material(shader);
                quad.GetComponent<Renderer>().sharedMaterial = mat;
                _vizQuads[i] = quad.transform;
                _vizMats[i] = mat;
            }

            // HMD 床マーカー（白い小さな菱形）
            var marker = GameObject.CreatePrimitive(PrimitiveType.Quad);
            marker.name = "HeadMarker";
            marker.transform.SetParent(_vizRoot.transform, worldPositionStays: false);
            marker.transform.rotation = Quaternion.Euler(90f, 45f, 0f);
            marker.transform.localScale = new Vector3(0.15f, 0.15f, 1f);
            var mcol = marker.GetComponent<Collider>();
            if (mcol != null) Destroy(mcol);
            var mmat = new Material(shader) { color = new Color(1f, 1f, 1f, 0.9f) };
            marker.GetComponent<Renderer>().sharedMaterial = mmat;
            _headMarker = marker.transform;
        }

        private void UpdateViz()
        {
            if (_vizRoot == null) return;
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                var q = _vizQuads[i];
                Vector3 c = z.Center;
                // ゾーン毎に高さを 1mm ずらして Z-fight 回避
                q.position = new Vector3(c.x, 0.02f + i * 0.002f, c.z);
                q.localScale = new Vector3(z.HalfExtents.x * 2f, z.HalfExtents.z * 2f, 1f);

                Color baseColor = Palette[z.CameraIndex % Palette.Length];
                bool selected = i == _selected;
                float alpha = selected
                    ? Mathf.Lerp(0.35f, 0.75f, Mathf.PingPong(Time.time * 2f, 1f)) // ブリンク
                    : 0.22f;
                _vizMats[i].color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            }

            if (_headMarker != null)
            {
                Transform? head = headTransform;
                if (head == null && Camera.main != null) head = Camera.main.transform;
                if (head != null)
                    _headMarker.position = new Vector3(head.position.x, 0.03f + zones.Length * 0.002f, head.position.z);
            }
        }

        private void TearDownViz()
        {
            if (_vizRoot != null) Destroy(_vizRoot);
            foreach (var m in _vizMats) if (m != null) Destroy(m);
            _vizRoot = null;
            _vizQuads = Array.Empty<Transform>();
            _vizMats = Array.Empty<Material>();
            _headMarker = null;
        }

        private void OnDestroy() => TearDownViz();
    }
}
