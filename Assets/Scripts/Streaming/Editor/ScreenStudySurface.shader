// Editor-only comparison surface. No runtime material refers to this shader.
Shader "Hidden/FixedCamVr/ScreenStudySurface"
{
    Properties {
        _MainTex("Processed source", 2D) = "black" {}
        _Mode("Mode (-1 passthrough)", Float) = 1
        _Strength("Glass interpretation", Range(0,1)) = 1
        _Bezel("Case", Range(0,1)) = 0
    }
    SubShader {
        Tags { "RenderType"="Opaque" "RenderPipeline"="UniversalPipeline" }
        Pass {
            Cull Off ZWrite Off
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            TEXTURE2D(_MainTex); SAMPLER(sampler_MainTex);
            float4 _MainTex_TexelSize;
            float _Mode, _Strength, _Bezel;
            struct Attributes { float4 positionOS : POSITION; float2 uv : TEXCOORD0; };
            struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; };
            Varyings vert(Attributes input) {
                Varyings o; o.positionCS=TransformObjectToHClip(input.positionOS.xyz); o.uv=input.uv; return o;
            }
            float rounded(float2 p, float2 extent, float radius) {
                float2 q=abs(p)-extent+radius;
                return length(max(q,0))+min(max(q.x,q.y),0)-radius;
            }
            float3 readColor(float2 uv) { return SAMPLE_TEXTURE2D(_MainTex,sampler_MainTex,uv).rgb; }
            half4 frag(Varyings input) : SV_Target {
                if (_Mode<0) return half4(readColor(input.uv),1);
                float aspect=16.0/9.0;
                float2 p=(input.uv-0.5)*2;
                float inset=lerp(1,0.9,_Bezel);
                float2 g=p/inset;
                float2 metric=g*float2(aspect,1);
                // Large old glass corners; source remains contain-fit, no barrel UV crop.
                float d=rounded(metric,float2(aspect,1),lerp(0.12,0.23,_Strength));
                float aa=max(fwidth(d),0.001);
                float inside=saturate(-d/aa);
                float2 uv=g*0.5+0.5;
                float2 radial=uv-0.5;
                float shift=lerp(0.0005,0.0026,_Strength);
                float2 split=radial*shift;
                float3 col=float3(readColor(uv+split).r,readColor(uv).g,readColor(uv-split).b);
                // Soft edges + highlight spread. All operations on the linear render texture.
                float2 pixel=_MainTex_TexelSize.xy;
                float3 nearBlur=(readColor(uv+float2(pixel.x,0))+readColor(uv-float2(pixel.x,0))
                                +readColor(uv+float2(0,pixel.y))+readColor(uv-float2(0,pixel.y)))*0.25;
                col=lerp(col,nearBlur,lerp(0.16,0.48,_Strength));
                float3 halo=0;
                for (int tap=0;tap<8;tap++) {
                    float theta=tap*0.78539816;
                    float2 delta=float2(cos(theta),sin(theta))*pixel*9;
                    halo+=max(readColor(uv+delta)-0.13,0);
                }
                col+=halo*(lerp(0.035,0.11,_Strength)/8);
                float edge=1-saturate(-d/0.19);
                col*=1-lerp(0.25,0.53,_Strength)*edge*edge;
                // Reflection concentrated at the rim, not a broad wash over the room.
                float rim=exp(-abs(d+0.019)*140);
                float directional=saturate(0.45+g.x*0.35+g.y*0.3);
                col+=rim*directional*lerp(0.008,0.032,_Strength);
                float arc=exp(-pow((g.y-(0.935-0.07*g.x*g.x))*85,2));
                col+=arc*smoothstep(-0.8,0.85,g.x)*lerp(0.002,0.012,_Strength);
                float3 result=col*inside;
                if (_Bezel>0.5) {
                    float2 m=p*float2(aspect,1);
                    float outer=rounded(m,float2(aspect*0.995,0.995),0.13);
                    float outerMask=saturate(-outer/max(fwidth(outer),0.001));
                    float innerEdge=max(d,0);
                    float lip=exp(-innerEdge*120);
                    float band=saturate(innerEdge/0.09);
                    float light=saturate(0.5+p.y*0.35-p.x*0.13);
                    float casing=0.009+0.011*light;
                    casing+=0.014*exp(-pow((band-0.7)*5,2))*light;
                    casing-=lip*0.008;
                    float outerLip=exp(-abs(outer+0.018)*150);
                    casing+=outerLip*(0.004+0.012*light);
                    // Faint satin grain rather than a painted solid rectangle.
                    float textureGrain=frac(sin(dot(floor(input.uv*float2(1920,1080)),float2(12.9898,78.233)))*43758.5453);
                    casing*=0.96+textureGrain*0.08;
                    result=lerp(casing.xxx,result,inside)*outerMask;
                }
                return half4(max(result,0),1);
            }
            ENDHLSL
        }
    }
}
