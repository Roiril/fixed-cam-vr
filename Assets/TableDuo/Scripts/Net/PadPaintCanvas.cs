#nullable enable
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

namespace TableDuoVr.Net
{
    /// <summary>
    /// 「あと6画のくま」の描画パッド。ペン先/消しゴムがパッド面に触れている間、線を RT に焼いて
    /// 全 peer 同期で描画/消去する（in-scene NetworkObject「BEAR_paint」）。
    ///
    /// 権威モデル: サーバが各ツールの接触を判定して線分を <see cref="DrawSegmentClientRpc"/> で全 peer へ配る
    /// （host も自分の RPC を受けて描く = サーバは別途ローカル描画しない）。RT へのスタンプは各 peer の
    /// TableDuoVr/PadStamp シェーダ + CommandBuffer で完結（カメラ非経由のオフスクリーン）。
    /// サーバはセグメントを記録し、遅参加クライアントへ <see cref="ReplaySegmentsClientRpc"/> で再送する。
    ///
    /// クリア契機（サーバ）: GameSwitcher の切替 / BoardReset。値は TableDuoSceneSetup が焼き込む。
    /// </summary>
    public sealed class PadPaintCanvas : NetworkBehaviour
    {
        // RT 解像度（パッド 210:297 とアスペクト一致: 1024*297/210 ≈ 1448）
        private const int RtWidth = 1024;
        private const int RtHeight = 1448;
        // 遅参加リプレイの 1 RPC あたりのセグメント数
        private const int ReplayBatch = 128;
        // ペン色（coral #D95B52 / teal #14766F）。toolId 0/1 のスタンプ材質へ焼く
        private static readonly Color Coral = new Color(217f / 255f, 91f / 255f, 82f / 255f, 1f);
        private static readonly Color Teal = new Color(20f / 255f, 118f / 255f, 111f / 255f, 1f);

        [Tooltip("描画パッド（BEAR_pad）。接触判定はこのローカル空間で行う")]
        [SerializeField] private Transform? padTransform;
        [Tooltip("パッド上面のオーバーレイ quad（BEAR_paint_layer）。この material インスタンスへ RT を割り当てる")]
        [SerializeField] private Renderer? overlayRenderer;
        [Tooltip("描画ツール（ペン2 + 消しゴム）。ToolId=索引 0/1/2 に一致させる")]
        [SerializeField] private PadDrawTool[] tools = System.Array.Empty<PadDrawTool>();
        [Tooltip("スタンプシェーダ（TableDuoVr/PadStamp）")]
        [SerializeField] private Shader? stampShader;

        // --- ランタイム（全 peer） ---
        private RenderTexture? _rt;
        private CommandBuffer? _cmd;
        private Mesh? _quad;
        // toolId 索引: 0=coral(pass0) / 1=teal(pass0) / 2=erase(pass1)
        private readonly Material?[] _stampMats = new Material?[3];
        private static readonly int[] Passes = { 0, 0, 1 };
        private readonly float[] _radiiById = { 2.2f, 2.2f, 10f };
        // 今フレーム描くべき保留セグメント（ClientRpc 受信 + 保持者ローカル予測でここへ積み、Update 末尾で一括描画）
        private readonly List<PadPaintLogic.PaintSegment> _pending = new();
        private bool _clearRequested;

        // --- サーバ専用 ---
        private Grabbable?[] _serverGrabs = System.Array.Empty<Grabbable?>();
        private Vector2[] _lastUv = System.Array.Empty<Vector2>();
        private PadPaintLogic.ContactGate[] _gates = System.Array.Empty<PadPaintLogic.ContactGate>();
        private bool _serverBound;

        // --- 保持者ローカル即時インク（非サーバ・Part C） ---
        // ローカルが保持するツールを毎フレ接触判定して即描画し、二重遅延（pose 上り→サーバ判定→RPC 下り）を消す。
        // サーバ経由の同一セグメント ClientRpc は予測中/終了直後 0.75s 破棄する（エコー抑止）。
        private Grabbable?[] _localGrabs = System.Array.Empty<Grabbable?>();
        private PadPaintLogic.ContactGate[] _localGates = System.Array.Empty<PadPaintLogic.ContactGate>();
        private Vector2[] _localLastUv = System.Array.Empty<Vector2>();
        private bool _clientBound;
        // toolId 索引（0..2）: 予測中フラグと予測終了時刻。エコー抑止判定に使う
        //（終了時刻は −∞ 初期化。0 だと起動直後 0.75s 間、他 peer のセグメントまで誤って抑止される）
        private readonly bool[] _predicting = new bool[3];
        private readonly float[] _predictEndTime =
            { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };
        private readonly List<PadPaintLogic.PaintSegment> _buffer = new();
        private GameSwitcher? _switcher;
        private int _lastActiveIndex;
        private SessionLogger? _logger;
        private readonly ulong[] _replayTarget = new ulong[1];

