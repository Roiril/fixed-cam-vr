#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

namespace FixedCamVr.Streaming.EditorTools.VirtualWalk
{
    /// <summary>
    /// デジタルツインを撮るカメラ 1 台ぶんの道具。部屋の光の中で描き（<see cref="VirtualRoom.BeginShot"/>）、
    /// にじみ・色の分け・周辺減光・粒子で仕上げてファイルへ書く。HDR に頼らない（URP 資産の HDR は触らない）。
    /// </summary>
    internal sealed class TwinLens : IDisposable
    {
        public struct Grade
        {
            public float Threshold, Bloom, Exposure, Contrast, Saturation, Lift, Vignette, Grain, Aberration, Letterbox, WarmDesat;
            public Color ShadowTint, HighTint, BloomTint;

            /// <summary>第三者の「立体」と静止画の仕上げ（参考: 裸電球 1 つの青緑の廃室）。</summary>
            public static Grade Cinematic => new()
            {
                Threshold = 0.86f, Bloom = 0.55f, Exposure = 1.32f, Contrast = 0.34f, Saturation = 1.15f, Lift = 0.012f,
                Vignette = 0.9f, Grain = 0.008f, Aberration = 0f, Letterbox = 0f, WarmDesat = 0.25f,
                ShadowTint = new Color(0.5f, 0.98f, 1.0f, 0.7f), HighTint = new Color(0.97f, 1.0f, 1.02f, 0.4f),
                BloomTint = new Color(0.92f, 0.98f, 1f),
            };

            /// <summary>真上の記録（読むための画。減光と粒は弱く）。</summary>
            public static Grade Plan => new()
            {
                Threshold = 0.7f, Bloom = 0.35f, Exposure = 1.25f, Contrast = 0.18f, Saturation = 0.9f, Lift = 0.02f,
                Vignette = 0.35f, Grain = 0.01f, Aberration = 0f, Letterbox = 0f, WarmDesat = 0.3f,
                ShadowTint = new Color(0.62f, 0.96f, 1.0f, 0.45f), HighTint = new Color(1.03f, 0.99f, 0.93f, 0.4f),
                BloomTint = new Color(1f, 0.9f, 0.76f),
            };
        }

        private readonly Camera _cam;
        private readonly RenderTexture _msaa, _full, _bright, _out;
        private RenderTexture? _depth, _dof;
        private readonly List<Object> _trash;
        private readonly Texture2D _read;
        private readonly Material _grade;
        public Grade Look;
        /// <summary>ピントの距離（m）。0 なら被写界深度なし。</summary>
        public float DofFocus;
        /// <summary>ピントから外れたときのぼけの最大半径（画素）。</summary>
        public float DofMaxPx = 4f;
        /// <summary>ぼけの付き方（絞りの開き）。|z - focus| / z にこれを掛けて 0..1 に切る。</summary>
        public float DofAperture = 2.2f;

        public Camera Camera => _cam;
        public int Width => _out.width;
        public int Height => _out.height;

        public TwinLens(string name, int width, int height, Grade look, List<Object> trash)
        {
            Look = look;
            _trash = trash;
            var go = new GameObject(name);
            trash.Add(go);
            _cam = go.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;
            _cam.allowHDR = false;
            _cam.allowMSAA = true;
            _cam.nearClipPlane = 0.03f;
            _cam.farClipPlane = 30f;
            _cam.cullingMask = ~(1 << 30);
            UniversalAdditionalCameraData data = _cam.GetUniversalAdditionalCameraData();
            data.requiresDepthTexture = true;     // 光の筋が床・壁・人で切れる
            data.renderPostProcessing = false;
            data.renderShadows = true;

            _msaa = Rt(width, height, 24, 8, false, trash);
            _full = Rt(width, height, 0, 1, true, trash);
            _bright = Rt(width / 2, height / 2, 0, 1, true, trash);
            _out = Rt(width, height, 0, 1, false, trash);
            _read = new Texture2D(width, height, TextureFormat.RGB24, false);
            trash.Add(_read);
            Shader grade = Shader.Find("Hidden/FixedCamVr/Twin/Grade")
                           ?? throw new InvalidOperationException("[TwinLens] Hidden/FixedCamVr/Twin/Grade が無い");
            _grade = new Material(grade) { name = "Twin grade" };
            trash.Add(_grade);
        }

        private static RenderTexture Rt(int w, int h, int depth, int aa, bool mips, List<Object> trash)
        {
            var rt = new RenderTexture(w, h, depth, RenderTextureFormat.ARGB32)
            {
                antiAliasing = aa, useMipMap = mips, autoGenerateMips = mips,
                filterMode = mips ? FilterMode.Trilinear : FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp,
            };
            rt.Create();
            trash.Add(rt);
            return rt;
        }

        public void Aim(Vector3 position, Vector3 target, float verticalFovDeg)
        {
            _cam.orthographic = false;
            _cam.fieldOfView = verticalFovDeg;
            _cam.transform.SetPositionAndRotation(position, Quaternion.LookRotation(target - position, Vector3.up));
        }

