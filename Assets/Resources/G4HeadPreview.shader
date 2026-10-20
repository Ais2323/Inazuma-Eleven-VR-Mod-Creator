Shader "G4/HeadPreview"
{
    Properties
    {
        _MainTex ("Base texture", 2D) = "white" {}
        _OcTex ("OC shadow texture", 2D) = "white" {}
        _SpmTex ("SPM highlight texture", 2D) = "black" {}
        _MskTex ("Skin color mask", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _HairColor ("Hair color", Color) = (1,1,1,1)
        _RecolorWhite ("Recolor white base pixels", Float) = 0
        _SkinColor ("Skin color", Color) = (1,1,1,1)
        _HasMsk ("Has skin mask", Float) = 0
        _WholeSkin ("Whole material uses skin color", Float) = 0
        _Cutoff ("Alpha cutoff", Range(0,1)) = 0.3
        _OcDarkness ("OC darkness", Range(0,1)) = 0.55
        _OcUseGreen ("OC uses green channel", Float) = 0
        _DetailUvST ("Detail UV scale and offset", Vector) = (1,1,0,0)
        _SpmStrength ("SPM strength", Range(0,1)) = 0.35
        _DepthBiasFactor ("Depth bias factor", Float) = 0
        _DepthBiasUnits ("Depth bias units", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="AlphaTest" "RenderType"="TransparentCutout" }
        Cull Back
        Pass
        {
            Offset [_DepthBiasFactor], [_DepthBiasUnits]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _OcTex, _SpmTex, _MskTex;
            float4 _MainTex_ST, _Color, _SkinColor, _HairColor, _DetailUvST;
            float _Cutoff, _OcDarkness, _OcUseGreen, _SpmStrength, _HasMsk, _WholeSkin, _RecolorWhite;
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
                // Fixed accessory colors in the hair base must survive recoloring.
                float whiteBase = step(0.999, min(color.r, min(color.g, color.b))) * step(0.5, _RecolorWhite);
                color.rgb = lerp(color.rgb, _HairColor.rgb, whiteBase);
                clip(color.a - _Cutoff);
                float2 detailUV = i.uv * _DetailUvST.xy + _DetailUvST.zw;
                fixed3 oc = tex2D(_OcTex, detailUV).rgb;
                fixed3 spm = tex2D(_SpmTex, detailUV).rgb;
                fixed3 msk = tex2D(_MskTex, i.uv).rgb;
                float pureRed = step(0.999, msk.r) * step(msk.g, 0.001) * step(msk.b, 0.001) * step(0.5, _HasMsk);
                color.rgb = lerp(color.rgb, _SkinColor.rgb, max(pureRed, step(0.5, _WholeSkin)));
                float ocMask = saturate(lerp(max(oc.r, oc.g), oc.g, _OcUseGreen));
                float spmMask = saturate(max(spm.r, max(spm.g, spm.b)));
                color.rgb *= lerp(_OcDarkness, 1.0, ocMask);
                color.rgb = lerp(color.rgb, fixed3(1, 1, 1), spmMask * _SpmStrength);
                return fixed4(color.rgb, 1);
            }
            ENDCG
        }
    }
}
