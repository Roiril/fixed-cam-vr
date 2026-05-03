#nullable enable
using UnityEngine;

namespace FixedCamVr.Fx.Source
{
    /// <summary>
    /// アセット（動画ファイル）を置かずに動的に映像入力を作るテストパターン生成器。
    /// MJPEG 映像の代替として動くチェッカーボード + ノイズを RenderTexture に書き込む。
    /// FxSandbox での 4 系統比較に共通入力として使う。
    ///
    /// シェーダで GPU 生成するため毎フレ GC アロケなし。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FxTestPatternSource : MonoBehaviour
    {
        [Tooltip("生成 RenderTexture の幅 (px)")]
        [SerializeField] private int width = 1280;
        [Tooltip("生成 RenderTexture の高さ (px)")]
        [SerializeField] private int height = 720;
        [Tooltip("テストパターンを描画するシェーダ (FxTestPattern.shader)")]
        [SerializeField] private Shader? patternShader;
        [Tooltip("生成 RT を ExplicitTexture として流し込む先のブリッジ")]
        [SerializeField] private FxSourceBinder? binderToFeed;

        private RenderTexture? _rt;
        private Material? _mat;

        public RenderTexture? Output => _rt;

        private void OnEnable()
        {
            _rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
            {
                name = "Fx_TestPattern_RT",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();

            if (patternShader != null)
            {
                _mat = new Material(patternShader) { hideFlags = HideFlags.HideAndDontSave };
            }

            if (binderToFeed != null)
            {
                binderToFeed.SetExplicitTexture(_rt);
            }
        }

        private void OnDisable()
        {
            if (_rt != null)
            {
                _rt.Release();
                Destroy(_rt);
                _rt = null;
            }
            if (_mat != null)
            {
                Destroy(_mat);
                _mat = null;
            }
        }

        private void Update()
        {
            if (_rt == null) return;
            if (_mat == null)
            {
                // フォールバック: シェーダ未割当時は単色 + 時刻
                var prev = RenderTexture.active;
                RenderTexture.active = _rt;
                GL.Clear(true, true, new Color(Mathf.PingPong(Time.time * 0.3f, 1f), 0.2f, 0.4f));
                RenderTexture.active = prev;
                return;
            }
            _mat.SetFloat(ShaderIds.Time, Time.time);
            Graphics.Blit(null, _rt, _mat);
        }

        private static class ShaderIds
        {
            public static readonly int Time = Shader.PropertyToID("_FxTime");
        }
    }
}