        public override void OnNetworkSpawn()
        {
            SetupRenderResources();
            // サーバ配線（tool→Grabbable 解決・GameSwitcher/SessionLogger 探索・イベント購読）は
            // 他オブジェクトの spawn 完了に依存するため、最初の Update で遅延バインドする（ServerBind）。
        }

        private void SetupRenderResources()
        {
            _rt = new RenderTexture(RtWidth, RtHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "BEAR_paint_rt",
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();
            ClearRtImmediate();

            if (overlayRenderer != null)
            {
                var mat = overlayRenderer.material; // インスタンス化（peer ごと）
                mat.SetTexture("_BaseMap", _rt);
                mat.mainTexture = _rt;
                // ベイク側は「テクスチャ未割当の白 quad がパッドを覆う」防止で alpha 0 で焼いてある。
                // RT を割り当てた今、表示を有効化（RT 自体は透明クリア済みなので見た目は変わらない）
                mat.SetColor("_BaseColor", Color.white);
                mat.color = Color.white;
            }

            _cmd = new CommandBuffer { name = "PadPaintStamp" };
            _quad = BuildQuad();

            if (stampShader != null)
            {
                _stampMats[0] = new Material(stampShader); _stampMats[0]!.SetColor("_Color", Coral);
                _stampMats[1] = new Material(stampShader); _stampMats[1]!.SetColor("_Color", Teal);
                _stampMats[2] = new Material(stampShader); // erase（pass1・色は使わない）
            }
            else
            {
                Debug.LogWarning($"[TableDuo] PadPaintCanvas {name}: stampShader 未設定（描画無効）");
            }

            // toolId → 半径（各 peer が同じ焼き込み値を持つ）
            for (int i = 0; i < tools.Length; i++)
            {
                var t = tools[i];
                if (t == null) continue;
                int id = t.ToolId;
                if (id >= 0 && id < _radiiById.Length) _radiiById[id] = t.RadiusMm;
            }
        }

        public override void OnNetworkDespawn()
        {
            TeardownServer();
            ReleaseRenderResources();
        }

        private void OnDestroy()
        {
            TeardownServer();
            ReleaseRenderResources();
        }

        // ---------------- サーバ配線 ----------------

        private void ServerBind()
        {
            int n = tools.Length;
            _serverGrabs = new Grabbable?[n];
            _lastUv = new Vector2[n];
            _gates = new PadPaintLogic.ContactGate[n];
            for (int i = 0; i < n; i++)
                _serverGrabs[i] = tools[i] != null ? tools[i].GetComponent<Grabbable>() : null;

            _switcher = FindObjectOfType<GameSwitcher>();
            _lastActiveIndex = _switcher != null ? _switcher.ActiveIndex : -1;
            _logger = FindObjectOfType<SessionLogger>();

            BoardReset.AfterReset += OnAfterReset;
            var nm = NetworkManager.Singleton;
            if (nm != null) nm.OnClientConnectedCallback += OnClientConnected;
            _serverBound = true;
        }

        private void TeardownServer()
        {
            if (!_serverBound) return;
            _serverBound = false;
            BoardReset.AfterReset -= OnAfterReset;
            var nm = NetworkManager.Singleton;
            if (nm != null) nm.OnClientConnectedCallback -= OnClientConnected;
        }

        // ---------------- フレーム処理 ----------------

        private void Update()
        {
            if (!IsSpawned) return;

            if (IsServer)
            {
                if (!_serverBound) ServerBind();
                ServerTick();
            }
            else
            {
                if (!_clientBound) ClientBind();
                ClientPredictTick();
            }
            FlushPending();
        }

        // 非サーバ配線: ローカル予測用の tool→Grabbable 解決（server の ServerBind と対称・遅延バインド）
        private void ClientBind()
        {
            int n = tools.Length;
            _localGrabs = new Grabbable?[n];
            _localGates = new PadPaintLogic.ContactGate[n];
            _localLastUv = new Vector2[n];
            for (int i = 0; i < n; i++)
                _localGrabs[i] = tools[i] != null ? tools[i].GetComponent<Grabbable>() : null;
            _clientBound = true;
        }

        // 保持者ローカル即時インク（非サーバ）: local client が保持するツールを接触判定し、_pending へ直接積む。
        // transform は ToolGripDriver（LateUpdate order120）が前フレームに置いた値を読む（1 フレーム遅れは許容）。
        private void ClientPredictTick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || padTransform == null) return;
            ulong localId = nm.LocalClientId;

