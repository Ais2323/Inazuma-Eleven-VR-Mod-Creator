Shader "G4/HeadPreview"
{
    Properties
    {
        _MainTex ("Base texture", 2D) = "white" {}
        _OcTex ("OC shadow texture", 2D) = "white" {}
        _SpmTex ("SPM highlight texture", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3
        _OcDarkness ("OC darkness", Range(0,1)) = 0.55
        _SpmStrength ("SPM strength", Range(0,1)) = 0.35
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        Cull Back
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _OcTex, _SpmTex;
            float4 _MainTex_ST, _Color;
            float _Cutoff, _OcDarkness, _SpmStrength;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; };
            Output vert(Input v)
            {
                Output o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }
            fixed4 frag(Output i) : SV_Target
            {
                fixed4 color = tex2D(_MainTex, i.uv) * _Color;
                clip(color.a - _Cutoff);
                fixed3 oc = tex2D(_OcTex, i.uv).rgb;
                fixed3 spm = tex2D(_SpmTex, i.uv).rgb;
                float ocMask = saturate(max(oc.r, oc.g));
                float spmMask = saturate(max(spm.r, max(spm.g, spm.b)));
                color.rgb *= lerp(_OcDarkness, 1.0, ocMask);
                color.rgb = lerp(color.rgb, fixed3(1, 1, 1), spmMask * _SpmStrength);
                return fixed4(color.rgb, 1);
            }
            ENDCG
        }
    }
}
