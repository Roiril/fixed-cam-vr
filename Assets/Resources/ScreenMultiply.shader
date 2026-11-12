// 増えるスクリーンの面と箱（参考 mul.jpg・走馬灯）。進み方は AnomalyEyesLogic の時計、画は ScreenMultiply が取り込む。
//
// 取り込みはメインスクリーンの最終の画を筐体ごと写したもの。外形・ガラス・乱れ・人形・テレビの枠がそのまま付いて来る。
// 面は自前の枠を描かない（0324: 枠はメインと同じにする）。ここが足すのは管の中の色味・電源の入り切り・面ごとの揺れだけ。
// 管と筐体の境はメインと同じ輪郭のマスク（_WallTubeMask）。マスクが無い画面は取り込みの alpha が管の外形。
// ⚠ 値は linear。0.05 でも sRGB では 0.25 の灰色に見える。筐体と箱は 0.01 より下で考える。
//
// 闇には黒い霧がある（ScreenFog.hlsl）。面は距離で暗くなり、取り込みの mip でぼける。
// 点いた面は席より奥から寄ってくるので、霧の中から出てくるように見える。
// 点いた直後の管は橙で、席の寒色へ落ち着く。面は帯ごとに頭のまわりをゆっくり回る（ScreenMultiplyCommon.hlsl）。
// 消える面はブラウン管の電源断: 上下に潰れて横一線 → 点 → 残光。
// _WallMirror = 1 の材質は床に映した灯（暗く・ぼけて・水平に揺らぐ）。
//
// ⚠ Geometry+50: メインスクリーンの後に描いて、隠れた所を深度で捨てる。
// ⚠ Resources に置いてある（Always Included に入れずにビルドへ入れる）。ScreenMultiply が Resources.Load で引く。
Shader "FixedCamVr/ScreenMultiply"
{
    Properties
    {
        [HideInInspector] _FeedTex("Feed Ring", 2DArray) = "black" {}
        [HideInInspector] _WallTubeMask("Tube Mask", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry+50" }
        LOD 100

        Pass
        {
            Name "ScreenMultiply"
            ZWrite On
            ZTest LEqual
            Cull Off

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma target 3.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "ScreenFog.hlsl"
            #include "ScreenMultiplyCommon.hlsl"

            TEXTURE2D_ARRAY(_FeedTex);
            SAMPLER(sampler_FeedTex);
            TEXTURE2D(_WallTubeMask);
            SAMPLER(sampler_WallTubeMask);

            CBUFFER_START(UnityPerMaterial)
                float _WallBig;
                float _WallField;
                float _WallSpan;
                float _WallClosing;
                float _WallTime;
                float _WallRingHead;
                float _WallRingFilled;
                float _WallRingCount;
                float _WallDesat;
                float _WallGain;
                float _WallBodyGain;
                float _WallNow;
                float _WallArriveSec;
                float _WallEmergeM;
                float _WallFogDensity;
                float _WallFogClearM;
                float _WallFogBlur;
                float _WallFarShow;
                float _WallHasTubeMask;
                float _WallAfterglowSec;
                float _WallMirror;
                float _WallFloorY;
                float _WallMirrorFadeM;
                float4 _WallWarm;        // rgb 点いた直後の色 / w 落ち着くまでの秒
                float4 _WallDrift;       // 帯 0..3 の回った角（rad）
                float4 _WallMirrorLook;  // 明るさ / 足すぼけ / 揺らぎの振幅 rad / 揺らぎの周期 秒
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float4 uv0 : TEXCOORD0;
                float4 uv1 : TEXCOORD1;
                float4 uv2 : TEXCOORD2;     // xyz 席の中心 / w 席の番号
                float4 uv3 : TEXCOORD3;     // x 帯の番号 / y 陰り
                float4 color : COLOR;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uv : TEXCOORD0;      // xy 面の uv / z 部位 / w 箱の奥行き
                float4 seat : TEXCOORD1;    // x 開き / y 点いた直後の明るさ / z 席の乱数 / w リングの段
                float3 tint : TEXCOORD2;
                float3 extra : TEXCOORD3;   // x 霧を抜けて届く光 / y 残光 1..0 / z 床の映り込みの濃さ
                UNITY_VERTEX_OUTPUT_STEREO
            };

            float Hash11(float x)
            {
                x = frac(x * 0.1031);
                x *= x + 33.33;
                x *= x + x;
                return frac(x);
            }

            float Hash21(float2 p)
            {
                float3 p3 = frac(float3(p.xyx) * 0.1031);
                p3 += dot(p3, p3.yzx + 33.33);
                return frac((p3.x + p3.y) * p3.z);
            }

            Varyings vert(Attributes v)
            {
                Varyings o = (Varyings)0;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);

                float rank = v.uv1.x;
                // AnomalyEyesLogic.EyeOpen と同じ式。0 番だけ大きい目の開きに従う。
                float span = max(0.005, _WallSpan);
                float open = v.uv1.w > 0.5 ? _WallBig : saturate((_WallField * (1.0 + span) - rank) / span);

                // 点いた直後だけ明るく、少しかけて落ち着く。閉じるときは足さない。
                float since = v.uv1.w > 0.5 ? saturate((_WallBig - 0.6) / 0.4) : saturate((_WallField - rank) / 0.14);
                float settle = (1.0 - since) * (1.0 - _WallClosing);

                float filled = max(1.0, _WallRingFilled);
                float d = min(v.uv1.y, filled - 1.0);
                float slot = fmod(_WallRingHead - d + _WallRingCount, _WallRingCount);

                // 点いた時刻・消えた時刻（ScreenMultiply が配る）。
                float seatIndex = floor(v.uv2.w + 0.5);
                float openAt = WallSeatOpenAt(seatIndex);
                float after = WallAfterglow(open, WallSeatClosedAt(seatIndex), _WallNow, _WallAfterglowSec);

                // 点いた面は霧の奥から席まで寄る。
                float arrive = saturate((_WallNow - openAt) / max(0.05, _WallArriveSec));
                float left = (1.0 - arrive) * (1.0 - arrive) * (1.0 - arrive);
                float3 pos = v.positionOS.xyz
                           + normalize(v.uv2.xyz) * (left * _WallEmergeM * (0.75 + 0.5 * v.uv1.z));
                // 帯ごとにゆっくり回る（ScreenMultiplyLayout.DriftDeg）。
                pos = WallRotateY(pos, dot(_WallDrift, WallLane(v.uv3.x)));
                float mirrorFade = 1.0;
                if (_WallMirror > 0.5)
                {
                    pos = WallMirror(pos, _WallFloorY, _WallMirrorLook, _WallNow);
                    mirrorFade = WallMirrorFade(pos, _WallMirrorFadeM);
                }
                float dist = max(0.001, length(pos));
                float fog = ScreenFogTransmit(dist, _WallFogDensity, _WallFogClearM,
                                              ScreenFogWisp(pos / dist, _WallNow));
                // 霧の奥の段は color.a = 0。プレビューの比較で消せる。
                float shown = step(0.001, max(open, after)) * step(0.5, max(v.color.a, _WallFarShow))
                            * step(0.001, mirrorFade);

                o.positionCS = shown > 0.5 ? TransformObjectToHClip(pos) : float4(0, 0, -1, 1);
                o.uv = v.uv0;
                o.seat = float4(open, settle, v.uv1.z, slot);
                o.tint = WallTint(v.color.rgb, _WallNow - openAt, _WallWarm) * v.uv3.y;
                o.extra = float3(fog, after, mirrorFade);
                return o;
            }

            half4 frag(Varyings i) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float open = i.seat.x;
                float seed = i.seat.z;
                float fogLight = i.extra.x;
                // 床に映った灯は暗く、頭から離れるほど薄い。
                float mirror = lerp(1.0, _WallMirrorLook.x * i.extra.z, _WallMirror);

                float flutter = 1.0 + 0.045 * sin(_WallTime * (3.0 + seed * 9.0) + seed * 40.0)
                                    + 0.02 * sin(_WallTime * (17.0 + seed * 23.0));

                // 電源の落ちた面は残光の点だけを描く。黒い筐体と箱で深度を書くと、
                // 後ろの面の暈が四角く切れる（閉じの途中に 0 番の暈が欠けた）。
                if (open <= 0.001)   // WallAfterglow と同じ境
                {
                    float2 pa = (i.uv.xy - 0.5) * float2(1.2427, 1.0);   // 面の高さを 1 とした座標（CrtReferenceScreen.CaseAspect）
                    float ra = dot(pa, pa);
                    float glow = (exp(-ra / 0.0005) * 3.0 + exp(-ra / 0.006) * 0.3) * i.extra.y * i.extra.y
                               * (1.0 - step(0.5, i.uv.z));
                    float3 dot3 = i.tint * glow * fogLight * mirror;
                    // 表示の 1 階調より暗い所は描かない（縁が円盤に見えない）。
                    clip(max(dot3.r, max(dot3.g, dot3.b)) - 0.0002);
                    return half4(dot3, 1.0);
                }

                // 箱: 自分の画の光が前の縁にだけ回り、奥へ消える。
                if (i.uv.z > 0.5)
                {
                    float fall = 1.0 - i.uv.w;
                    float3 body = i.tint * (0.0015 + _WallBodyGain * fall * fall) * saturate(open * 2.0) * flutter;
                    return half4(body * fogLight * mirror, 1.0);
                }

                float2 face = saturate(i.uv.xy) * 0.994 + 0.003;
                // 霧を抜けきれない分だけぼかす。床の映り込みはさらにぼかす。
                float blur = (1.0 - fogLight) * _WallFogBlur + _WallMirror * _WallMirrorLook.y;

                // 管の輪郭（メインと同じマスク）。筐体の無い画面は 1 で、取り込みの alpha が外形を決める。
                float tubeMask = _WallHasTubeMask > 0.5
                    ? smoothstep(0.4, 0.6, SAMPLE_TEXTURE2D_LOD(_WallTubeMask, sampler_WallTubeMask, face, 0).a)
                    : 1.0;

                // 筐体: 取り込んだメインの枠をそのままの色で出す（メインと揃える。管の色味は掛けない）。
                half4 shell = SAMPLE_TEXTURE2D_ARRAY_LOD(_FeedTex, sampler_FeedTex, face, i.seat.w, blur);
                float body = smoothstep(0.35, 0.65, shell.a);
                float power = saturate(open * 2.0);

                // ときどき同期を失う面。ずれるのは管の中の映像だけ（筐体は動かない）。
                float tick = floor(_WallTime * 5.0 + seed * 17.0);
                float torn = step(0.955, Hash11(tick + seed * 91.0));
                float rowId = floor(i.uv.y * 26.0);
                float2 tornUv = face;
                tornUv.x = saturate(tornUv.x + torn * (Hash21(float2(rowId, tick)) - 0.5) * 0.07);
                half4 feed = SAMPLE_TEXTURE2D_ARRAY_LOD(_FeedTex, sampler_FeedTex, tornUv, i.seat.w, blur);
                float tube = smoothstep(0.35, 0.65, feed.a) * tubeMask;

                // 面ごとの明るさの差（参考の、弱った管と明るい管の混ざり方）。
                float lamp = lerp(0.5, 1.05, seed);
                float luma = dot(feed.rgb, float3(0.30, 0.59, 0.11));
                float3 picture = lerp(feed.rgb, luma.xxx, _WallDesat) * i.tint * _WallGain * lamp * flutter;

                if (torn > 0.5)
                {
                    float sand = Hash21(floor(i.uv.xy * float2(160.0, 130.0)) + tick);
                    picture = lerp(picture, i.tint * sand * 0.8, 0.35);
                }

                // 管の電源: 中央の線が縦に開く。閉じるときは同じ形で潰れる（上下が潰れて横一線 → 点）。
                float2 c = i.uv.xy - 0.5;
                float halfH = smoothstep(0.06, 0.62, open) * 0.5 + 0.004;
                float halfW = smoothstep(0.0, 0.16, open) * 0.5;
                float inside = step(abs(c.y), halfH) * step(abs(c.x), halfW) * tube;

                // 消えている管のガラスは暗い。筐体は管の外（tubeMask の外）だけ。
                float3 glass = i.tint * 0.004 * power * flutter;
                float3 col = lerp(glass, shell.rgb * power, body * (1.0 - tubeMask));

                // 開く途中の光と線。点いた直後の明るさも管の中だけ。
                float beam = 1.0 - smoothstep(0.0, 0.75, open);
                picture += i.tint * beam * 2.0;
                picture *= 1.0 + i.seat.y * 0.55;
                col = lerp(col, picture, inside);
                col += i.tint * beam * exp(-abs(c.y) * 90.0) * step(abs(c.x), halfW) * 3.0 * tube;

                return half4(col * fogLight * mirror, 1.0);
            }
            ENDHLSL
        }
    }
}