        /// <summary>仕上げ済みの画（<see cref="Develop"/> の結果）。固定カメラのスマホの画面に流す。</summary>
        public RenderTexture Output => _out;

        /// <summary>描いて仕上げて書く。拡張子が .png なら PNG、それ以外は JPEG（品質 92）。</summary>
        public void Shoot(VirtualRoom room, float time, int frame, string path)
        {
            RenderTexture? old = RenderTexture.active;
            try
            {
                Develop(room, time, frame);
                RenderTexture.active = _out;
                _read.ReadPixels(new Rect(0, 0, _out.width, _out.height), 0, 0);
                _read.Apply();
                bool png = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase);
                File.WriteAllBytes(path, png ? _read.EncodeToPNG() : _read.EncodeToJPG(92));
            }
            finally
            {
                RenderTexture.active = old;
            }
        }

        /// <summary>部屋の光の中で描き、にじみ・色・周辺減光・粒子で仕上げて <see cref="Output"/> に置く。</summary>
        public void Develop(VirtualRoom room, float time, int frame)
        {
            RenderTexture? old = RenderTexture.active;
            try
            {
                // 札の見え方の判定は描画先の縦横比で（描画先を付ける前は画面の既定比で計算される）
                _cam.aspect = _out.width / (float)_out.height;
                room.PrepareFor(_cam);
                using (room.BeginShot(time))
                {
                    _cam.targetTexture = _msaa;
                    _cam.Render();
                    _cam.targetTexture = null;
                }
                // 被写界深度の深度: URP の深度テクスチャは描画の外から引けないので、MSAA なし・深度テクスチャなしで
                // もう 1 度描き、描画先の深度バッファをそのまま読む（中間テクスチャを挟まない条件で描く）
                RenderTexture? depth = null;
                if (DofFocus > 0f)
                {
                    _depth ??= Rt(_out.width, _out.height, 24, 1, false, _trash);
                    UniversalAdditionalCameraData data = _cam.GetUniversalAdditionalCameraData();
                    using (room.BeginShot(time))
                    {
                        data.requiresDepthTexture = false;
                        _cam.allowMSAA = false;
                        _cam.targetTexture = _depth;
                        _cam.Render();
                        _cam.targetTexture = null;
                        _cam.allowMSAA = true;
                        data.requiresDepthTexture = true;
                    }
                    depth = _depth;
                }
                Graphics.Blit(_msaa, _full);
                RenderTexture src = _full;
                _grade.SetFloat("_DofFocus", depth != null ? DofFocus : 0f);
                if (depth != null)
                {
                    float n = _cam.nearClipPlane, f = _cam.farClipPlane;
                    float x = SystemInfo.usesReversedZBuffer ? -1f + f / n : 1f - f / n;
                    float y = SystemInfo.usesReversedZBuffer ? 1f : f / n;
                    _grade.SetTexture("_DepthTex", depth, UnityEngine.Rendering.RenderTextureSubElement.Depth);
                    _grade.SetVector("_DofZ", new Vector4(x / f, y / f, 0f, 0f));
                    _grade.SetFloat("_DofMaxPx", DofMaxPx);
                    _grade.SetFloat("_DofAperture", DofAperture);
                    _grade.SetFloat("_Mode", 2f);
                    _dof ??= Rt(_out.width, _out.height, 0, 1, true, _trash);
                    Graphics.Blit(_full, _dof, _grade);    // にじみの元もぼけた像から抜く（電球だけくっきり残らない）
                    src = _dof;
                }
                _grade.SetTexture("_BloomTex", Texture2D.blackTexture);   // 抜く先を読まない
                _grade.SetFloat("_Mode", 0f);
                _grade.SetFloat("_Threshold", Look.Threshold);
                Graphics.Blit(src, _bright, _grade);
                _grade.SetFloat("_Mode", 1f);
                _grade.SetTexture("_BloomTex", _bright);
                _grade.SetFloat("_BloomStrength", Look.Bloom);
                _grade.SetFloat("_Exposure", Look.Exposure);
                _grade.SetFloat("_Contrast", Look.Contrast);
                _grade.SetFloat("_Saturation", Look.Saturation);
                _grade.SetFloat("_Lift", Look.Lift);
                _grade.SetFloat("_Vignette", Look.Vignette);
                _grade.SetFloat("_Grain", Look.Grain);
                _grade.SetFloat("_Aberration", Look.Aberration);
                _grade.SetFloat("_Letterbox", Look.Letterbox);
                _grade.SetFloat("_WarmDesat", Look.WarmDesat);
                _grade.SetFloat("_Frame", frame);

                _grade.SetColor("_ShadowTint", Look.ShadowTint);
                _grade.SetColor("_HighTint", Look.HighTint);
                _grade.SetColor("_BloomTint", Look.BloomTint);
                Graphics.Blit(src, _out, _grade);
            }
            finally
            {
                _cam.targetTexture = null;
                RenderTexture.active = old;
            }
        }

        public void Dispose() => _cam.targetTexture = null;
    }
}
