Shader "Hidden/G4/AvatarMaterialBake"
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
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            Offset [_DepthBiasFactor], [_DepthBiasUnits]
            CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _OcTex, _SpmTex, _MskTex;
            float4 _MainTex_ST, _Color, _SkinColor, _HairColor, _DetailUvST;
            float _BakeChannel;
            float _Cutoff, _OcDarkness, _OcUseGreen, _SpmStrength, _HasMsk, _WholeSkin, _RecolorWhite;
            fixed4 frag(v2f_img i) : SV_Target
            {
                float2 uv = i.uv * _MainTex_ST.xy + _MainTex_ST.zw;
                fixed4 color = tex2D(_MainTex, uv) * _Color;
                // Fixed accessory colors in the hair base must survive recoloring.
                float whiteBase = step(0.999, min(color.r, min(color.g, color.b))) * step(0.5, _RecolorWhite);
                color.rgb = lerp(color.rgb, _HairColor.rgb, whiteBase);
                float alpha = step(_Cutoff, color.a);
                float2 detailUV = uv * _DetailUvST.xy + _DetailUvST.zw;
                fixed3 oc = tex2D(_OcTex, detailUV).rgb;
                fixed3 spm = tex2D(_SpmTex, detailUV).rgb;
                fixed3 msk = tex2D(_MskTex, uv).rgb;
                float pureRed = step(0.999, msk.r) * step(msk.g, 0.001) * step(msk.b, 0.001) * step(0.5, _HasMsk);
                color.rgb = lerp(color.rgb, _SkinColor.rgb, max(pureRed, step(0.5, _WholeSkin)));
                float ocMask = saturate(lerp(max(oc.r, oc.g), oc.g, _OcUseGreen));
                float spmMask = saturate(max(spm.r, max(spm.g, spm.b)));
                // Export base color and illumination maps independently.
                if (_BakeChannel > 2.5) return fixed4(spmMask, spmMask, spmMask, 1);
                if (_BakeChannel > 1.5) return fixed4(ocMask, ocMask, ocMask, 1);
                return fixed4(color.rgb, alpha);
            }
            ENDCG
        }
    }
}
