#nullable enable
using UnityEngine;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// MJPEG をテクスチャに焼き、Renderer.material.mainTexture に流し込む。
    /// 2 モード:
    ///   - registry モード: CameraStreamRegistry のアクティブ stream の Texture を毎フレ参照（複数台切替対応）
    ///   - 単一モード（互換）: registry が未設定なら従来通り CameraSource / overrideUrl から自前で受信
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class MjpegScreen : MonoBehaviour
    {
        [SerializeField] private CameraStreamRegistry? registry;
        [SerializeField] private CameraSource? source;
        [SerializeField] private string overrideUrl = "";

        [Header("Auto Orient (fixed-cam-streamer)")]
        [Tooltip("/info の rotationDeg / アスペクトを使ってスクリーンを自動回転 + アスペクト補正する。")]
        [SerializeField] private bool autoOrient = true;

        [Tooltip("回転 / スケールを反映する Transform。null なら自身の Transform を使う。子に Content を置きたい場合に使う。")]
        [SerializeField] private Transform? orientTarget;

        [Tooltip("Z 軸回転を /info の代わりに固定値で適用する（負値 = 自動）。配信側が rotationDeg を更新しない / 画像が square で回転反映できない時用。")]
        [SerializeField] private int manualRotationDegOverride = -1;

        [Tooltip("isPortrait で動的に Z 軸補正を入れる（fixed-cam-streamer は rotationDeg を更新しない代わりに isPortrait を更新するため）。横持ち時に +portraitToLandscapeOffsetDeg を加算。")]
        [SerializeField] private bool useIsPortraitForRotation = true;

        [Tooltip("isPortrait が false (横持ち) のときに rotationDeg に加える補正角度。Inspector で 0 / 90 / -90 / 180 を試して合うものを選ぶ。")]
        [SerializeField] private int landscapeRotationOffsetDeg = 90;

        [Tooltip("正方形フレーム時に縦長 / 横長 でアスペクトを切り替える。isPortrait に応じて自動。")]
        [SerializeField] private bool autoAspectFromIsPortrait = true;

        [Tooltip("portrait (縦持ち) 時の表示アスペクト (W/H)。Pixel 7 Pro なら 9/19.5 ≒ 0.4615。")]
        [SerializeField] private float portraitAspectWPerH = 9f / 19.5f;

        [Tooltip("landscape (横持ち) 時の表示アスペクト (W/H)。Pixel 7 Pro なら 19.5/9 ≒ 2.1667。")]
        [SerializeField] private float landscapeAspectWPerH = 19.5f / 9f;

        [Tooltip("autoAspectFromIsPortrait が無効な square 時の aspect 強制値。0 以下なら 1:1。")]
        [SerializeField] private float fallbackSquareAspect = 0f;

        [Tooltip("有効なら Texture2D の実寸 (width/height) をそのまま aspect として採用する。/info 経由の値より優先（streamer 側 /info が square 固定でも、JPEG 自体が 19.5:9 等で来る場合に正しく追従するため）。Texture が未デコード時のみ /info ベースにフォールバック。")]
        [SerializeField] private bool useTextureAspect = true;

        private MjpegStreamReceiver? _receiver;
        private Texture2D? _tex;
        private byte[]? _scratch;
        private Renderer? _renderer;
        private Texture? _lastAssigned;
        private CameraStream? _lastSubscribed;
        private Quaternion _baseLocalRotation;
        private Vector3 _baseLocalScale;
        private int _lastTexW;
        private int _lastTexH;

        // MjpegScreen が「この localRotation であってほしい」と最後に決めた値。
        // ScreenAnchor 等が world rotation を上書きする際、これを掛け合わせて補正を保つ。
        private Quaternion _desiredLocalRotation = Quaternion.identity;

        /// <summary>
        /// 配信メタに基づいて MjpegScreen が orientTarget に適用したい localRotation。
        /// ScreenAnchor (head-lock) のような外部回転源が、本補正を保ったまま world 回転を
        /// 上書きする時に <c>head.rotation * DesiredLocalRotation</c> として使う。
        /// </summary>
        public Quaternion DesiredLocalRotation => _desiredLocalRotation;

        private bool UseRegistry => registry != null;

        private void Awake()
        {
            _renderer = GetComponent<Renderer>();
            if (!UseRegistry)
            {
                _tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
                _renderer.material.mainTexture = _tex;
            }

            // 初期 transform を覚えておき、Metadata 反映時にこの上に rotation/scale を適用する。
            var t = orientTarget != null ? orientTarget : transform;
            _baseLocalRotation = t.localRotation;
            _baseLocalScale = t.localScale;
            _desiredLocalRotation = _baseLocalRotation;
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
            // 単一モードで自前確保した Texture2D を解放（registry モードでは _tex は null）。
            if (_tex != null)
            {
                if (Application.isPlaying) Destroy(_tex);
                else DestroyImmediate(_tex);
                _tex = null;
            }
            UnsubscribeOrient();
        }

        private void SubscribeOrient(CameraStream stream)
        {
            if (!autoOrient) return;
            if (ReferenceEquals(stream, _lastSubscribed)) return;
            UnsubscribeOrient();
            _lastSubscribed = stream;
            stream.MetadataUpdated += OnMetadataUpdated;
        }

        private void UnsubscribeOrient()
        {
            if (_lastSubscribed != null)
            {
                _lastSubscribed.MetadataUpdated -= OnMetadataUpdated;
                _lastSubscribed = null;
            }
        }

        private void OnMetadataUpdated(StreamMetadata meta) => ApplyOrient(meta);

        /// <summary>
        /// 現在割り当てられている Texture から W/H 比を返す。useTextureAspect が無効、
        /// または Texture 未割当 / 未デコード (2x2 等の placeholder) なら 0 を返してフォールバック。
        /// </summary>
        private float TryGetTextureAspect()
        {
            if (!useTextureAspect) return 0f;
            var tex = _lastAssigned ?? _tex;
            if (tex == null) return 0f;
            int w = tex.width, h = tex.height;
            if (w <= 4 || h <= 4) return 0f;
            return (float)w / h;
        }

        /// <summary>
        /// Texture の実寸が前回適用時から変化していたら ApplyOrient を再実行する。
        /// 初回 LoadImage（2x2 → 実寸）や streamer 側で解像度が変わったときに自動追従する。
        /// </summary>
        private void ReapplyOrientIfTextureSizeChanged(CameraStream active)
        {
            if (!useTextureAspect) return;
            var tex = _lastAssigned ?? _tex;
            if (tex == null) return;
            int w = tex.width, h = tex.height;
            if (w <= 4 || h <= 4) return;
            if (w == _lastTexW && h == _lastTexH) return;
            _lastTexW = w;
            _lastTexH = h;
            var meta = active.Metadata ?? new StreamMetadata { widthPx = w, heightPx = h };
            ApplyOrient(meta);
        }

        /// <summary>
        /// rotationDeg と effective aspect を Transform に適用する。
        /// - rotation: 初期回転に Z 軸 -rotationDeg を掛ける（反時計回り = 画像を正立させる）
        /// - scale:    初期 scale から effective aspect (W/H) に合わせて X/Y を調整。Y を保ち X = Y * aspect。
        /// 既存の物理寸法を完全に保てるわけではないが、縦持ちと横持ちの切替には対応できる。
        /// </summary>
        private void ApplyOrient(StreamMetadata meta)
        {
            if (!autoOrient) return;
            var t = orientTarget != null ? orientTarget : transform;

            // 1) 回転角の決定。
            //    - manualRotationDegOverride (>=0) があれば最優先（デバッグ用）
            //    - それ以外は meta.rotationDeg を基準にしつつ、isPortrait 動的更新を見て補正
            int rot;
            if (manualRotationDegOverride >= 0)
            {
                rot = manualRotationDegOverride;
            }
            else
            {
                rot = meta.rotationDeg;
                if (useIsPortraitForRotation && !meta.isPortrait)
                {
                    rot += landscapeRotationOffsetDeg;
                }
            }
            _desiredLocalRotation = _baseLocalRotation * Quaternion.Euler(0f, 0f, -rot);
            t.localRotation = _desiredLocalRotation;

            // 2) アスペクトの決定 (= テクスチャ自体の見た目の W/H 比、回転前)。
            //    最優先: Texture2D の実寸（streamer 側 /info が square 固定でも、JPEG 寸法が
            //    実態を正しく表しているケースに追従できる）。
            //    fallback: /info の widthPx/heightPx + isPortrait 切替。
            float aspect = TryGetTextureAspect();
            if (aspect <= 0f)
            {
                bool isSquare = meta.widthPx == meta.heightPx && meta.widthPx > 0;
                if (isSquare)
                {
                    if (autoAspectFromIsPortrait)
                    {
                        aspect = meta.isPortrait ? portraitAspectWPerH : landscapeAspectWPerH;
                    }
                    else if (fallbackSquareAspect > 0f)
                    {
                        aspect = fallbackSquareAspect;
                    }
                    else
                    {
                        aspect = 1f;
                    }
                }
                else
                {
                    aspect = meta.EffectiveAspect();
                }
            }

            // 3) サイズ計算。
            //    - 長辺を _baseLocalScale.y に固定 → 縦/横でスクリーンの「サイズ感」が統一される
            //    - rot が 90 / 270 のとき localScale が World 軸と swap するので、W/H を入れ替えて整合
            //    （これをやらないと縦持ちなのに World 上で横長になる現象が出る）
            if (aspect > 0f && _baseLocalScale.y > 0f)
            {
                float baseSize = _baseLocalScale.y;
                float localW, localH;
                if (aspect >= 1f) { localW = baseSize; localH = baseSize / aspect; }
                else              { localH = baseSize; localW = baseSize * aspect; }

                int rotMod = ((rot % 360) + 360) % 360;
                bool swapped = (rotMod == 90 || rotMod == 270);
                if (swapped) { (localW, localH) = (localH, localW); }

                t.localScale = new Vector3(localW, localH, _baseLocalScale.z);
            }
        }

        private void Update()
        {
            if (UseRegistry)
            {
                var active = registry!.GetActive();
                if (active == null || _renderer == null) return;
                var tex = active.Texture;
                if (!ReferenceEquals(tex, _lastAssigned))
                {
                    _renderer.material.mainTexture = tex;
                    _lastAssigned = tex;
                    _lastTexW = 0; _lastTexH = 0;
                    SubscribeOrient(active);
                    // 既に Metadata 取得済みなら即適用（subscribe より前にイベントが発火していたケース）。
                    if (active.Metadata != null) ApplyOrient(active.Metadata);
                }
                // 初回 LoadImage で 2x2 placeholder → 実寸に切り替わるタイミング、
                // および streamer 解像度変更時に自動でアスペクト再適用。
                ReapplyOrientIfTextureSizeChanged(active);
                return;
            }

            if (_receiver == null || _tex == null) return;
            // while-drain: バーストで溜まったフレームは捨てて最新のみ表示（加速⇄スロー対策）。
            byte[]? lastBuf = null;
            int lastLen = 0;
            while (_receiver.TryConsumeFrame(ref _scratch, out int len) && len > 0 && _scratch != null)
            {
                lastBuf = _scratch;
                lastLen = len;
            }
            if (lastBuf != null && lastLen > 0)
            {
                _tex.LoadImage(lastBuf, markNonReadable: false);
            }
        }
    }
}
