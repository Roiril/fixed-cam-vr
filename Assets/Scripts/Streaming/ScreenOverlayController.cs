#nullable enable
using System;
using UnityEngine;
using UnityEngine.Video;

namespace FixedCamVr.Streaming
{
    /// <summary>
    /// ScreenComposite シェーダのオーバーレイ側（事前撮影クリップ × マスク）を駆動する。
    /// MjpegScreen と同じ Renderer のマテリアルインスタンスへプロパティを書き込む。
    ///
    /// 発火方法:
    ///   - キーボード（Editor + Link 運用でのオペレータ操作）: bindings の key
    ///   - コード: PlayCue(int) / PlayCue(OverlayCue) / StopOverlay()
    ///     PlayerZoneTracker のゾーン遷移等から呼べるよう public にしてある。
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
        private OverlayCue? _current;

        // フェード状態。target に向かって _strength を進める。
        private float _strength;
        private float _target;
        private float _fadeSpeed = 4f;
        private bool _stopWhenFadedOut;

        /// <summary>現在再生中の cue（フェードアウト中も含む）。null なら停止。</summary>
        public OverlayCue? Current => _current;

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
            if (cue != null) PlayCue(cue);
        }

        /// <summary>cue を発火。再生中の cue があれば即座に置き換える。</summary>
        public void PlayCue(OverlayCue cue)
        {
            if (_material == null || _player == null) return;
            _current = cue;
            _stopWhenFadedOut = false;

            // マスク。null は全面差し替え（白）
            _material.SetTexture(MaskTexId, cue.mask != null ? cue.mask : Texture2D.whiteTexture);

            if (cue.clip != null)
            {
                _player.Stop();
                _player.clip = cue.clip;
                _player.isLooping = cue.loop;
                _player.Prepare(); // 完了後 OnPrepared で RT 接続 + 再生 + フェードイン
            }
            else if (cue.stillImage != null)
            {
                if (_player.isPlaying) _player.Stop();
                SetOverlayTexture(cue.stillImage, (float)cue.stillImage.width / cue.stillImage.height);
                BeginFadeIn(cue);
            }
            else
            {
                Debug.LogWarning($"[ScreenOverlay] cue '{cue.name}' has no clip / stillImage.");
                _current = null;
            }
        }

        /// <summary>現在のオーバーレイをフェードアウトして停止。</summary>
        public void StopOverlay()
        {
            if (_current == null) return;
            float fade = Mathf.Max(_current.fadeOutSeconds, 1e-3f);
            _target = 0f;
            _fadeSpeed = _strength / fade;
            _stopWhenFadedOut = true;
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
            vp.Play();
            BeginFadeIn(cue);
        }

        private void BeginFadeIn(OverlayCue cue)
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
