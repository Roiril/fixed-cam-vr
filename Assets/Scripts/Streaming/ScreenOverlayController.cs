#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.Video;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ScreenComposite シェーダのオーバーレイ側（事前撮影クリップ × マスク）を駆動する。
    /// MjpegScreen と同じ Renderer のマテリアルインスタンスへプロパティを書き込む。
    ///
    /// 発火方法（すべて PlayCue(OverlayCueData) に集約、最後の命令が勝つ）:
    ///   - キーボード（Editor + Link 運用でのオペレータ操作）: bindings の key
    ///   - Web オペレータ卓: ShowControlClient → PlayCue(OverlayCueData)
    ///   - コード（ゾーントリガ等）: PlayCue / StopOverlay
    /// ソースは ローカル（VideoClip / Texture2D）と URL（mp4 直再生 / png ダウンロード）の両対応。
    /// </summary>
    [RequireComponent(typeof(Renderer))]
    public sealed class ScreenOverlayController : MonoBehaviour
    {
        private static readonly int OverlayTexId = Shader.PropertyToID("_OverlayTex");
        private static readonly int OverlayScaleId = Shader.PropertyToID("_OverlayScale");
        private static readonly int MaskTexId = Shader.PropertyToID("_MaskTex");
        private static readonly int OverlayStrengthId = Shader.PropertyToID("_OverlayStrength");

        [Serializable]
        public struct CueBinding
        {
            public OverlayCue? cue;
            [Tooltip("Editor / Link 運用でこの cue を発火するキー。None なら手動コードのみ。")]
            public KeyCode key;
        }

        [SerializeField] private CueBinding[] bindings = Array.Empty<CueBinding>();

        [Tooltip("全オーバーレイ停止キー。")]
        [SerializeField] private KeyCode stopKey = KeyCode.Alpha0;

        [Tooltip("VideoClip 用 RenderTexture の縦解像度。クリップのアスペクトで横を決める。")]
        [SerializeField] private int videoHeightPx = 720;

        private Material? _material;
        private MjpegScreen? _screen;
        private VideoPlayer? _player;
        private RenderTexture? _videoRt;
        private OverlayCueData? _current;

        // URL ロード物のキャッシュ（マスク / 静止画）。現場で同じ cue を繰り返し叩く前提。
        private readonly Dictionary<string, Texture2D> _urlTextureCache = new();
        // PlayCue が非同期ロードを挟む間に次の PlayCue が来たら古い方を破棄するための世代カウンタ。
        private int _playGeneration;

        /// <summary>現在のオーバーレイ（フェードアウト中も含む）。null なら停止。</summary>
        public OverlayCueData? Current => _current;

        // フェード状態。target に向かって _strength を進める。
        private float _strength;
        private float _target;
        private float _fadeSpeed = 4f;
        private bool _stopWhenFadedOut;

        private void Awake()
        {
            // MjpegScreen が Awake で .material をインスタンス化するので同じものを共有する。
            _screen = GetComponent<MjpegScreen>();
            _material = GetComponent<Renderer>().material;

            _player = gameObject.AddComponent<VideoPlayer>();
            _player.playOnAwake = false;
            _player.renderMode = VideoRenderMode.RenderTexture;
            _player.audioOutputMode = VideoAudioOutputMode.None;
            _player.skipOnDrop = true;
            _player.prepareCompleted += OnPrepared;
            _player.loopPointReached += OnVideoEnd; // 自然終端 → 自動フェードアウト（ループしない cue のみ）

            ApplyStrength(0f);
        }

        private void OnDestroy()
        {
            if (_videoRt != null)
            {
                _videoRt.Release();
                if (Application.isPlaying) Destroy(_videoRt);
                else DestroyImmediate(_videoRt);
                _videoRt = null;
            }
            foreach (var tex in _urlTextureCache.Values)
            {
                if (tex != null)
                {
                    if (Application.isPlaying) Destroy(tex);
                    else DestroyImmediate(tex);
                }
            }
            _urlTextureCache.Clear();
        }

        private void Update()
        {
            for (int i = 0; i < bindings.Length; i++)
            {
                if (bindings[i].key != KeyCode.None && Input.GetKeyDown(bindings[i].key))
                {
                    PlayCue(i);
                }
            }
            if (Input.GetKeyDown(stopKey)) StopOverlay();

            // 動画の再生区間終端で自動停止（ループしない cue・trimEnd>0 のとき）
            if (_current != null && !_current.loop && _current.trimEnd > 0f && !_stopWhenFadedOut
                && _player != null && _player.isPlaying && _player.time >= _current.trimEnd)
            {
                StopOverlay();
            }

            // フェード進行
            if (!Mathf.Approximately(_strength, _target))
            {
                _strength = Mathf.MoveTowards(_strength, _target, _fadeSpeed * Time.deltaTime);
                ApplyStrength(_strength);
            }
            else if (_stopWhenFadedOut && _strength <= 0f)
            {
                _stopWhenFadedOut = false;
                if (_player != null && _player.isPlaying) _player.Stop();
                _current = null;
            }
        }

        /// <summary>bindings の index で cue を発火。</summary>
        public void PlayCue(int index)
        {
            if (index < 0 || index >= bindings.Length) return;
            var cue = bindings[index].cue;
            if (cue != null) PlayCue(OverlayCueData.From(cue));
        }

        /// <summary>ScriptableObject 版 cue を発火（互換 API）。</summary>
        public void PlayCue(OverlayCue cue) => PlayCue(OverlayCueData.From(cue));

        /// <summary>cue を発火。再生中の cue があれば置き換える（最後の命令が勝つ）。</summary>
        public void PlayCue(OverlayCueData data)
        {
            if (_material == null || _player == null) return;
            int gen = ++_playGeneration;
            _ = PlayCueAsync(data, gen, destroyCancellationToken);
        }

        private async Task PlayCueAsync(OverlayCueData data, int gen, CancellationToken ct)
        {
            // 1) マスク（URL ならロード、ローカルならそのまま、無指定なら全面白）
            Texture? mask = data.maskTexture;
            if (mask == null && !string.IsNullOrEmpty(data.maskUrl))
            {
                mask = await LoadTextureAsync(data.maskUrl, ct);
                if (gen != _playGeneration || ct.IsCancellationRequested) return; // 古い発火は破棄
                if (mask == null)
                {
                    Debug.LogWarning($"[ScreenOverlay] mask load failed: {data.maskUrl} (全面差し替えで続行)");
                }
            }

            // 2) ソース
            if (data.SourceIsVideo)
            {
                _current = data;
                _material!.SetTexture(MaskTexId, mask != null ? mask : Texture2D.whiteTexture);
                _player!.Stop();
                if (data.clip != null)
                {
                    _player.source = VideoSource.VideoClip;
                    _player.clip = data.clip;
                }
                else
                {
                    _player.source = VideoSource.Url;
                    _player.url = data.sourceUrl;
                }
                _player.isLooping = data.loop;
                _player.Prepare(); // 完了後 OnPrepared で RT 接続 + 再生 + フェードイン
            }
            else
            {
                Texture? still = data.stillImage;
                if (still == null && !string.IsNullOrEmpty(data.sourceUrl))
                {
                    still = await LoadTextureAsync(data.sourceUrl, ct);
                    if (gen != _playGeneration || ct.IsCancellationRequested) return;
                }
                if (still == null)
                {
                    Debug.LogWarning($"[ScreenOverlay] cue '{data.displayName}' has no source.");
                    return;
                }
                _current = data;
                _material!.SetTexture(MaskTexId, mask != null ? mask : Texture2D.whiteTexture);
                if (_player!.isPlaying) _player.Stop();
                SetOverlayTexture(still, (float)still.width / still.height);
                BeginFadeIn(data);
            }
        }

        /// <summary>現在のオーバーレイをフェードアウトして停止。</summary>
        public void StopOverlay()
        {
            if (_current == null) return;
            _playGeneration++; // ロード途中の発火も破棄
            float fade = Mathf.Max(_current.fadeOutSeconds, 1e-3f);
            _target = 0f;
            _fadeSpeed = Mathf.Max(_strength, 0.01f) / fade;
            _stopWhenFadedOut = true;
        }

        private async Task<Texture2D?> LoadTextureAsync(string url, CancellationToken ct)
        {
            if (_urlTextureCache.TryGetValue(url, out var cached) && cached != null) return cached;
            try
            {
                using var req = UnityWebRequestTexture.GetTexture(url, nonReadable: true);
                req.timeout = 5;
                var op = req.SendWebRequest();
                while (!op.isDone)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Yield();
                }
                if (req.result != UnityWebRequest.Result.Success) return null;
                var tex = DownloadHandlerTexture.GetContent(req);
                _urlTextureCache[url] = tex;
                return tex;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Debug.LogWarning($"[ScreenOverlay] texture load error: {url} ({e.Message})");
                return null;
            }
        }

        private void OnPrepared(VideoPlayer vp)
        {
            var cue = _current;
            if (cue == null || _material == null) return;

            int w = (int)vp.width, h = (int)vp.height;
            if (w <= 0 || h <= 0) { w = 16; h = 9; }
            float aspect = (float)w / h;

            int rtH = Mathf.Min(videoHeightPx, h);
            int rtW = Mathf.RoundToInt(rtH * aspect);
            if (_videoRt == null || _videoRt.width != rtW || _videoRt.height != rtH)
            {
                if (_videoRt != null)
                {
                    _videoRt.Release();
                    Destroy(_videoRt);
                }
                _videoRt = new RenderTexture(rtW, rtH, 0, RenderTextureFormat.ARGB32)
                {
                    name = "ScreenOverlayVideo",
                    useMipMap = false,
                };
                _videoRt.Create();
            }

            vp.targetTexture = _videoRt;
            SetOverlayTexture(_videoRt, aspect);
            if (cue.trimStart > 0f) vp.time = cue.trimStart; // 再生区間の頭へシーク
            vp.Play();
            BeginFadeIn(cue);
        }

        // 動画が自然終端（trimEnd 未指定で最後まで再生）に達した時。ループしない cue を自動で戻す。
        private void OnVideoEnd(VideoPlayer vp)
        {
            if (_current != null && !_current.loop && !_stopWhenFadedOut) StopOverlay();
        }

        private void BeginFadeIn(OverlayCueData cue)
        {
            float fade = Mathf.Max(cue.fadeInSeconds, 1e-3f);
            _target = cue.strength;
            _fadeSpeed = Mathf.Max(_target - _strength, 0.01f) / fade;
        }

        private void SetOverlayTexture(Texture tex, float srcAspect)
        {
            if (_material == null) return;
            float screenAspect = _screen != null ? _screen.ScreenAspect : 16f / 9f;
            Vector2 scale = srcAspect < screenAspect
                ? new Vector2(srcAspect / screenAspect, 1f)
                : new Vector2(1f, screenAspect / srcAspect);
            _material.SetTexture(OverlayTexId, tex);
            _material.SetVector(OverlayScaleId, new Vector4(scale.x, scale.y, 0f, 0f));
        }

        private void ApplyStrength(float v)
        {
            _material?.SetFloat(OverlayStrengthId, v);
        }
    }
}
