Shader "CharMaker/G4EventCharacter"
{
    Properties
    {
        _MainTex ("Base texture", 2D) = "white" {}
        _OcTex ("OC shadow texture", 2D) = "white" {}
        _SpmTex ("SPM highlight texture", 2D) = "black" {}
        _MskTex ("Skin color mask", 2D) = "black" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _LightTint ("Event light", Color) = (1,1,1,1)
        _StatusColor ("Native status tint", Vector) = (1,1,1,1)
        _StatusRim ("Native status rim", Vector) = (1,1,1,0)
        _StatusPower ("Native status rim power", Float) = 10
        _Gain ("Native ambient gain", Float) = 1
        _ModelOpacity ("Event model opacity", Float) = 1
        _Glass ("Layered prop glass", Float) = 0
        _GlassReflection ("Prop sphere reflection", 2D) = "black" {}
        _SrcBlend ("Source blend", Float) = 1
        _DstBlend ("Destination blend", Float) = 0
        _ZWrite ("Depth write", Float) = 1
        _EventUvRow0 ("Event U", Vector) = (1,0,0,0)
        _EventUvRow1 ("Event V", Vector) = (0,1,0,0)
        _UniformLabelColors ("Use uniform lettering colors", Float) = 0
        _LabelPrimaryColor ("Lettering primary color", Color) = (0,0,0,1)
        _LabelSecondaryColor ("Lettering secondary color", Color) = (1,1,1,1)
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
        Blend [_SrcBlend] [_DstBlend]
        ZWrite [_ZWrite]
        Pass
        {
            Offset [_DepthBiasFactor], [_DepthBiasUnits]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex, _OcTex, _SpmTex, _MskTex,_GlassReflection;
            float4 _MainTex_ST, _Color, _SkinColor, _HairColor, _DetailUvST;
            float4 _LabelPrimaryColor, _LabelSecondaryColor, _LightTint, _EventUvRow0, _EventUvRow1;
            float4 _StatusColor,_StatusRim;float _StatusPower;
            float _UniformLabelColors,_Glass,_ModelOpacity,_Gain;
            float _Cutoff, _OcDarkness, _OcUseGreen, _SpmStrength, _HasMsk, _WholeSkin, _RecolorWhite;
            struct Input { float4 vertex : POSITION; float2 uv : TEXCOORD0; float3 normal:NORMAL; };
            struct Output { float4 position : SV_POSITION; float2 uv : TEXCOORD0; float3 normal:TEXCOORD1; float3 view:TEXCOORD2; };
            Output vert(Input v)
            {
                Output o;
                o.position = UnityObjectToClipPos(v.vertex);
                o.normal=UnityObjectToWorldNormal(v.normal);o.view=UnityWorldSpaceViewDir(mul(unity_ObjectToWorld,v.vertex).xyz);
                float3 uv = float3(v.uv,1);
                o.uv = float2(dot(_EventUvRow0.xyz,uv),dot(_EventUvRow1.xyz,uv));
                return o;
            }
            float4 frag(Output i) : SV_Target
            {
                float4 color = tex2D(_MainTex, i.uv) * _Color * _LightTint;
                if(_Glass>.5){
                    // Optical preview for the native paired shell: preserve the
                    // interior and show the authored sphere highlight at the rim.
                    float3 n=normalize(mul((float3x3)UNITY_MATRIX_V,normalize(i.normal)));
                    float2 sphere=n.xy*float2(.5,-.5)+.5;
                    float3 reflection=tex2D(_GlassReflection,sphere).rgb;
                    float fresnel=pow(1-saturate(abs(dot(normalize(i.normal),normalize(i.view)))),3);
                    return float4(lerp(color.rgb,reflection,saturate(fresnel+.25)),color.a*(.08+fresnel*.42)*_ModelOpacity);
                }
                // Fixed accessory colors in the hair base must survive recoloring.
                float whiteBase = step(0.999, min(color.r, min(color.g, color.b))) * step(0.5, _RecolorWhite);
                color.rgb = lerp(color.rgb, _HairColor.rgb, whiteBase);
                clip(color.a - _Cutoff);
                float2 detailUV = i.uv * _DetailUvST.xy + _DetailUvST.zw;
                fixed3 oc = tex2D(_OcTex, detailUV).rgb;
                fixed3 spm = tex2D(_SpmTex, detailUV).rgb;
                fixed3 msk = tex2D(_MskTex, i.uv).rgb;
                if (_UniformLabelColors > 0.5) {
                    color.rgb *= lerp(fixed3(1,1,1), _LabelPrimaryColor.rgb, msk.g);
                    color.rgb *= lerp(fixed3(1,1,1), _LabelSecondaryColor.rgb, msk.b);
                }
                float pureRed = step(0.999, msk.r) * step(msk.g, 0.001) * step(msk.b, 0.001) * step(0.5, _HasMsk);
                color.rgb = lerp(color.rgb, _SkinColor.rgb, max(pureRed, step(0.5, _WholeSkin)));
                float ocMask = saturate(lerp(max(oc.r, oc.g), oc.g, _OcUseGreen));
                // A specular mask is not a uniform white overlay. Restrict it
                // to the light/view half-vector and preserve the surface hue.
                float3 halfVector=normalize(normalize(i.view)+normalize(float3(.3,.8,.5)));
                float specular=pow(saturate(dot(normalize(i.normal),halfVector)),32);
                float spmMask=saturate(spm.b)*specular;
                color.rgb *= lerp(_OcDarkness, 1.0, ocMask);
                color.rgb += color.rgb * spmMask * _SpmStrength;
                float facing=saturate(dot(normalize(i.normal),normalize(i.view)));
                float3 rim=_StatusRim.rgb*_StatusRim.w*pow(1-facing,max(_StatusPower,.0001));
                float3 limited=.3*saturate(rim);
                color.rgb=color.rgb*_StatusColor.rgb+min(5*color.a,1)*(rim-limited)+limited;
                return float4(color.rgb*_Gain, _ModelOpacity);
            }
            ENDCG
        }
    }
}
