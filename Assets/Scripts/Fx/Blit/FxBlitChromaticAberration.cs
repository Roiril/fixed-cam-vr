#nullable enable
using FixedCamVr.Fx.Source;
using UnityEngine;

namespace FixedCamVr.Fx.Blit
{
    /// <summary>
    /// FxSourceBinder の入力 Texture に色収差シェーダを適用し、
    /// 結果 RT を Renderer.material.mainTexture に流し込む MonoBehaviour 駆動 Blit。
    ///
    /// URP のパイプライン外（OnPreRender 相当のタイミング）でフレーム単位に Blit する設計。
    /// Phase 1 の Renderer Feature 方式との比較対象として用意している。
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class FxBlitChromaticAberration : MonoBehaviour
    {
        [Tooltip("色収差を計算する HLSL シェーダ (FxChromaticAberration.shader)")]
        [SerializeField] private Shader? blitShader;
        [Tooltip("色収差を掛ける入力 Texture を提供するブリッジ")]
        [SerializeField] private FxSourceBinder? binder;
        [Tooltip("加工後 RT を mainTexture として渡す描画先 Renderer")]
        [SerializeField] private Renderer? targetRenderer;
        [Tooltip("色収差の最大ずらし量 (UV 単位)")]
        [Range(0, 0.05f)] [SerializeField] private float strength = 0.012f;
        [Tooltip("中心からの距離に対するずらし強度の指数。値が大きいほど周辺だけ強く出る")]
        [Range(0, 4f)] [SerializeField] private float falloff = 1.5f;

        private Material? _mat;
        private RenderTexture? _rt;
        private Texture? _lastSource;
        // targetRenderer.material は呼ぶたびに sharedMaterial の複製を返す。
        // 1 度だけインスタンス化して使い回し、OnDisable で Destroy する。
        private Material? _targetMatInstance;

        private static readonly int IdStrength = Shader.PropertyToID("_Strength");
        private static readonly int IdFalloff = Shader.PropertyToID("_Falloff");

        private void OnEnable()
        {
            if (blitShader == null)
            {
                Debug.LogWarning($"[{nameof(FxBlitChromaticAberration)}] blitShader 未割り当て。Update は no-op になります。", this);
                return;
            }
            _mat = new Material(blitShader) { hideFlags = HideFlags.HideAndDontSave };
        }

        private void OnDisable()
        {
            if (_mat != null) { Destroy(_mat); _mat = null; }
            ReleaseRT();
            if (_targetMatInstance != null)
            {
                _targetMatInstance.mainTexture = null;
                Destroy(_targetMatInstance);
                _targetMatInstance = null;
            }
        }

        private void Update()
        {
            if (_mat == null || binder == null || targetRenderer == null) return;
            var src = binder.Current;
            if (src == null) return;

            EnsureRT(src.width, src.height);
            if (_rt == null) return;

            _mat.SetFloat(IdStrength, strength);
            _mat.SetFloat(IdFalloff, falloff);
            Graphics.Blit(src, _rt, _mat);

            if (!ReferenceEquals(_rt, _lastSource))
            {
                // targetRenderer.material は呼び出し時に複製インスタンスを生成するため
                // 初回のみ取得して保持。以降は同じインスタンスへ書き込む。
                if (_targetMatInstance == null) _targetMatInstance = targetRenderer.material;
                _targetMatInstance.mainTexture = _rt;
                _lastSource = _rt;
            }
        }

        private void EnsureRT(int w, int h)
        {
            if (_rt != null && _rt.width == w && _rt.height == h) return;
            ReleaseRT();
            _rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32)
            {
                name = "Fx_ChromaticAb_RT",
                filterMode = FilterMode.Bilinear,
            };
            _rt.Create();
            _lastSource = null;
        }

        private void ReleaseRT()
        {
            if (_rt != null) { _rt.Release(); Destroy(_rt); _rt = null; }
        }
    }
}
