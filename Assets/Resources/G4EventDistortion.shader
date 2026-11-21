Shader "CharMaker/G4EventDistortion"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _MaskTex ("Mask", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Gain ("Gain", Float) = 1
        _UseMask ("Use mask", Float) = 0
        _SrcBlend ("Source blend", Float) = 5
        _DstBlend ("Destination blend", Float) = 10
        _UvRow0 ("Texture U", Vector) = (1,0,0,0)
        _UvRow1 ("Texture V", Vector) = (0,1,0,0)
        _MaskRow0 ("Mask U", Vector) = (1,0,0,0)
        _MaskRow1 ("Mask V", Vector) = (0,1,0,0)
        _AddressModes ("Texture and mask UV addressing", Vector) = (1,1,1,1)
        _ExtraTex ("Second threshold mask", 2D) = "white" {}
        _ExtraRow0 ("Second mask U", Vector) = (1,0,0,0)
        _ExtraRow1 ("Second mask V", Vector) = (0,1,0,0)
        _ExtraAddressU ("Second mask address U", Float) = 1
        _ExtraAddressV ("Second mask address V", Float) = 1
        _Threshold ("Threshold gradient", Float) = 0
        _Pattern ("Animated pattern atlas", Float) = 0
        _SolidToon ("Solid toon surface", Float) = 0
        _ZWrite ("Depth write", Float) = 0
        _Cull ("Cull mode", Float) = 0
        _ZTest ("Depth test", Float) = 4
        _Family ("Native material family", Float) = 0
        _Flow ("Threshold flow map", Float) = 0
        _Tex3 ("Sampler 3", 2D) = "white" {}
        _Tex4 ("Sampler 4", 2D) = "white" {}
        _Tex5 ("Sampler 5", 2D) = "white" {}
        _Tex3Row0 ("Sampler 3 U", Vector) = (1,0,0,0)
        _Tex3Row1 ("Sampler 3 V", Vector) = (0,1,0,0)
        _Tex4Row0 ("Sampler 4 U", Vector) = (1,0,0,0)
        _Tex4Row1 ("Sampler 4 V", Vector) = (0,1,0,0)
        _Tex5Row0 ("Sampler 5 U", Vector) = (1,0,0,0)
        _Tex5Row1 ("Sampler 5 V", Vector) = (0,1,0,0)
        _Tex3AddressU ("Sampler 3 address U", Float) = 1
        _Tex3AddressV ("Sampler 3 address V", Float) = 1
        _Tex4AddressU ("Sampler 4 address U", Float) = 1
        _Tex4AddressV ("Sampler 4 address V", Float) = 1
        _Tex5AddressU ("Sampler 5 address U", Float) = 1
        _Tex5AddressV ("Sampler 5 address V", Float) = 1
        _Param0 ("Parameter 0", Vector) = (0,0,0,0)
        _Param1 ("Parameter 1", Vector) = (0,0,0,0)
        _Param2 ("Parameter 2", Vector) = (0,0,0,0)
        _Param3 ("Parameter 3", Vector) = (0,0,0,0)
        _Param4 ("Parameter 4", Vector) = (0,0,0,0)
        _Param5 ("Parameter 5", Vector) = (0,0,0,0)
        _Param6 ("Parameter 6", Vector) = (0,0,0,0)
        _Param7 ("Parameter 7", Vector) = (0,0,0,0)
    }
    SubShader
    {
        Tags {"Queue"="Transparent" "RenderType"="Transparent"}
        Cull [_Cull] ZWrite [_ZWrite] ZTest [_ZTest] Blend [_SrcBlend] [_DstBlend]
        GrabPass { "_HissatsuSceneColor" }
        UsePass "CharMaker/G4EventEffect/Effect"
    }
}
