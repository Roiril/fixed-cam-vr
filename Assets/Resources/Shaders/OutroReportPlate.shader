Shader "FixedCamVr/OutroReportPlate"
{
    Properties { _Failed ("Return impossible", Float) = 0 }
    SubShader
    {
        Tags { "Queue"="Overlay-2" "RenderType"="Transparent" "RenderPipeline"="UniversalPipeline" }
        Pass
        {
            ZWrite Off ZTest Always Cull Off
            Blend SrcAlpha OneMinusSrcAlpha
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            struct A { float4 positionOS:POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct V { float4 positionCS:SV_POSITION; float2 uv:TEXCOORD0; UNITY_VERTEX_OUTPUT_STEREO };
            CBUFFER_START(UnityPerMaterial)
            float _Failed;
            CBUFFER_END
            V vert(A i) { V o; UNITY_SETUP_INSTANCE_ID(i); UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o); o.positionCS=TransformObjectToHClip(i.positionOS.xyz); o.uv=i.uv; return o; }
            float stroke(float d, float w) { return 1-smoothstep(w, w+fwidth(d), abs(d)); }
            half4 frag(V i):SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(i);
                float2 p=(i.uv-.5)*float2(2.42,1.78);
                float3 ink=float3(.82,.78,.72);
                float3 accent=lerp(ink,float3(1,.55,.40),_Failed);
                float3 col=float3(.019,.021,.023);
                float frame=max(stroke(abs(p.x)-1.17,.0014)*step(abs(p.y),.82),
                    stroke(abs(p.y)-.85,.0014)*step(abs(p.x),1.14));
                float corners=max(stroke(abs(p.x)-1.14,.0028)*step(.72,abs(p.y))*step(abs(p.y),.82),
                    stroke(abs(p.y)-.82,.0028)*step(1.04,abs(p.x))*step(abs(p.x),1.14));
                float rules=max(stroke(p.y-.355,.001)*step(abs(p.x),1.01),
                    max(stroke(p.y+.018,.001),stroke(p.y+.65,.001))*step(abs(p.x),1.01));
                float rail=step(-1.095,p.x)*step(p.x,-1.078)*step(.365,p.y)*step(p.y,.56);
                float ticks=stroke(frac((p.y+.8)*25)-.5,.10)*step(-1.14,p.x)*step(p.x,-1.127)*step(abs(p.y),.66);
                col=lerp(col,ink*.22,max(frame,rules));
                col=lerp(col,accent*.74,max(corners,rail));
                col=lerp(col,ink*.24,ticks);
                return half4(col,1);
            }
            ENDHLSL
        }
    }
}
