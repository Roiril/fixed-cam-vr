#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// 固定枠スクリーン（web-compositor モデル）。
    /// Quad の Transform は authored のまま不変。映像は FixedCamVr/ScreenComposite シェーダへ
    /// テクスチャと contain-fit スケールを渡して letterbox 表示する。
    /// 旧実装の ApplyOrient（/info メタで Transform を回転・変形）は廃止 —
    /// streamer は常に正立フレームを送る（.claude/rules/streaming.md）ため回転は不要で、
    /// アスペクトはデコード済み Texture の実寸から毎フレーム追従する。
    ///
    /// 2 モード:
    ///   - registry モード: CameraStreamRegistry のアクティブ stream の Texture を毎フレ参照（複数台切替対応）
    ///   - 単一モード（互換 / オフラインテスト用）: registry 未設定なら CameraSource / overrideUrl から自前受信
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class MjpegScreen : MonoBehaviour
    {
        private static readonly int LiveTexId = Shader.PropertyToID("_LiveTex");
        private static readonly int LiveScaleId = Shader.PropertyToID("_LiveScale");
        private static readonly int UvRotStepsId = Shader.PropertyToID("_UvRotSteps");

        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private CameraSource? source;
        [SerializeField] private string overrideUrl = "";

        [Header("Fit (contain / letterbox)")]
        [Tooltip("スクリーン枠のアスペクト (W/H)。0 以下なら transform.localScale.x/y から自動取得。")]
        [SerializeField] private float screenAspectOverride = 0f;

        [Tooltip("ライブ映像の 90 度単位 UV 回転 (0-3)。streamer は正立フレームを送るので通常 0。緊急時の手動補正用。")]
        [Range(0, 3)]
        [SerializeField] private int uvRotSteps = 0;

        private MjpegStreamReceiver? _receiver;
        private Texture2D? _tex;
        private byte[]? _scratch;
        private Renderer? _renderer;
        private Material? _material;
        private Texture? _lastAssigned;
        private int _lastTexW;
        private int _lastTexH;
        private float _lastScreenAspect;

        private bool UseRegistry => registry != null;

        /// <summary>スクリーン枠のアスペクト (W/H)。</summary>
        public float ScreenAspect
        {
            get
            {
                if (screenAspectOverride > 0f) return screenAspectOverride;
                var s = transform.localScale;
                return s.y > 1e-5f ? Mathf.Abs(s.x / s.y) : 1f;
            }
        }

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            _material = _renderer.material; // インスタンス化（スクリーンは 1 枚想定）

            if (!UseRegistry)
            {
                _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                // 未初期化 Texture2D は Quest GPU で白ノイズ化する（unity_pitfalls.md）
                _tex.SetPixels(new[] { Color.black, Color.black, Color.black, Color.black });
                _tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                AssignLive(_tex);
            }
        }

        private void OnEnable()
        {
            if (UseRegistry) return;

            string url = !string.IsNullOrEmpty(overrideUrl) ? overrideUrl : source?.BuildUrl() ?? "";
            if (string.IsNullOrEmpty(url))
            {
                Debug.LogError("[MjpegScreen] No registry / CameraSource / overrideUrl set.");
                enabled = false;
                return;
            }
            Debug.Log($"[MjpegScreen] connecting: {url}");
            _receiver = new MjpegStreamReceiver(url);
            _receiver.Start();
        }

        private void OnDisable()
        {
            _receiver?.Dispose();
            _receiver = null;
        }

        private void OnDestroy()
        {
            if (_tex != null)
            {
                if (Application.isPlaying) Destroy(_tex);
                else DestroyImmediate(_tex);
                _tex = null;
            }
            if (_material != null)
            {
                if (Application.isPlaying) Destroy(_material);
                else DestroyImmediate(_material);
                _material = null;
            }
        }

        private void AssignLive(Texture tex)
        {
            if (_material == null) return;
            _material.SetTexture(LiveTexId, tex);
            _lastAssigned = tex;
            _lastTexW = 0;
            _lastTexH = 0; // 次の Update で contain スケール再計算
        }

        /// <summary>
        /// テクスチャ実寸とスクリーン枠から contain-fit スケールを計算してシェーダへ渡す。
        /// uvRotSteps が奇数（90/270 度）のときはソースの W/H が入れ替わって見えるため反転する。
        /// </summary>
        private void UpdateContainScale()
        {
            if (_material == null) return;
            var tex = _lastAssigned;
            if (tex == null) return;

            int w = tex.width, h = tex.height;
            float screenAspect = ScreenAspect;
            if (w == _lastTexW && h == _lastTexH && Mathf.Approximately(screenAspect, _lastScreenAspect)) return;
            _lastTexW = w;
            _lastTexH = h;
            _lastScreenAspect = screenAspect;

            // 2x2 placeholder（未デコード）は黒のまま全面に出しても見た目は変わらないので 1:1 扱い
            float srcAspect = (w > 4 && h > 4) ? (float)w / h : 1f;
            if ((uvRotSteps & 1) == 1) srcAspect = 1f / srcAspect;

            Vector2 scale = srcAspect < screenAspect
                ? new Vector2(srcAspect / screenAspect, 1f)
                : new Vector2(1f, screenAspect / srcAspect);

            _material.SetVector(LiveScaleId, new Vector4(scale.x, scale.y, 0f, 0f));
            _material.SetFloat(UvRotStepsId, uvRotSteps);
        }

        private void Update()
        {
            if (UseRegistry)
            {
                var active = registry!.GetActive();
                if (active == null || _material == null) return;
                var tex = active.Texture;
                if (!ReferenceEquals(tex, _lastAssigned)) AssignLive(tex);
                UpdateContainScale();
                return;
            }

            if (_receiver == null || _tex == null) return;
            // 単一スロット最新フレームを直接消費（受信側で既に「最新のみ」保持される設計）。
            if (_receiver.TryConsumeFrame(ref _scratch, out int len, out _) && len > 0 && _scratch != null)
            {
                // markNonReadable=false 維持: 連続 LoadImage で texture を再上書きするため、
                // CPU 側 readable を残しておく必要がある。
                _tex.LoadImage(_scratch, markNonReadable: false);
            }
            UpdateContainScale();
        }
    }
}
