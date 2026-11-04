// ツインの撮影の仕上げ（Graphics.Blit 用）。_Mode 0 = にじみの元（明るい所だけ抜く）、1 = 仕上げ、2 = 被写界深度。
// 仕上げ: にじみ（抜いた像の mip を重ねる）→ 色の分け（暗部を青緑・明部を暖色）→ 締まり → 周辺減光 → 粒子。
Shader "Hidden/FixedCamVr/Twin/Grade"
{
    Properties
    {
        _MainTex ("Source", 2D) = "black" {}
        _BloomTex ("Bloom", 2D) = "black" {}
    }
    SubShader
    {
        ZTest Always Cull Off ZWrite Off
        Pass
        {
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment Frag
            #pragma target 4.5
            #include "UnityCG.cginc"
            sampler2D _MainTex; float4 _MainTex_TexelSize;
            sampler2D _BloomTex;
            sampler2D _DepthTex;
            float4 _DofZ;
            float _DofFocus, _DofMaxPx, _DofAperture;
            float _Mode, _Threshold, _BloomStrength, _Vignette, _Grain, _Frame, _Aberration;
            float _Exposure, _Contrast, _Saturation, _Lift, _Letterbox, _WarmDesat;
            float4 _ShadowTint, _HighTint, _BloomTint;

            float Hash(float2 p) { float3 q = frac(float3(p.xyx) * 0.1031); q += dot(q, q.yzx + 33.33); return frac((q.x + q.y) * q.z); }
            float Luma(float3 c) { return dot(c, float3(0.2126, 0.7152, 0.0722)); }
            // ぼけの半径（画素）。深度から目からの距離を戻し、ピントからの外れを薄いレンズの形で
            float Coc(float2 uv)
            {
                float z = 1.0 / (_DofZ.x * tex2Dlod(_DepthTex, float4(uv, 0, 0)).r + _DofZ.y);
                return saturate(abs(z - _DofFocus) / max(z, 0.05) * _DofAperture) * _DofMaxPx;
            }

            float4 Frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv;
                if (_Mode > 1.5)
                {
                    // 黄金角の円盤で集める。各点は自分のぼけの半径が届く分だけ寄与する（ピントの合った前景を背景へ滲ませない）
                    float3 c0 = tex2D(_MainTex, uv).rgb;
                    float rad = Coc(uv);
                    if (rad <= 0.6) return float4(c0, 1);
                    float3 acc = c0; float wsum = 1.0;
                    float lod = log2(max(rad / 2.0, 1.0));
                    [unroll] for (int k = 0; k < 24; k++)
                    {
                        float rr = sqrt((k + 0.5) / 24.0);
                        float a = k * 2.39996;
                        float2 o = float2(cos(a), sin(a)) * rr * rad * _MainTex_TexelSize.xy;
                        float w = saturate(Coc(uv + o) - rr * rad + 1.0);
                        acc += tex2Dlod(_MainTex, float4(uv + o, 0, lod)).rgb * w;
                        wsum += w;
                    }
                    return float4(acc / wsum, 1);
                }
                if (_Mode < 0.5)
                {
                    float3 c = tex2D(_MainTex, uv).rgb;
                    float l = Luma(c);
                    float k = saturate((l - _Threshold) / max(1.0 - _Threshold, 1e-3));
                    return float4(c * k * k, 1);
                }
                float2 d = uv - 0.5;
                float r2 = dot(d, d);
                // 周辺だけわずかに色がずれる（安いレンズの縁）
                float2 ab = d * r2 * _Aberration;
                float3 col;
                col.r = tex2D(_MainTex, uv + ab).r;
                col.g = tex2D(_MainTex, uv).g;
                col.b = tex2D(_MainTex, uv - ab).b;

                float3 bloom = 0;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 1)).rgb * 0.30;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 2)).rgb * 0.30;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 3)).rgb * 0.45;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 4)).rgb * 0.60;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 5)).rgb * 0.80;
                bloom += tex2Dlod(_BloomTex, float4(uv, 0, 6)).rgb * 1.00;
                col += bloom * _BloomStrength * _BloomTint.rgb;

                col *= _Exposure;
                // 暗部を青緑へ、明部を電球の暖色へ（掛けすぎない。主役は面の色）
                float l = Luma(col);
                float sh = 1.0 - smoothstep(0.0, 0.35, l);
                float hi = smoothstep(0.35, 1.0, l);
                col = lerp(col, col * _ShadowTint.rgb * 1.6, sh * _ShadowTint.a);
                col = lerp(col, col * _HighTint.rgb, hi * _HighTint.a);
                col += _Lift * _ShadowTint.rgb * (1.0 - l);
                // 締まり（中間調を中心にした S 字）
                col = saturate(col);
                float3 s = col * col * (3.0 - 2.0 * col);
                col = lerp(col, s, _Contrast);
                l = Luma(col);
                col = lerp(l.xxx, col, _Saturation);
                // 明るい所ほど彩度を落とす（白い光が青緑の塗膜に当たった、淡い灰青緑）
                col = lerp(l.xxx, col, lerp(1.0, 0.55, smoothstep(0.06, 0.4, l)));
                // 最暗部の色を抜いて中立の黒へ（中間の青緑は残す）
                col = lerp(l.xxx, col, smoothstep(0.015, 0.06, l));
                // 青緑の中で橙（肌・木）だけが浮く。赤が緑より勝つ画素だけ彩度を落とす
                float warm = saturate((col.r - col.g) * 6.0) * saturate((col.r - col.b) * 4.0);
                col = lerp(col, l.xxx + (col - l.xxx) * (1.0 - _WarmDesat), warm);
                // 周辺減光（楕円・やわらかく）
                float2 vd = d * float2(1.0, 0.82);
                float vig = 1.0 - smoothstep(0.18, 0.78, length(vd) * 1.25);
                col *= lerp(1.0, vig, _Vignette);
                // フィルムの粒（暗部ほど目立つ）
                float2 px = uv * _MainTex_TexelSize.zw;
                float g = Hash(px + _Frame * 37.7) + Hash(px * 1.7 - _Frame * 11.3) - 1.0;
                col += g * _Grain * (0.55 + 0.3 * Luma(col));
                // 上下の帯（映画の画角）
                float band = step(_Letterbox, uv.y) * step(uv.y, 1.0 - _Letterbox);
                col *= band;
                return float4(saturate(col), 1);
            }
            ENDCG
        }
    }
}
