// 固定枠スクリーン用の合成シェーダ。web-compositor (tools/web-compositor) のモデルを移植:
//   スクリーン枠 (Quad) は不変、ソースは contain-fit で letterbox、
//   ライブ映像 × 事前撮影オーバーレイをマスクで合成し、ポスト FX はスクリーン内容にのみかける。
// Transform を映像に合わせて変形させる旧 MjpegScreen.ApplyOrient 方式を置き換える。
Shader "FixedCamVr/ScreenComposite"
{
    Properties
    {
        _LiveTex("Live (MJPEG)", 2D) = "black" {}
        _OverlayTex("Overlay (Pre-recorded)", 2D) = "black" {}
        _MaskTex("Overlay Mask (R, screen space)", 2D) = "black" {}
        _CgTex("CG Layer (RGBA, screen space)", 2D) = "black" {}
        _LiveScale("Live Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _OverlayScale("Overlay Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _CgScale("CG Contain Scale (xy)", Vector) = (1, 1, 0, 0)
        _UvRotSteps("Live UV Rotation (90deg steps, 0-3)", Float) = 0
        _OverlayStrength("Overlay Strength", Range(0, 1)) = 0
        _CgStrength("CG Layer Strength", Range(0, 1)) = 0
        [Header(Post FX inside screen)]
        _Exposure("Exposure (EV)", Range(-2, 2)) = 0
        _Contrast("Contrast", Range(0.5, 2)) = 1
        _Saturation("Saturation", Range(0, 2)) = 1
        _Temperature("Temperature", Range(-1, 1)) = 0
        _Vignette("Vignette", Range(0, 1)) = 0
        _Grain("Grain", Range(0, 0.3)) = 0
        _Scanline("Scanline", Range(0, 1)) = 0
        _ScanlineCount("Scanline Count", Float) = 240
        _Lift("Lift (raised black)", Range(0, 0.3)) = 0
        _Tint("Tint (green-magenta)", Range(-1, 1)) = 0
        [Header(Switch and Signal FX (out of FS_POST parity))]
        // ↓ これらは web-compositor の FS_POST 一致規約の対象外（別系統 uniform）。
        //   dip-to-black（切替演出）と信号ロスト（配信断のフェイルソフト＝砂嵐）を post FX の後段にかける。
        _SwitchDim("Switch Dip Dim", Range(0, 1)) = 0
        _SignalLost("Signal Lost (static)", Range(0, 1)) = 0
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }
        LOD 100

        Pass
        {
            Name "ScreenComposite"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            TEXTURE2D(_LiveTex);    SAMPLER(sampler_LiveTex);
            TEXTURE2D(_OverlayTex); SAMPLER(sampler_OverlayTex);
            TEXTURE2D(_MaskTex);    SAMPLER(sampler_MaskTex);
            // CG レイヤ（実カメラの双子の仮想カメラが描く人形）。スクリーン空間・アルファ = 被覆率。
            TEXTURE2D(_CgTex);      SAMPLER(sampler_CgTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _LiveScale;
                float4 _OverlayScale;
                float4 _CgScale;
                float _UvRotSteps;
                float _OverlayStrength;
                float _CgStrength;
                float _Exposure;
                float _Contrast;
                float _Saturation;
                float _Temperature;
                float _Vignette;
                float _Grain;
                float _Scanline;
                float _ScanlineCount;
                float _Lift;
                float _Tint;
                // FS_POST 一致規約の対象外（別系統）。CameraSwitchDirector / SignalLostFx が駆動。
                float _SwitchDim;
                float _SignalLost;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionHCS = TransformObjectToHClip(input.positionOS.xyz);
                o.uv = input.uv;
                return o;
            }

            // 90 度単位の UV 回転（中心まわり）。streamer が正立フレームを送る前提なので
            // 通常 0。緊急時の手動補正用。
            float2 RotateUvSteps(float2 uv, float steps)
            {
                float2 p = uv - 0.5;
                int s = (int)round(steps) & 3;
                if (s == 1) p = float2(-p.y, p.x);
                else if (s == 2) p = -p;
                else if (s == 3) p = float2(p.y, -p.x);
                return p + 0.5;
            }

            // contain-fit: scale<1 の軸を縮めてソース全体を枠内に収める。
            // 枠外 (uv が [0,1] を出る) は inside=0 → 黒 letterbox。
            float2 ContainUv(float2 uv, float2 scale, out float inside)
            {
                float2 s = max(scale, 1e-4);
                float2 p = (uv - 0.5) / s + 0.5;
                float2 ok = step(0.0, p) * step(p, 1.0);
                inside = ok.x * ok.y;
                return saturate(p);
            }

            float Hash21(float2 p)
            {
                p = frac(p * float2(123.34, 456.21));
                p += dot(p, p + 45.32);
                return frac(p.x * p.y);
            }

            half4 frag(Varyings input) : SV_Target
            {
                float2 screenUv = input.uv;

                // 1) ライブ映像: 回転 → contain-fit、枠外は黒
                float liveIn;
                float2 uvL = ContainUv(RotateUvSteps(screenUv, _UvRotSteps), _LiveScale.xy, liveIn);
                half3 live = SAMPLE_TEXTURE2D(_LiveTex, sampler_LiveTex, uvL).rgb * liveIn;

                // 2) オーバーレイ: 独自 contain-fit。マスクはスクリーン空間（合成後の枠基準）
                float ovIn;
                float2 uvO = ContainUv(screenUv, _OverlayScale.xy, ovIn);
                half3 overlay = SAMPLE_TEXTURE2D(_OverlayTex, sampler_OverlayTex, uvO).rgb * ovIn;
                half mask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, screenUv).r;
                half3 col = lerp(live, overlay, saturate(mask * _OverlayStrength));

                // 2.5) CG レイヤ（人形）。**ポスト FX の前**に重ねるのが要点 —
                //      映像と同じ露出・彩度・ヴィネット・走査線・グレインを浴びて初めて
                //      「映像の中に居る」ように見える（後段に足すと必ず浮く）。
                //
                //      **ライブ映像と同じ contain-fit 枠に収める**（_CgScale = MjpegScreen.ContainScale）。
                //      旧実装は生の screenUv で枠いっぱいに描いており、4:3 の映像が 16:9 の枠へ
                //      レターボックスされている分（_LiveScale.x=0.75）だけ人形が水平 1.33 倍外側へずれていた
                //      ＝ 姿勢を完璧に測っても絶対に合わない（2026-07-27 監査 CRITICAL 1）。
                //      letterbox 帯に人形が出ないのは正しい挙動（映像の外に人形は居ない）。
                if (_CgStrength > 0.001)
                {
                    float cgIn;
                    float2 uvC = ContainUv(screenUv, _CgScale.xy, cgIn);
                    half4 cg = SAMPLE_TEXTURE2D(_CgTex, sampler_CgTex, uvC);
                    // premultiplied over（Porter-Duff 1984）。straight alpha の lerp から変えたのは、
                    // **影が「乗算」だから** — 影を rgb=0 / a=濃さ の断片として同じ RT に描けば、
                    // この式が自動的に背景を (1-a) 倍する。不透明な人形（a=1）に対しては lerp と同値。
                    float s = saturate(_CgStrength) * cgIn;
                    col = col * (1.0 - saturate(cg.a) * s) + cg.rgb * s;
                }

                // 3) ポスト FX（web-compositor の FS_POST と数式・順序を一致させる）
                col *= exp2(_Exposure);
                col.r *= 1.0 + 0.25 * _Temperature;
                col.b *= 1.0 - 0.25 * _Temperature;
                // 色かぶり（緑↔マゼンタ）: 色温度と直交する軸。安物 CMOS + 蛍光灯の緑寄りを作る。
                col.g *= 1.0 + 0.25 * _Tint;
                col.r *= 1.0 - 0.12 * _Tint;
                col.b *= 1.0 - 0.12 * _Tint;
                col = (col - 0.5) * _Contrast + 0.5;
                // 黒浮き: **コントラストの後**に黒の床を上げる（前だと潰されて意味が無い）。
                col = col * (1.0 - _Lift) + _Lift;
                half luma = dot(col, half3(0.299, 0.587, 0.114));
                col = lerp(luma.xxx, col, _Saturation);

                float2 dir = screenUv - 0.5;
                col *= saturate(1.0 - _Vignette * dot(dir, dir) * 2.2);

                if (_Scanline > 0.001)
                {
                    float s = 0.5 + 0.5 * sin(screenUv.y * _ScanlineCount * 3.14159265);
                    col *= 1.0 - _Scanline * (1.0 - s) * 0.6;
                }
                if (_Grain > 0.001)
                {
                    float n = Hash21(screenUv * 480.0 + frac(_Time.y));
                    col += (n - 0.5) * _Grain;
                }

                // --- 切替 dip-to-black + 信号ロスト砂嵐（FS_POST 一致規約の対象外・別系統）---
                // 信号ロスト: 手続き砂嵐へクロスフェード + 減光。強=1.0（配信断）/ 弱（トラッキングロスト）は低い値。
                if (_SignalLost > 0.001)
                {
                    float st = Hash21(screenUv * 320.0 + floor(_Time.y * 60.0)); // ~60Hz でざわつく砂嵐
                    half3 stat = half3(st, st, st);
                    float sl = saturate(_SignalLost);
                    col = lerp(col, stat, sl);
                    col *= 1.0 - 0.30 * sl; // 減光
                }
                // dip-to-black: 切替の一瞬だけ黒へ（CCTV の瞬断）。
                col *= 1.0 - saturate(_SwitchDim);

                return half4(saturate(col), 1);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
