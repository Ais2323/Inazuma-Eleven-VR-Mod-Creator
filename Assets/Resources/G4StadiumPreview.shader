Shader "CharMaker/G4StadiumPreview"
{
    Properties {
        _MainTex ("Native surface",2D)="white" {}
        _UvRow0 ("Native U",Vector)=(1,0,0,0)
        _UvRow1 ("Native V",Vector)=(0,1,0,0)
        _Grass ("Native grass surface",Float)=0
        _MaskTex ("Grass color weights",2D)="white" {}
        _MaskRow0 ("Grass weight U",Vector)=(1,0,0,0)
        _MaskRow1 ("Grass weight V",Vector)=(0,1,0,0)
        _Param1 ("Grass shading controls",Vector)=(1,0,0,0)
        _Param2 ("Grass shading tint",Vector)=(1,1,1,0)
    }
    SubShader {
        Tags {"Queue"="Geometry" "RenderType"="Opaque"}
        Cull Back ZWrite On
        Pass {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"
            sampler2D _MainTex,_MaskTex;float4 _UvRow0,_UvRow1,_MaskRow0,_MaskRow1,_Param1,_Param2;float _Grass;
            struct Input{float4 vertex:POSITION;float2 uv:TEXCOORD0;float2 uv1:TEXCOORD1;float4 color:COLOR;};
            struct Output{float4 position:SV_POSITION;float2 uv:TEXCOORD0;float2 mask:TEXCOORD1;float4 color:COLOR;};
            Output vert(Input v){Output o;o.position=UnityObjectToClipPos(v.vertex);float3 uv=float3(v.uv,1);o.uv=float2(dot(_UvRow0.xyz,uv),dot(_UvRow1.xyz,uv));uv=float3(v.uv1,1);o.mask=float2(dot(_MaskRow0.xyz,uv),dot(_MaskRow1.xyz,uv));o.color=v.color;return o;}
            float4 frag(Output i):SV_Target{
                float4 c=tex2D(_MainTex,i.uv);
                if(_Grass>.5){
                    // Native color/weight combination, without the expensive
                    // parallax ray march and game-specific light buffers.
                    float4 weight=tex2D(_MaskTex,i.mask);weight.rgb*=i.color.rgb;
                    float amount=lerp(c.a,1,weight.a*_Param1.w);
                    float3 tint=lerp(_Param2.rgb,1,amount);
                    float3 detail=c.rgb*(1-(1-weight.a)*_Param1.z);
                    return float4(1-(1-weight.rgb*tint)*(1-detail),1);
                }
                clip(c.a-.3);return float4(c.rgb,1);
            }
            ENDCG
        }
    }
}