            for (int i = 0; i < tools.Length; i++)
            {
                var tool = tools[i];
                var grab = i < _localGrabs.Length ? _localGrabs[i] : null;
                int toolId = tool != null ? tool.ToolId : -1;
                if (toolId < 0 || toolId >= _predicting.Length) continue;

                // 確定保持のみ予測対象（楽観保持中＝未確定は含めない）。両手を確認
                bool heldLocal = tool != null && grab != null
                    && (grab.IsHeldBy(localId, 0) || grab.IsHeldBy(localId, 1));

                bool inXz = false;
                Vector2 uv = default;
                float h = 0f;
                if (heldLocal)
                {
                    Vector3 local = padTransform.InverseTransformPoint(tool!.ContactPoint);
                    inXz = PadPaintLogic.TryGetUv(local, out uv, out h);
                }

                bool was = _localGates[i].Contacting;
                bool now = _localGates[i].Tick(heldLocal && inXz, h);
                if (now)
                {
                    _predicting[toolId] = true;
                    byte tid = (byte)toolId;
                    if (!was)
                    {
                        _localLastUv[i] = uv;
                        _pending.Add(new PadPaintLogic.PaintSegment { a = uv, b = uv, tool = tid });
                    }
                    else if (PadPaintLogic.ShouldEmit(_localLastUv[i], uv))
                    {
                        _pending.Add(new PadPaintLogic.PaintSegment { a = _localLastUv[i], b = uv, tool = tid });
                        _localLastUv[i] = uv;
                    }
                }
                else if (was)
                {
                    _predicting[toolId] = false;
                    _predictEndTime[toolId] = Time.time; // エコー抑止の起点
                }
            }
        }

        private void ServerTick()
        {
            // GameSwitcher 切替でクリア
            if (_switcher != null)
            {
                int idx = _switcher.ActiveIndex;
                if (idx != _lastActiveIndex)
                {
                    _lastActiveIndex = idx;
                    ServerClear();
                }
            }

            if (padTransform == null) return;

            for (int i = 0; i < tools.Length; i++)
            {
                var tool = tools[i];
                var grab = i < _serverGrabs.Length ? _serverGrabs[i] : null;
                bool held = tool != null && grab != null && grab.IsHeld;

                bool inXz = false;
                Vector2 uv = default;
                float h = 0f;
                if (held)
                {
                    Vector3 local = padTransform.InverseTransformPoint(tool!.ContactPoint);
                    inXz = PadPaintLogic.TryGetUv(local, out uv, out h);
                }

                bool was = _gates[i].Contacting;
                bool now = _gates[i].Tick(held && inXz, h); // ヒステリシス（DownTol/UpTol）で端の欠けを消す
                if (now)
                {
                    byte toolId = (byte)tool!.ToolId;
                    if (!was)
                    {
                        _lastUv[i] = uv;
                        EmitSegment(uv, uv, toolId); // 触れた瞬間に点を打つ
                        _logger?.LogEvent("draw_start", tool.name);
                    }
                    else if (PadPaintLogic.ShouldEmit(_lastUv[i], uv))
                    {
                        EmitSegment(_lastUv[i], uv, toolId);
                        _lastUv[i] = uv;
                    }
                }
                else if (was)
                {
                    _logger?.LogEvent("draw_end", tool != null ? tool.name : $"tool{i}");
                }
            }
        }

        /// <summary>サーバ: 線分を全 peer へ配り、記録バッファへ積む（上限超過は記録のみ止める）。</summary>
        private void EmitSegment(Vector2 a, Vector2 b, byte toolId)
        {
            DrawSegmentClientRpc(a, b, toolId);
            PadPaintLogic.TryAdd(_buffer, new PadPaintLogic.PaintSegment { a = a, b = b, tool = toolId });
        }

        private void ServerClear()
        {
            _buffer.Clear();
            ClearClientRpc();
        }

        private void OnAfterReset()
        {
            if (IsServer) ServerClear();
        }

        private void OnClientConnected(ulong clientId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || clientId == nm.LocalClientId) return; // host 自身は既に描いている
            if (_buffer.Count == 0) return;

