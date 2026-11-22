Shader "CharMaker/HissatsuPreviewDisplay"
{
    Properties
    {
        [PerRendererData] _MainTex ("Preview", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
        [Toggle(UNITY_UI_ALPHACLIP)] _UseUIAlphaClip ("Use Alpha Clip", Float) = 0
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "IgnoreProjector"="True" "RenderType"="Transparent" "PreviewType"="Plane" "CanUseSpriteAtlas"="True" }
        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }
        Cull Off Lighting Off ZWrite Off ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #pragma multi_compile_local _ UNITY_UI_ALPHACLIP
            #include "UnityCG.cginc"
            #include "UnityUI.cginc"
            sampler2D _MainTex;
            fixed4 _Color;
            float4 _ClipRect;
            struct Input { float4 vertex:POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct Output { float4 vertex:SV_POSITION; fixed4 color:COLOR; float2 uv:TEXCOORD0; float4 local:TEXCOORD1; UNITY_VERTEX_OUTPUT_STEREO };
            Output vert(Input v)
            {
                Output o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(o);
                o.local=v.vertex;o.vertex=UnityObjectToClipPos(v.vertex);o.uv=v.uv;o.color=v.color*_Color;
                return o;
            }
            fixed4 frag(Output i):SV_Target
            {
                // The native event frame is a completed picture, not a cutout.
                // Its framebuffer alpha can contain zero from opaque effects.
                // Keep UI/CanvasGroup opacity, but don't use that framebuffer
                // alpha to composite the completed frame onto the editor UI.
                fixed4 color=fixed4(tex2D(_MainTex,i.uv).rgb*i.color.rgb,i.color.a);
                #ifdef UNITY_UI_CLIP_RECT
                color.a*=UnityGet2DClipping(i.local.xy,_ClipRect);
                #endif
                #ifdef UNITY_UI_ALPHACLIP
                clip(color.a-.001);
                #endif
                return color;
            }
            ENDCG
        }
    }
}
