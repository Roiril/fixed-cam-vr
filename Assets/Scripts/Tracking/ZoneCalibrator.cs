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
    /// 右コントローラから床へレイを飛ばし、レイ先（床ヒット点）を基準に操作する。
    /// 入力は OvrControllerBridge から <see cref="Feed"/> で転送される（このアセンブリは
    /// OVRInput に依存しない。レイの向きは <see cref="rightHandTransform"/> の forward から
    /// このスクリプト自身が計算する）。操作系:
    ///   両グリップ 3 秒長押し … 校正モード切替（Bridge 側で検知）
    ///   A … レイ先（床ヒット点）にあるゾーンを選択
    ///   右トリガ握り中 … 選択ゾーンをレイ先へドラッグ（床に沿って平行移動）
    ///   右スティック … 横倒し=halfExtents.x（横）/ 縦倒し=halfExtents.z（縦）を伸縮
    ///   X … 保存（persistentDataPath/zone_calibration.json、以後の起動で自動適用）
    ///   Y … authored 値へリセット + 保存ファイル削除
    ///
    /// 校正モード中のみ、コントローラ先端から床へのレイと、ゾーンの床フットプリント
    /// （カメラ index 別の色・選択中はブリンク・レイで指している間はハイライト）を表示する。
    /// マテリアルは Sprites/Default（URP 互換・Cull Off・透過）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class ZoneCalibrator : MonoBehaviour
    {
        [Header("References")]
        [Tooltip("調整対象のゾーン一覧。空なら Awake で FindObjectsOfType する。")]
        [SerializeField] private PlayerZone[] zones = Array.Empty<PlayerZone>();

        [Tooltip("HMD（CenterEyeAnchor）。床マーカー表示・起動位置基準化用。null なら Camera.main。")]
        [SerializeField] private Transform? headTransform;

        [Tooltip("レイの起点・向き（右コントローラ = OVRCameraRig/TrackingSpace/RightHandAnchor）。" +
                 "position から forward 方向に床へレイを飛ばす。null なら headTransform にフォールバック。")]
        [SerializeField] private Transform? rightHandTransform;

        [Header("Behavior")]
        [Tooltip("起動と同時に校正モードへ入る（通常は両グリップ長押しで入る）。")]
        [SerializeField] private bool startInCalibration = false;

        [Tooltip("保存ファイル名（Application.persistentDataPath 直下）。")]
        [SerializeField] private string saveFileName = "zone_calibration.json";

        [Tooltip("右スティックでのサイズ伸縮速度 (m/s)。")]
        [SerializeField, Min(0.01f)] private float resizeSpeed = 0.6f;

        [Tooltip("halfExtents の下限 (m)。これ未満には縮められない。")]
        [SerializeField, Min(0.01f)] private float minHalfExtent = 0.15f;

        [Header("Recenter（起動時の立ち位置基準化）")]
        [Tooltip("起動時の HMD 位置（XZ）をレイアウト原点としてゾーン全体を平行移動する。" +
                 "authored 配置は『プレイヤーがコース中心 (0,0) に立つ』前提なので、" +
                 "これを ON にすると起動した場所がコース中心になる。向き（回転）は合わせない（AABB のため）。")]
        [SerializeField] private bool recenterOnStart = true;

        [Tooltip("トラッキング安定待ち。HMD 位置が有効になってからこの秒数後に基準化する。")]
        [SerializeField, Min(0f)] private float recenterDelaySec = 1.0f;

        /// <summary>Bridge から毎フレーム渡される校正入力。</summary>
        public struct CalibInput
        {
            public Vector2 size;     // 右スティック（halfExtents XZ）
            public bool pick;        // A: レイ先のゾーンを選択
            public bool grab;        // 右トリガ握り中: レイ先へドラッグ
            public bool save;        // X
            public bool reset;       // Y
        }

        [Serializable]
        private class Entry { public string name = ""; public Vector3 center; public Vector3 halfExtents; }

        [Serializable]
        private class SaveData { public List<Entry> entries = new(); }

        /// <summary>校正モード中か。Bridge はこれを見て通常入力を抑止する。</summary>
        public bool IsActive { get; private set; }

        private (Vector3 center, Vector3 half)[] _authored = Array.Empty<(Vector3, Vector3)>();
        private int _selected;
        private int _hovered = -1;       // レイ先にあるゾーン（ハイライト用）
        private bool _hasHit;            // 今フレーム床ヒットがあるか
        private Vector3 _hit;            // 床ヒット点（ワールド）
        private bool _grabbing;          // トリガでドラッグ中
        private Vector2 _grabOffsetXZ;   // 掴んだ瞬間の中心 - ヒット点（XZ、ドラッグ追従用）

        // 起動時基準化のオフセット（XZ）。ゾーンの「真の座標」は origin 相対で扱い、
        // 表示・判定用のワールド座標 = origin 相対 + _recenterOffset。
        // 保存時は引き、ロード時は足すことで、JSON は常に origin 相対で持つ
        // （セッション毎に立ち位置が変わっても保存済みレイアウトの形が崩れない）。
        private Vector3 _recenterOffset = Vector3.zero;
        private bool _recenterDone;
        private float _recenterTimer;
        private GameObject? _vizRoot;
        private Transform[] _vizQuads = Array.Empty<Transform>();
        private Material[] _vizMats = Array.Empty<Material>();
        private Transform? _headMarker;
        private LineRenderer? _rayLine;
        private Transform? _rayDot;
        private Material? _rayMat;
        private Material? _rayDotMat;

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
                _grabbing = false;
                BuildViz();
                Debug.Log("[ZoneCalib] 校正モード ON — レイ先 A=ゾーン選択, 右トリガ握り=床ドラッグ移動, " +
                          "右スティック=サイズ(横/縦), X=保存, Y=リセット, 両グリップ長押し=終了");
            }
            else
            {
                _grabbing = false;
                TearDownViz();
                Debug.Log("[ZoneCalib] 校正モード OFF");
            }
        }

        /// <summary>Bridge から毎フレーム呼ばれる（校正モード中のみ）。</summary>
        public void Feed(in CalibInput input)
        {
            if (!IsActive || zones.Length == 0) return;

            if (input.save) { Save(); return; }
            if (input.reset) { ResetToAuthored(); return; }

            // 床へのレイヒットを計算（ハイライト・選択・ドラッグの共通入力）
            _hasHit = TryFloorHit(out _hit);
            _hovered = _hasHit ? PickZoneAt(_hit) : -1;

            // A: レイ先のゾーンを選択
            if (input.pick && _hovered >= 0)
            {
                _selected = _hovered;
                Debug.Log($"[ZoneCalib] 選択: {zones[_selected].Label} (cam {zones[_selected].CameraIndex})");
            }

            var z = zones[_selected];
            Vector3 center = z.Center;
            Vector3 half = z.HalfExtents;

            // 右トリガ握り中: 選択ゾーンをレイ先へドラッグ（掴んだ瞬間の相対位置を維持して追従）
            if (input.grab && _hasHit)
            {
                if (!_grabbing)
                {
                    _grabbing = true;
                    _grabOffsetXZ = new Vector2(center.x - _hit.x, center.z - _hit.z);
                }
                center.x = _hit.x + _grabOffsetXZ.x;
                center.z = _hit.z + _grabOffsetXZ.y;
            }
            else
            {
                _grabbing = false;
            }

            // 右スティック: 横倒し=x（横幅）、縦倒し=z（奥行き）を伸縮（デッドゾーン付き）
            Vector2 sz = input.size.sqrMagnitude > 0.01f ? input.size : Vector2.zero;
            if (sz != Vector2.zero)
            {
                float spd = resizeSpeed * Time.deltaTime;
                half.x = Mathf.Max(minHalfExtent, half.x + sz.x * spd);
                half.z = Mathf.Max(minHalfExtent, half.z + sz.y * spd);
            }

            z.SetRuntimeBounds(center, half);
        }

        private Transform? RayOrigin()
        {
            if (rightHandTransform != null) return rightHandTransform;
            if (headTransform != null) return headTransform;
            return Camera.main != null ? Camera.main.transform : null;
        }

        // レイ起点の forward 方向と床平面 (y = _recenterOffset.y = 0) の交点。
        // 下向き成分が無い（上向き・水平）ならヒットなし。
        private bool TryFloorHit(out Vector3 hit)
        {
            hit = Vector3.zero;
            Transform? origin = RayOrigin();
            if (origin == null) return false;
            Vector3 o = origin.position;
            Vector3 d = origin.forward;
            if (d.y >= -1e-4f) return false;          // 上向き／水平はヒットなし
            float floorY = 0f;                        // ゾーンは床基準。recenter は XZ のみなので床は y=0
            float t = (floorY - o.y) / d.y;
            if (t <= 0f) return false;
            hit = o + d * t;
            return true;
        }

        // 床ヒット点の XZ を含むゾーン。重なりは Priority 大が勝ち、同値なら配列先頭。
        private int PickZoneAt(Vector3 worldHit)
        {
            int best = -1;
            int bestPriority = int.MinValue;
            for (int i = 0; i < zones.Length; i++)
            {
                var z = zones[i];
                Vector3 c = z.Center;
                if (Mathf.Abs(worldHit.x - c.x) <= z.HalfExtents.x &&
                    Mathf.Abs(worldHit.z - c.z) <= z.HalfExtents.z)
                {
                    if (z.Priority > bestPriority) { bestPriority = z.Priority; best = i; }
                }
            }
            return best;
        }

        private void Update()
        {
            if (recenterOnStart && !_recenterDone) TryRecenter();
            if (IsActive) UpdateViz();
        }

        // 起動時の立ち位置基準化。HMD のトラッキングが有効になる（位置が原点から動く）のを
        // 待ってから recenterDelaySec 経過後に 1 回だけ実行する。
        private void TryRecenter()
        {
            Transform? head = headTransform;
            if (head == null && Camera.main != null) head = Camera.main.transform;
            if (head == null) return;

            // 起動直後はポーズ未確定で (0,0,0) 付近に座っていることがある。
            // 高さ（y）が現実的な値になったらトラッキング確立とみなす。
            if (head.position.y < 0.3f) { _recenterTimer = 0f; return; }

            _recenterTimer += Time.deltaTime;
            if (_recenterTimer < recenterDelaySec) return;

            _recenterDone = true;
            var delta = new Vector3(head.position.x, 0f, head.position.z) - _recenterOffset;
            ShiftAllZones(delta);
            _recenterOffset += delta;
            Debug.Log($"[ZoneCalib] 起動位置基準化: レイアウト原点を ({_recenterOffset.x:F2}, {_recenterOffset.z:F2}) へ平行移動");
        }

        private void ShiftAllZones(Vector3 delta)
        {
            if (delta.sqrMagnitude < 1e-8f) return;
            foreach (var z in zones)
                z.SetRuntimeBounds(z.Center + delta, z.HalfExtents);
        }

        // ---- 永続化 -------------------------------------------------------------

        private void Save()
        {
            var data = new SaveData();
            // JSON は常にレイアウト原点相対で保存（起動位置基準化のオフセットを除去）。
            // セッション毎に立ち位置が変わっても保存済みレイアウトの形・相対配置が保たれる。
            foreach (var z in zones)
                data.entries.Add(new Entry { name = z.name, center = z.Center - _recenterOffset, halfExtents = z.HalfExtents });
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
                        z.SetRuntimeBounds(e.center + _recenterOffset, e.halfExtents);
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
                zones[i].SetRuntimeBounds(_authored[i].center + _recenterOffset, _authored[i].half);
            try { if (File.Exists(SavePath)) File.Delete(SavePath); }
            catch (Exception ex) { Debug.LogWarning($"[ZoneCalib] 保存ファイル削除失敗: {ex.Message}"); }
            Debug.Log("[ZoneCalib] authored 値へリセット（保存ファイルも削除）");
        }

        // ---- 可視化（床フットプリント + レイ）-----------------------------------

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

            // コントローラ → 床のレイ（校正モード限定で表示）
            var lineGo = new GameObject("CalibRay");
            lineGo.transform.SetParent(_vizRoot.transform, worldPositionStays: false);
            _rayLine = lineGo.AddComponent<LineRenderer>();
            _rayMat = new Material(shader) { color = new Color(0.4f, 1f, 1f, 0.9f) };
            _rayLine.sharedMaterial = _rayMat;
            _rayLine.useWorldSpace = true;
            _rayLine.widthMultiplier = 0.006f;
            _rayLine.numCapVertices = 2;
            _rayLine.positionCount = 2;
            _rayLine.textureMode = LineTextureMode.Stretch;
            _rayLine.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            // 床ヒット点のドット
            var dot = GameObject.CreatePrimitive(PrimitiveType.Quad);
            dot.name = "RayDot";
            dot.transform.SetParent(_vizRoot.transform, worldPositionStays: false);
            dot.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            dot.transform.localScale = new Vector3(0.12f, 0.12f, 1f);
            var dcol = dot.GetComponent<Collider>();
            if (dcol != null) Destroy(dcol);
            _rayDotMat = new Material(shader) { color = new Color(0.4f, 1f, 1f, 0.95f) };
            dot.GetComponent<Renderer>().sharedMaterial = _rayDotMat;
            _rayDot = dot.transform;
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
                bool hovered = i == _hovered;
                float alpha = selected
                    ? Mathf.Lerp(0.35f, 0.8f, Mathf.PingPong(Time.time * 2f, 1f)) // 選択中はブリンク
                    : hovered ? 0.45f                                              // レイで指している
                    : 0.2f;
                _vizMats[i].color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
            }

            if (_headMarker != null)
            {
                Transform? head = headTransform;
                if (head == null && Camera.main != null) head = Camera.main.transform;
                if (head != null)
                    _headMarker.position = new Vector3(head.position.x, 0.03f + zones.Length * 0.002f, head.position.z);
            }

            // レイ表示（origin → 床ヒット点 or 行き止まり）
            Transform? rayOrigin = RayOrigin();
            if (_rayLine != null && rayOrigin != null)
            {
                Vector3 start = rayOrigin.position;
                Vector3 end = _hasHit ? _hit : start + rayOrigin.forward * 5f;
                _rayLine.SetPosition(0, start);
                _rayLine.SetPosition(1, end);

                Color rc = _hasHit ? new Color(0.4f, 1f, 1f, 0.95f) : new Color(1f, 0.5f, 0.4f, 0.6f);
                if (_rayMat != null) _rayMat.color = rc;
            }

            if (_rayDot != null)
            {
                if (_hasHit)
                {
                    _rayDot.gameObject.SetActive(true);
                    _rayDot.position = new Vector3(_hit.x, 0.025f + zones.Length * 0.002f, _hit.z);
                    // 掴み中は緑寄り、通常は水色
                    if (_rayDotMat != null)
                        _rayDotMat.color = _grabbing ? new Color(0.4f, 1f, 0.5f, 0.95f) : new Color(0.4f, 1f, 1f, 0.95f);
                }
                else
                {
                    _rayDot.gameObject.SetActive(false);
                }
            }
        }

        private void TearDownViz()
        {
            if (_vizRoot != null) Destroy(_vizRoot);
            foreach (var m in _vizMats) if (m != null) Destroy(m);
            if (_rayMat != null) Destroy(_rayMat);
            if (_rayDotMat != null) Destroy(_rayDotMat);
            _vizRoot = null;
            _vizQuads = Array.Empty<Transform>();
            _vizMats = Array.Empty<Material>();
            _headMarker = null;
            _rayLine = null;
            _rayDot = null;
            _rayMat = null;
            _rayDotMat = null;
            _hovered = -1;
            _hasHit = false;
        }

        private void OnDestroy() => TearDownViz();
    }
}
