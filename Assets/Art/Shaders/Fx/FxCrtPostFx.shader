// URP FullScreenPassRendererFeature 用フルスクリーンポストエフェクト。
// CRT スキャンライン + フィルムグレイン + ビネット の合成。
// バイオハザード固定カメラ風の「画面に質感を載せる」表現を狙う。
Shader "FixedCamVr/Fx/CrtPostFx"
{
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Common.hlsl"
    // Blitter 用の Vert/Varyings/_BlitTexture/sampler_LinearClamp は SRP Blit.hlsl で定義
    #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"

    float _ScanlineIntensity;
    float _ScanlineCount;
    float _GrainIntensity;
    float _VignetteIntensity;
    float _Time01;

    float Hash21(float2 p)
    {
        p = frac(p * float2(123.34, 456.21));
        p += dot(p, p + 45.32);
        return frac(p.x * p.y);
    }

    half4 FragCRT (Varyings input) : SV_Target
    {
        UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
        float2 uv = input.texcoord.xy;
        half4 col = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);

        // Scanline
        float scan = sin(uv.y * _ScanlineCount * 3.14159265 + _Time01 * 6.0);
        col.rgb *= 1.0 - _ScanlineIntensity * (0.5 - 0.5 * scan);

        // Grain
        float n = Hash21(uv * 1024.0 + _Time01 * 60.0);
        col.rgb += (n - 0.5) * _GrainIntensity;

        // Vignette
        float d = distance(uv, 0.5);
        float vig = smoothstep(0.85, 0.3, d);
        col.rgb *= lerp(1.0, vig, _VignetteIntensity);

        return col;
    }
    ENDHLSL

    SubShader
    {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        ZWrite Off Cull Off ZTest Always

        Pass
        {
            Name "FxCrtPostFx"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragCRT
            ENDHLSL
        }
    }
    FallBack Off
}
