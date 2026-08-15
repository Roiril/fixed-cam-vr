#nullable enable
using FixedCamVr.Streaming;
using TMPro;
using UnityEngine;

namespace FixedCamVr.Diagnostics
{
    /// <summary>
    /// <b>上司からの連絡（第 2 の面）。</b> 本編のスクリーンとは別に、少し手前・少し外側に立てる。
    ///
    /// 判定は <c>canon/LEDGER.md</c> 0043（ユーザー逐語）:
    /// 「せっかく VR で立体的なので、スクリーンにつけなくていい。スクリーンよりも少し体験者に近く
    /// かつすこし外側に、新しいスクリーンとして設置するでもいいと思う」。
    /// 実装の順序と未確定は <c>.claude/plans/2026-08-15_comms-panel.md</c>。
    ///
    /// ⚠⚠ <b>これは仮実装</b>（2026-08-15）。出るのは<b>本編に入って一定秒後に 1 回だけ</b>で、
    /// 文面もコードが持っている。show.json への著作（take の並列チャンネル）は次の段。
    ///
    /// ⚠ <b>追従は本編のスクリーンと同じ法則</b>（<see cref="YawFollowLogic"/>・ヨーだけ）。
    /// 新しい追従を書かない — 体験の中で追従の癖が 2 種類になると、どちらも「板」に見える。
    ///
    /// ⚠ <b>読まなくても体験は進む。</b> 既読の操作は作らない（体験者が持つ唯一の入力 ＝ 左 X は
    /// 記録専用で、兼用すると押した時刻の意味が濁る）。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class CommsPanel : MonoBehaviour
    {
        [Tooltip("体験の骨格。本編に入ったことを見るために読む。null なら実行時に探す。")]
        [SerializeField] private ShowRunDirector? runDirector;

        [Tooltip("頭の Transform。null なら CenterEyeAnchor を名前で探す。")]
        [SerializeField] private Transform? head;

        // ---- 置き場所。**const**（SerializeField にすると既存シーンの YAML で 0 に読まれる）----
        /// <summary>頭からの距離 (m)。本編のスクリーンは 2.0m なので<b>0.5m 手前</b>。</summary>
        private const float DistanceM = 1.5f;

        /// <summary>
        /// 視線中心から外側へ振る角度（度）。<b>負が左</b>（ユーザーの図が左下だった）。
        /// ⚠ スクリーンは見かけ 55° 前後あるので、20° では半分ほど重なる。
        /// 重ねたくないなら 30° 以上へ振るが、そこまで外だと視界の端で読みにくい。
        /// </summary>
        private const float YawOffsetDeg = -20f;

        /// <summary>視線中心から下へ振る角度（度）。</summary>
        private const float PitchOffsetDeg = 10f;

        /// <summary>面の幅 (m)。1.5m 先で 0.76m ＝ <b>見かけ 28°</b>。</summary>
        private const float PanelW = 0.76f;
        /// <summary>面の高さ (m)。1.5m 先で 0.26m ＝ 見かけ 10°。</summary>
        private const float PanelH = 0.26f;
        /// <summary>縁の張り出し (m)。地より一回り大きい面を裏に置いて枠に見せる。</summary>
        private const float BezelM = 0.012f;

        /// <summary>
        /// 文字の拡大率。<b>fontSize ではなく scale で掛ける</b>（fontSize を上げると
        /// メッシュの座標だけ広がる — <c>canon/LEDGER.md</c> 0035）。
        ///
        /// ⚠⚠ <b>初版は 0.34 で、実機の画で 1 文字 0.9° にしかならず読めなかった</b>（2026-08-15 実測）。
        /// VR の日本語は <b>1 文字 1.5° 以上</b>が目安。0.67 なら 1.5m 先で 1 文字 1.8°。
        /// </summary>
        private const float TextScale = 0.67f;

        // ---- 追従（`ScreenAnchor` / `TitleScreen` と同じ値。片方だけ変えない）----
        private const float YawDeadzoneDeg = 0.5f;
        private const float YawTrailDeg = 0f;
        private const float SmoothTimeSec = 0.30f;
        private const float MaxYawSpeedDegPerSec = 110f;
        private const float CatchUpThresholdDeg = 45f;
        private const float CatchUpBoost = 2f;
        private const float ResumeGapSec = 0.5f;

        /// <summary>⚠ 5000 を超えると URP の透明パスに入らず 1 画素も出ない（2026-07-31 実害）。</summary>
        private const int RenderQueue = 4980;
        private const int GlyphQueue = 4990;

        /// <summary>仮の発火。本編に入ってからこれだけ経つと 1 回だけ出る。</summary>
        private const float FireAfterRunSec = 12f;

        /// <summary>
        /// 仮の文面。<b>身体を操作する指示にしない</b>（<c>canon/LEDGER.md</c> 0034 —
        /// 「右手をあげてください」を伏線にするのはスマートではない）。即時の業務指示に留める。
        /// ⚠ 文言を変えたら <c>menu hud-font</c> を再実行する（静的ベイクなので忘れると豆腐）。
        /// </summary>
        /// ⚠ <b>1 行は 13 文字まで</b>（面の幅 28° に 1 文字 1.8° で入る数）。超えると折り返して面から出る。
        private const string NoticeText = "観測を継続してください\n異常を認めた場合のみ記録を";

        private readonly CommsPanelLogic _logic = new CommsPanelLogic();
        private readonly YawFollowLogic _yawFollow = new YawFollowLogic();

        private Transform? _root;
        private MeshRenderer? _panelRenderer;
        private MeshRenderer? _bezelRenderer;
        private Material? _panelMat;
        private Material? _bezelMat;
        private Mesh? _panelMesh;
        private TMP_Text? _text;
        private bool _yawSeeded;
        private bool _fired;
        private float _runSec;

        /// <summary>実体を組めたか。<b>false なら一生出ない</b>（テレメトリが読む）。</summary>
        public bool IsBuilt => _text != null;

        /// <summary>いまの段（テレメトリ用）。</summary>
        public CommsStage Stage => _logic.Stage;

        /// <summary>直近に書いた文字の不透明度（「画に出た」側の観測）。</summary>
        public float AppliedGlyph { get; private set; }

        /// <summary>
        /// 連絡が届いた回数。<b>増えた瞬間に左コントローラを震わせる</b>のは
        /// <c>OvrControllerBridge</c>（Streaming / Diagnostics から OVR を触らない規約）。
        /// </summary>
        public int PulseCount { get; private set; }

        private void Awake()
        {
            ResolveRefs();
            Build();
            Apply(CommsWeights.Hidden);
        }

        private void OnDisable()
        {
            _logic.Disable();
            Apply(CommsWeights.Hidden);
        }

        private void OnDestroy()
        {
            if (_panelMat != null) Destroy(_panelMat);
            if (_bezelMat != null) Destroy(_bezelMat);
            if (_panelMesh != null) Destroy(_panelMesh);
        }

        private void ResolveRefs()
        {
            if (runDirector == null) runDirector = FindObjectOfType<ShowRunDirector>();
            if (head == null)
            {
                var anchor = GameObject.Find("CenterEyeAnchor");
                if (anchor != null) head = anchor.transform;
            }
        }

        /// <summary>連絡を出す（仮実装では自分で叩く。将来は show.json の著作から）。</summary>
        public void Deliver()
        {
            if (!IsBuilt) return;
            _logic.Begin();
            PulseCount++;
            Debug.Log("[Comms] 上司からの連絡を出した（仮の発火）");
        }

        private void Update()
        {
            if (!IsBuilt) return;

            // 本編に入ってからの経過で 1 回だけ出す（仮）。ラン開始で相が Intro へ戻るので、
            // 次の体験者にも出る（＝ 体験 1 回ぶんの状態をここで落としている）。
            bool inRun = runDirector != null && runDirector.Phase == ShowPhase.Run;
            if (!inRun) { _runSec = 0f; _fired = false; }
            else
            {
                _runSec += Time.unscaledDeltaTime;
                if (!_fired && _runSec >= FireAfterRunSec) { _fired = true; Deliver(); }
            }

            _logic.Tick(Time.unscaledDeltaTime);
            Apply(_logic.Weights);
        }

        private void LateUpdate()
        {
            if (!IsBuilt || !_logic.Active || head == null || _root == null) return;

            float dt = Time.unscaledDeltaTime;
            float headYaw = head.eulerAngles.y;
            if (!_yawSeeded || dt > ResumeGapSec)
            {
                _yawFollow.Reseat(_yawSeeded ? _yawFollow.CurrentYaw : headYaw);
                _yawSeeded = true;
            }
            else
            {
                _yawFollow.Step(headYaw, dt, YawDeadzoneDeg, YawTrailDeg, SmoothTimeSec,
                                MaxYawSpeedDegPerSec, CatchUpThresholdDeg, CatchUpBoost);
            }

            // 追従したヨーから見て「外側へ振って、下げて、手前に置く」。
            // ⚠ 面は体験者の方を向ける（板が斜めを向いていると読めない）。
            Quaternion yaw = Quaternion.Euler(0f, _yawFollow.CurrentYaw + YawOffsetDeg, 0f);
            Vector3 dir = yaw * Quaternion.Euler(PitchOffsetDeg, 0f, 0f) * Vector3.forward;
            _root.position = head.position + dir * DistanceM;
            _root.rotation = Quaternion.LookRotation(_root.position - head.position, Vector3.up);
        }

        private void Build()
        {
            if (_text != null) return;
            var jp = JapaneseHudFont.TryGet();
            if (jp == null)
            {
                Debug.LogWarning("[Comms] 日本語フォントを解決できないので連絡の面は出しません");
                return;
            }

            var rootGo = new GameObject("CommsRoot");
            rootGo.transform.SetParent(transform, worldPositionStays: false);
            _root = rootGo.transform;

            // 地（受信票の面）。⚠ 標準シェーダが見つからなければ**文字だけ**にする
            //    （面が無くても読めるので、体験は止めない）。
            Shader? flat = Shader.Find("Unlit/Color");
            if (flat != null)
            {
                _panelMesh = BuildQuad();
                // 縁（裏の一回り大きい面）。⚠ **地だけだと真っ黒の中で面が消える**
                //    （2026-08-15 の実機の画で、文字だけが宙に浮いていた）。
                _bezelRenderer = MakeQuad(rootGo.transform, "CommsBezelQuad",
                                          PanelW + BezelM * 2f, PanelH + BezelM * 2f, 0.014f,
                                          flat, RenderQueue - 1, out _bezelMat);
                // 地。暗い漆のような面。純黒だと「穴」に見え、明るいと掲示物に見える。
                _panelRenderer = MakeQuad(rootGo.transform, "CommsPanelQuad",
                                          PanelW, PanelH, 0.012f,
                                          flat, RenderQueue, out _panelMat);
            }

            var textGo = new GameObject("CommsText");
            textGo.transform.SetParent(rootGo.transform, worldPositionStays: false);
            var tmp = textGo.AddComponent<TextMeshPro>();
            tmp.font = jp;
            tmp.text = NoticeText;
            tmp.alignment = TextAlignmentOptions.Center;
            tmp.fontSize = 0.07f;
            tmp.enableWordWrapping = true;
            tmp.richText = false;
            tmp.color = new Color(0.82f, 0.78f, 0.72f, 1f);
            var rt = (RectTransform)textGo.transform;
            rt.sizeDelta = new Vector2(1.0f, 0.34f);
            // ⚠ 大きさは scale で掛ける（fontSize を上げるとメッシュの座標だけ広がる — LEDGER 0035）。
            textGo.transform.localScale = Vector3.one * TextScale;
            var overlay = Shader.Find("TextMeshPro/Distance Field Overlay");
            if (overlay != null) tmp.fontMaterial.shader = overlay;
            tmp.fontMaterial.renderQueue = GlyphQueue;
            _text = tmp;
        }

        /// <summary>面を 1 枚作る（地と縁で共有）。色は <see cref="Apply"/> が毎フレーム書く。</summary>
        private MeshRenderer MakeQuad(Transform parent, string name, float w, float h, float z,
                                      Shader shader, int queue, out Material mat)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            go.transform.localPosition = new Vector3(0f, 0f, z);   // 文字より奥
            go.transform.localScale = new Vector3(w, h, 1f);
            go.AddComponent<MeshFilter>().sharedMesh = _panelMesh;
            var r = go.AddComponent<MeshRenderer>();
            mat = new Material(shader) { name = name + " (runtime)", renderQueue = queue };
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            r.enabled = false;
            return r;
        }