            _replayTarget[0] = clientId;
            var target = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = _replayTarget } };
            for (int start = 0; start < _buffer.Count; start += ReplayBatch)
            {
                int cnt = Mathf.Min(ReplayBatch, _buffer.Count - start);
                var aArr = new Vector2[cnt];
                var bArr = new Vector2[cnt];
                var tArr = new byte[cnt];
                for (int i = 0; i < cnt; i++)
                {
                    var s = _buffer[start + i];
                    aArr[i] = s.a; bArr[i] = s.b; tArr[i] = s.tool;
                }
                ReplaySegmentsClientRpc(aArr, bArr, tArr, target);
            }
        }

        // ---------------- ClientRpc（全 peer・host 上でも実行） ----------------

        [ClientRpc]
        private void DrawSegmentClientRpc(Vector2 a, Vector2 b, byte toolId)
        {
            // 保持者本人のクライアントは既にローカル予測で描いている → サーバ経由のエコーは破棄（二重描画防止）。
            // host（IsServer）は自分の RPC を受けて描く唯一の経路なので抑止しない。
            if (!IsServer && toolId < _predicting.Length
                && PadPaintLogic.ShouldSuppressEcho(_predicting[toolId], Time.time - _predictEndTime[toolId]))
                return;
            _pending.Add(new PadPaintLogic.PaintSegment { a = a, b = b, tool = toolId });
        }

        [ClientRpc]
        private void ClearClientRpc()
        {
            _pending.Clear();
            _clearRequested = true;
        }

        [ClientRpc]
        private void ReplaySegmentsClientRpc(Vector2[] aArr, Vector2[] bArr, byte[] tArr, ClientRpcParams clientRpcParams = default)
        {
            int n = Mathf.Min(aArr.Length, Mathf.Min(bArr.Length, tArr.Length));
            for (int i = 0; i < n; i++)
                _pending.Add(new PadPaintLogic.PaintSegment { a = aArr[i], b = bArr[i], tool = tArr[i] });
        }

        // ---------------- RT スタンプ描画 ----------------

        private void FlushPending()
        {
            if (!_clearRequested && _pending.Count == 0) return;
            if (_rt == null || _cmd == null || _quad == null)
            {
                _pending.Clear();
                _clearRequested = false;
                return;
            }

            _cmd.Clear();
            _cmd.SetRenderTarget(_rt);
            if (_clearRequested) _cmd.ClearRenderTarget(false, true, Color.clear);
            if (_pending.Count > 0)
            {
                _cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.Ortho(0f, 1f, 0f, 1f, -1f, 1f));
                for (int i = 0; i < _pending.Count; i++) StampSegment(_pending[i]);
            }
            Graphics.ExecuteCommandBuffer(_cmd);

            _pending.Clear();
            _clearRequested = false;
        }

        private void StampSegment(in PadPaintLogic.PaintSegment seg)
        {
            int id = seg.tool;
            if (id < 0 || id >= _stampMats.Length) return;
            var mat = _stampMats[id];
            if (mat == null) return;
            int pass = id < Passes.Length ? Passes[id] : 0;
            float rMm = id < _radiiById.Length ? _radiiById[id] : 2.2f;

            // 線分 a→b を半径×0.5 間隔の円スタンプ列へ展開（物理 mm 空間で等間隔）
            float du = (seg.b.x - seg.a.x) * PadPaintLogic.WidthM;
            float dv = (seg.b.y - seg.a.y) * PadPaintLogic.HeightM;
            float distM = Mathf.Sqrt(du * du + dv * dv);
            float spacing = Mathf.Max(rMm / 1000f * 0.5f, 1e-4f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(distM / spacing));
            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 uv = Vector2.Lerp(seg.a, seg.b, t);
                var m = Matrix4x4.TRS(new Vector3(uv.x, uv.y, 0f), Quaternion.identity,
                    new Vector3(rMm / 210f, rMm / 297f, 1f) * 2f);
                _cmd!.DrawMesh(_quad, m, mat, 0, pass);
            }
        }

        // ---------------- リソース ----------------

        private void ClearRtImmediate()
        {
            if (_rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = _rt;
            GL.Clear(false, true, Color.clear);
            RenderTexture.active = prev;
        }

        private void ReleaseRenderResources()
        {
            if (_rt != null) { _rt.Release(); Object.Destroy(_rt); _rt = null; }
            if (_cmd != null) { _cmd.Dispose(); _cmd = null; }
            for (int i = 0; i < _stampMats.Length; i++)
            {
                var mat = _stampMats[i];
                if (mat != null) { Object.Destroy(mat); _stampMats[i] = null; }
            }
            if (_quad != null) { Object.Destroy(_quad); _quad = null; }
        }

        /// <summary>1×1 中心原点のスタンプ用 quad（uv 0..1）。</summary>
        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "PadStampQuad" };
            m.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(0.5f, 0.5f, 0f), new Vector3(-0.5f, 0.5f, 0f),
            };
            m.uv = new[] { new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(1f, 1f), new Vector2(0f, 1f) };
            m.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            m.RecalculateBounds();
            return m;
        }
    }
}
