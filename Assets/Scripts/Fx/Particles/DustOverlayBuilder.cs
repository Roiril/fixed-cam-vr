#nullable enable
using UnityEngine;

namespace FixedCamVr.Fx.Particles
{
    /// <summary>
    /// 映像 Quad の前面に配置する「埃 / 霧」風 ParticleSystem を
    /// プログラマブルに構築する MonoBehaviour。
    ///
    /// VFX Graph はパッケージ追加が必要なため、Phase 3 では標準同梱の ParticleSystem を採用。
    /// バイオハザード固定カメラの「画面に空気感を載せる」表現を狙う。
    /// </summary>
    [RequireComponent(typeof(ParticleSystem))]
    [DisallowMultipleComponent]
    public sealed class DustOverlayBuilder : MonoBehaviour
    {
        [Tooltip("単位時間あたりのパーティクル発生数 (個/秒)")]
        [SerializeField] private float emitRate = 60f;
        [Tooltip("各パーティクルの生存時間 (秒)")]
        [SerializeField] private float lifetime = 4f;
        [Tooltip("各パーティクルの初期サイズ (ワールド単位)")]
        [SerializeField] private float startSize = 0.04f;
        [Tooltip("パーティクルの色味と最大不透明度")]
        [SerializeField] private Color tint = new Color(0.9f, 0.85f, 0.7f, 0.25f);

        // ApplyConfig 内で生成したマテリアルの参照を保持し、OnDestroy で確実に破棄する。
        private Material? _generatedMat;

        private void Reset()
        {
            // RequireComponent で ParticleSystem が無い場合に備えて ApplyConfig 呼ぶ
            ApplyConfig();
        }

        private void OnEnable()
        {
            ApplyConfig();
        }

        private void OnDestroy()
        {
            if (_generatedMat != null)
            {
                if (Application.isPlaying) Destroy(_generatedMat);
                else DestroyImmediate(_generatedMat);
                _generatedMat = null;
            }
        }

        private void ApplyConfig()
        {
            var ps = GetComponent<ParticleSystem>();
            if (ps == null) return;

            var main = ps.main;
            main.duration = 5f;
            main.loop = true;
            main.startLifetime = lifetime;
            main.startSpeed = 0.05f;
            main.startSize = startSize;
            main.startColor = tint;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            main.maxParticles = 2048;

            var emission = ps.emission;
            emission.rateOverTime = emitRate;

            var shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Box;
            shape.scale = new Vector3(2.0f, 1.2f, 0.05f);

            var velocity = ps.velocityOverLifetime;
            velocity.enabled = true;
            velocity.x = new ParticleSystem.MinMaxCurve(-0.02f, 0.02f);
            velocity.y = new ParticleSystem.MinMaxCurve(-0.005f, 0.01f);

            var color = ps.colorOverLifetime;
            color.enabled = true;
            var grad = new Gradient();
            grad.SetKeys(
                new[] { new GradientColorKey(tint, 0f), new GradientColorKey(tint, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(tint.a, 0.3f), new GradientAlphaKey(0f, 1f) });
            color.color = new ParticleSystem.MinMaxGradient(grad);

            var renderer = GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = ParticleSystemRenderMode.Billboard;

                // 既に生成済みマテリアルがある場合は使い回す。
                // 以前は OnEnable のたびに new Material を作って前回分が GC 管理から外れていた。
                if (_generatedMat == null)
                {
                    var sh = Shader.Find("Universal Render Pipeline/Particles/Unlit");
                    if (sh == null)
                    {
                        Debug.LogWarning("[DustOverlayBuilder] URP Particles/Unlit shader not found. " +
                            "URP がプロジェクトに導入されているか確認してください。");
                    }
                    else
                    {
                        _generatedMat = new Material(sh)
                        {
                            name = "FxDustParticleMat",
                            hideFlags = HideFlags.DontSave,
                        };
                    }
                }
                if (_generatedMat != null) renderer.sharedMaterial = _generatedMat;
            }
        }
    }
}