        private static Mesh BuildQuad()
        {
            var m = new Mesh { name = "CommsPanelQuad" };
            m.SetVertices(new[]
            {
                new Vector3(-0.5f, -0.5f, 0f), new Vector3(0.5f, -0.5f, 0f),
                new Vector3(-0.5f, 0.5f, 0f), new Vector3(0.5f, 0.5f, 0f),
            });
            m.SetUVs(0, new[]
            {
                new Vector2(0f, 0f), new Vector2(1f, 0f), new Vector2(0f, 1f), new Vector2(1f, 1f),
            });
            m.SetTriangles(new[] { 0, 2, 1, 2, 3, 1 }, 0);
            m.RecalculateBounds();
            return m;
        }

        private void Apply(in CommsWeights w)
        {
            AppliedGlyph = Mathf.Clamp01(w.glyph);
            if (_text != null)
            {
                _text.alpha = AppliedGlyph;
                bool on = AppliedGlyph > 0.002f;
                if (_text.gameObject.activeSelf != on) _text.gameObject.SetActive(on);
            }
            float pa = Mathf.Clamp01(w.panel);
            // Unlit/Color は alpha を持たないので、明るさで濃さを出す（暗い場所なので十分）。
            if (_panelRenderer != null && _panelMat != null)
            {
                _panelMat.color = new Color(0.050f * pa, 0.042f * pa, 0.038f * pa, 1f);
                _panelRenderer.enabled = pa > 0.01f;
            }
            if (_bezelRenderer != null && _bezelMat != null)
            {
                // 縁は地より明るい。ここだけが「面がある」ことを伝える。
                _bezelMat.color = new Color(0.150f * pa, 0.110f * pa, 0.085f * pa, 1f);
                _bezelRenderer.enabled = pa > 0.01f;
            }
        }
    }
}
