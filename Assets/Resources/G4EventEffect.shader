Shader "CharMaker/G4EventEffect"
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
        _VDisp ("Native vertex displacement", Float) = 0
        _ZWrite ("Depth write", Float) = 0
        _BlendOp ("Native blend operation", Float) = 0
        _Cull ("Cull mode", Float) = 0
        _ZTest ("Depth test", Float) = 4
        _BallAuraColumn ("Shot color palette column", Float) = 0
        _Family ("Native material family", Float) = 0
        _Flow ("Threshold flow map", Float) = 0
        _DistortionBlur ("Distortion blur variant", Float) = 1
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
        Cull [_Cull] ZWrite [_ZWrite] ZTest [_ZTest] Blend [_SrcBlend] [_DstBlend] BlendOp [_BlendOp]
        Pass
        {
            Name "Effect"
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.5
            #include "UnityCG.cginc"
            sampler2D _MainTex,_MaskTex,_ExtraTex,_Tex3,_Tex4,_Tex5,_HissatsuSceneColor;
            float4 _Color,_UvRow0,_UvRow1,_MaskRow0,_MaskRow1;
            float4 _AddressModes,_MainTex_TexelSize,_MaskTex_TexelSize;
            float4 _ExtraRow0,_ExtraRow1,_ExtraTex_TexelSize,_Param0,_Param1,_Param2,_Param3,_Param4,_Param5,_Param6;
            float4 _Tex3Row0,_Tex3Row1,_Tex4Row0,_Tex4Row1,_Tex5Row0,_Tex5Row1;
            float4 _Tex3_TexelSize,_Tex4_TexelSize,_Tex5_TexelSize;
            float _Tex3AddressU,_Tex3AddressV,_Tex4AddressU,_Tex4AddressV,_Tex5AddressU,_Tex5AddressV,_Family,_Flow;
            float _ExtraAddressU,_ExtraAddressV,_Threshold,_Pattern;
            float _SolidToon,_DistortionBlur,_VDisp;
            float _Gain,_UseMask,_BallAuraColumn;
            struct Input {float4 vertex:POSITION;float3 normal:NORMAL;float4 tangent:TANGENT;float2 uv:TEXCOORD0;float2 uv1:TEXCOORD1;float2 uv2:TEXCOORD2;float2 uv3:TEXCOORD3;float2 uv4:TEXCOORD4;float2 uv5:TEXCOORD5;float4 controls:TEXCOORD6;float4 color:COLOR;};
            struct Output {float4 vertex:SV_POSITION;float2 uv:TEXCOORD0;float2 mask:TEXCOORD1;float4 color:COLOR;float2 extra:TEXCOORD2;float4 normal:TEXCOORD3;float4 view:TEXCOORD4;float alpha:TEXCOORD5;float4 rawUv:TEXCOORD6;float4 screen:TEXCOORD7;float2 flowUv:TEXCOORD8;float4 controls:TEXCOORD9;};
            Output vert(Input v){
                Output o;
                if(_VDisp>.5){
                    float3 t=normalize(v.tangent.xyz),n=v.normal*(v.color.a*2-1);
                    float3 b=-cross(n,t); // reflect the native right-handed basis
                    float3 p=float3(v.uv3,1);float2 uv=float2(dot(_Tex3Row0.xyz,p),dot(_Tex3Row1.xyz,p));
                    float3 displacement=tex2Dlod(_Tex3,float4(uv,0,0)).xyz*2-1;
                    float3 scale=float3(length(unity_ObjectToWorld._m00_m10_m20),length(unity_ObjectToWorld._m01_m11_m21),length(unity_ObjectToWorld._m02_m12_m22));
                    v.vertex.xyz+=(t*displacement.x+b*displacement.y+n*displacement.z)*(_Param0.w*v.color.r*_Param0.z)/max(scale,.00001);
                    float3 normal=tex2Dlod(_Tex4,float4(uv,0,0)).xyz;
                    float3 q=float3((normal.xy*2-1)*(v.color.a*2-1),normal.z*2-2)*(_Param0.z*v.color.r)+float3(0,0,1);
                    v.normal=normalize(t*q.x-cross(v.normal,t)*q.y+v.normal*q.z);
                }
                if(_SolidToon>.5){float depth=abs(UnityObjectToViewPos(v.vertex.xyz).z);v.vertex.xyz+=normalize(v.normal)*_Param4.y*min(depth,_Param4.z)*v.color.b*.1;
                    float3 toward=ObjSpaceViewDir(v.vertex);float distance=length(toward);
                    v.vertex.xyz+=toward/max(distance,.00001)*min(distance,_Param4.w);
                }
                o.vertex=UnityObjectToClipPos(v.vertex);float3 uv=float3(v.uv,1);
                o.uv=float2(dot(_UvRow0.xyz,uv),dot(_UvRow1.xyz,uv));
                float3 maskUv=float3((_SolidToon>.5||_Family>5.5||_UseMask>.5||_Threshold>.5||(_Family>3.5&&_Family<4.5))?v.uv1:v.uv,1);
                o.mask=float2(dot(_MaskRow0.xyz,maskUv),dot(_MaskRow1.xyz,maskUv));
                float3 extraUv=float3(_Threshold>.5||_Family>5.5?v.uv2:(_Family>2.5&&_Family<3.5)?v.uv1:v.uv,1);
                o.extra=float2(dot(_ExtraRow0.xyz,extraUv),dot(_ExtraRow1.xyz,extraUv));if(_Family>6.5&&_Family<8.5)o.extra=v.uv1;o.color=(_VDisp>.5?v.controls:v.color)*_Color;
                o.normal=float4(UnityObjectToWorldNormal(v.normal),v.uv3.x);
                o.view=float4(UnityWorldSpaceViewDir(mul(unity_ObjectToWorld,v.vertex).xyz),v.uv3.y);
                o.alpha=v.color.a;o.rawUv=float4(v.uv,v.uv4);o.flowUv=v.uv5;o.controls=v.controls;o.screen=ComputeGrabScreenPos(o.vertex);return o;
            }
            float Address(float uv,float mode,float texel){
                if(mode>2.5)return clamp(uv,texel*.5,1-texel*.5);
                if(mode>1.5)return clamp(1-abs(frac(uv*.5)*2-1),texel*.5,1-texel*.5);
                return uv;
            }
            float2 Mapped(float2 uv,float4 row0,float4 row1,float u,float v,float4 texel){
                float3 p=float3(uv,1);return float2(Address(dot(row0.xyz,p),u,texel.x),Address(dot(row1.xyz,p),v,texel.y));
            }
            float ThresholdMask3(float2 uv){
                return tex2D(_Tex3,float2(Address(uv.x,_Tex3AddressU,_Tex3_TexelSize.x),Address(uv.y,_Tex3AddressV,_Tex3_TexelSize.y))).r;
            }
            float ThresholdMask4(float2 uv){
                return tex2D(_Tex4,float2(Address(uv.x,_Tex4AddressU,_Tex4_TexelSize.x),Address(uv.y,_Tex4AddressV,_Tex4_TexelSize.y))).r;
            }
            float4 ExtraLookup(float2 uv){
                return tex2D(_ExtraTex,float2(Address(uv.x,_ExtraAddressU,_ExtraTex_TexelSize.x),Address(uv.y,_ExtraAddressV,_ExtraTex_TexelSize.y)));
            }
            float4 frag(Output i):SV_Target {
                float2 uv=float2(Address(i.uv.x,_AddressModes.x,_MainTex_TexelSize.x),Address(i.uv.y,_AddressModes.y,_MainTex_TexelSize.y));
                float2 mask=float2(Address(i.mask.x,_AddressModes.z,_MaskTex_TexelSize.x),Address(i.mask.y,_AddressModes.w,_MaskTex_TexelSize.y));
                if(_SolidToon>.5){
                    // Native SolidToon: color, normal, toon ramp, specular/occlusion,
                    // sphere. Sampler zero is the surface color, not the ramp.
                    float3 n=normalize(i.normal.xyz);float3 view=normalize(i.view.xyz);
                    float3 dp1=ddx(-i.view.xyz),dp2=ddy(-i.view.xyz);float2 duv1=ddx(i.mask),duv2=ddy(i.mask);
                    float3 tangent=cross(dp2,n)*duv1.x+cross(n,dp1)*duv2.x;
                    float3 bitangent=cross(dp2,n)*duv1.y+cross(n,dp1)*duv2.y;
                    float invScale=rsqrt(max(max(dot(tangent,tangent),dot(bitangent,bitangent)),.000001));
                    float3 normal=tex2D(_MaskTex,mask).xyz*2-1;
                    n=normalize(normal.x*tangent*invScale+normal.y*bitangent*invScale+normal.z*n);
                    float4 surface=tex2D(_MainTex,uv)*_Color;
                    float2 specUv=Mapped(float2(i.normal.w,i.view.w),_Tex3Row0,_Tex3Row1,_Tex3AddressU,_Tex3AddressV,_Tex3_TexelSize);
                    float4 spec=tex2D(_Tex3,specUv);
                    float3 lightVector=_Param0.x==0?_Param1.xyz:_Param1.xyz-(_WorldSpaceCameraPos-i.view.xyz);
                    float3 lightDir=dot(lightVector,lightVector)<.00001?normalize(float3(.3,.8,.5)):normalize(lightVector);
                    float light=lerp(dot(n,lightDir)*.5+.5,spec.a,_Param0.z);
                    float3 toon=ExtraLookup(float2(_Param1.w,1-light)).rgb;
                    float luminance=dot(tex2D(_MainTex,uv).rgb,float3(.29891,.58661,.11448));
                    toon=lerp(toon,toon+luminance*(1-toon),_Param0.y);
                    float3 vn=mul((float3x3)UNITY_MATRIX_V,n);
                    float2 sphereUv=Mapped(vn.xy*float2(.5,-.5)+.5,_Tex4Row0,_Tex4Row1,_Tex4AddressU,_Tex4AddressV,_Tex4_TexelSize);
                    float4 sphere=tex2D(_Tex4,sphereUv);
                    float rim=saturate((1-dot(view,n)-(_Param2.w-.01))/.01);
                    float3 lit=surface.rgb*toon+sphere.rgb*spec.rgb+rim*_Param2.rgb;
                    float4 result=float4(lerp(lit,surface.rgb,_Param4.x)*_Gain,surface.a*sphere.a*i.alpha);
                    clip(result.a-.001);return result;
                }
                float3 n=normalize(i.normal.xyz),view=normalize(i.view.xyz);
                float2 sphere=mul((float3x3)UNITY_MATRIX_V,n).xy*float2(.5,-.5)+.5;
                if(_Family>1.5&&_Family<2.5){
                    float2 normalUv=Mapped(i.rawUv.xy,_Tex4Row0,_Tex4Row1,_Tex4AddressU,_Tex4AddressV,_Tex4_TexelSize);
                    float3 normal=tex2D(_Tex4,normalUv).xyz*2-1;
                    float4 c=tex2D(_MainTex,uv+normal.xy*_Param0.x)*i.color;
                    float4 reflection=tex2D(_MaskTex,sphere);
                    c.rgb=c.rgb*_Gain+reflection.rgb;c.a*=reflection.a;return c;
                }
                if(_Family>2.5&&_Family<3.5){
                    float2 rimUv=float2(_Param0.x,dot(view,n));
                    float4 rim=tex2D(_MaskTex,float2(Address(rimUv.x,_AddressModes.z,_MaskTex_TexelSize.x),Address(rimUv.y,_AddressModes.w,_MaskTex_TexelSize.y)));
                    float4 c=tex2D(_MainTex,uv)*i.color;
                    float2 extra=float2(Address(i.extra.x,_ExtraAddressU,_ExtraTex_TexelSize.x),Address(i.extra.y,_ExtraAddressV,_ExtraTex_TexelSize.y));
                    c.a*=tex2D(_ExtraTex,extra).r*tex2D(_Tex3,float2(Address(rimUv.x,_Tex3AddressU,_Tex3_TexelSize.x),Address(rimUv.y,_Tex3AddressV,_Tex3_TexelSize.y))).r;
                    c.rgb=c.rgb*_Gain*(1-rim.a*_Param0.y)+rim.rgb*rim.a*(1+_Param1.x);return c;
                }
                if(_Family>3.5&&_Family<4.5){
                    // Distortion samples the rendered scene; displaying its normal
                    // map as RGB produced opaque rainbow rectangles.
                    float4 control=tex2D(_MaskTex,mask);
                    if(_DistortionBlur<.5){
                        // Native Effect_Distortion: t0 is a signed direction map,
                        // t1.r controls displacement and t1.a controls opacity.
                        // Only t11 (the scene behind this surface) supplies RGB.
                        float2 direction=tex2D(_MainTex,uv).xy*2-1;
                        float3 axisX=normalize(mul((float3x3)UNITY_MATRIX_MV,float3(1,0,0)));
                        float3 axisY=normalize(mul((float3x3)UNITY_MATRIX_MV,float3(0,1,0)));
                        float2 offset=direction.x*axisX.xy+direction.y*axisY.xy;
                        #if UNITY_UV_STARTS_AT_TOP
                        offset.y=-offset.y;
                        #endif
                        float tanY=rcp(abs(UNITY_MATRIX_P[1][1]));
                        float tanX=rcp(abs(UNITY_MATRIX_P[0][0]));
                        float ratio=2.97*tanY/tanX+.03;
                        float projY=UNITY_MATRIX_VP[1][1];
                        // Reconstruct native (non-reversed) clip depth. Unity's
                        // D3D depth buffer uses the opposite near/far convention.
                        float clipDepth=max(0,_ProjectionParams.z*(i.screen.w-_ProjectionParams.y)/max(_ProjectionParams.z-_ProjectionParams.y,.0001));
                        offset*=control.r*lerp(.17*projY*projY,.0085,_Param1.w)*clipDepth*ratio*float2(tanY,tanX)*_Param0.x;
                        float2 screen=i.screen.xy/i.screen.w;
                        screen=lerp(screen,float2(.5,.5),saturate(_Param2.w));
                        return float4(tex2D(_HissatsuSceneColor,screen+offset).rgb*i.color.rgb*_Gain,control.a*i.color.a);
                    }
                    float2 offset=(tex2D(_MainTex,uv).xy*2-1)*control.g*.02;
                    float2 screen=i.screen.xy/i.screen.w;
                    screen=lerp(screen,float2(.5,.5),saturate(_Param2.w));
                    float3 c=0;float weight=0;
                    for(int s=0;s<6;s++){float w=6-s;c+=tex2D(_HissatsuSceneColor,screen+offset*(_Param0.x+s*_Param0.y)).rgb*w;weight+=w;}
                    c=pow(max(c/weight+_Param0.w,0),max(_Param1.x,.00001));
                    c=lerp(c,1-c,_Param1.y);c=lerp(dot(c,float3(.29891,.58661,.11448)),c,_Param1.z);
                    return float4(c*i.color.rgb*_Gain,control.a*i.color.a);
                }
                if(_Family>8.5){
                    // Native BallAura1: sampler zero is a shot-color palette,
                    // not a billboard image. Neutral is palette column four.
                    float facing=abs(dot(n,view))/max(saturate(_Param1.w+_Param1.z),.00001);
                    bool useAlpha=(_Param2.z>=2&&_Param2.z<3)||_Param2.z<1;
                    float a1=(useAlpha?1:i.alpha)*_Color.a;
                    float a2=(useAlpha?i.alpha:1)*facing;
                    float value=ExtraLookup(i.extra).r-(1-tex2D(_MaskTex,mask).r*_Param1.y*a2);
                    float opacity=saturate(value/max(1-_Param1.x,.005))*a1;
                    float g1=saturate((value-(1-_Param0.y))/max(1-_Param0.x,.005));
                    float g2=saturate((value-(1-_Param0.w))/max(1-_Param0.z,.005));
                    float4 palette=tex2D(_MainTex,float2((_BallAuraColumn+.5)/128,1+g2*(-.5-.5*g1)));
                    float4 result=float4(palette.rgb*i.color.rgb*_Gain,palette.a*opacity);
                    clip(result.a-.001);return result;
                }
                if(_Family>6.5){
                    // DifferenceFlow / DifferenceFlowM: t0 and t1 are scalar
                    // fields. Their flowed difference indexes the t2 palette;
                    // displaying t0 directly exposes a white mask as geometry.
                    float2 flowUv=Mapped(float2(i.normal.w,i.view.w),_Tex3Row0,_Tex3Row1,_Tex3AddressU,_Tex3AddressV,_Tex3_TexelSize);
                    float2 flow=(tex2D(_Tex3,flowUv).xy*2-1)*float2(1,-1);
                    float2 uvA=Mapped(i.rawUv.xy+flow*_Param0.w,_UvRow0,_UvRow1,_AddressModes.x,_AddressModes.y,_MainTex_TexelSize);
                    float2 uvB=Mapped(i.extra+flow*_Param1.x,_MaskRow0,_MaskRow1,_AddressModes.z,_AddressModes.w,_MaskTex_TexelSize);
                    float4 a=tex2D(_MainTex,uvA);float b=tex2D(_MaskTex,uvB).r;
                    float difference=a.r-b;difference+=_Param0.z*(abs(difference)-difference);
                    float range=saturate(_Param2.y+_Param2.z);
                    float facing=range>0?saturate(abs(dot(n,view))/range):1;
                    facing=max((facing-_Param2.z)/max(1-_Param2.z,.00001),0);
                    float control=facing*i.alpha;
                    float paletteU=_Param0.y+_Param0.x*_Param0.y*(control-1);
                    float4 c=ExtraLookup(float2(paletteU,1-difference));
                    c.a*=a.a;
                    if(_Family>7.5){
                        float2 maskUv=Mapped(i.rawUv.zw,_Tex4Row0,_Tex4Row1,_Tex4AddressU,_Tex4AddressV,_Tex4_TexelSize);
                        c.a*=tex2D(_Tex4,maskUv).r;
                    }
                    c.rgb*=i.color.rgb*_Gain;c.a*=lerp(control,1,_Param1.w)*_Color.a;
                    return c;
                }
                if(_Family>5.5){
                    // Smoke2: t0 stores normals/roughness, t1 stores the animated
                    // silhouette, t2 is the toon color/opacity lookup. None is
                    // a surface-color image to display directly on a rectangle.
                    float2 tiles=max(_Param5.xy,1);float frame=floor(_Param5.z);
                    float2 cell0=float2(fmod(frame,tiles.x),fmod(floor(frame/tiles.x),tiles.y));
                    float2 cell1=float2(fmod(frame+1,tiles.x),fmod(floor((frame+1)/tiles.x),tiles.y));
                    float4 silhouette=lerp(tex2D(_MaskTex,(mask+cell0)/tiles),tex2D(_MaskTex,(mask+cell1)/tiles),frac(_Param5.z));
                    float3 sv=silhouette.xyz*2-1;
                    float2 roughUv=Mapped(i.rawUv.xy+_Param0.y*float2(-sv.x,sv.y),_UvRow0,_UvRow1,_AddressModes.x,_AddressModes.y,_MainTex_TexelSize);
                    float4 rough=tex2D(_MainTex,roughUv);
                    float alpha=silhouette.a*i.color.a;
                    float2 opacityUv=float2(Address(alpha,_ExtraAddressU,_ExtraTex_TexelSize.x),Address(1-rough.a,_ExtraAddressV,_ExtraTex_TexelSize.y));
                    float opacity=tex2D(_ExtraTex,opacityUv).a;
                    float3 dp1=ddx(i.view.xyz),dp2=ddy(i.view.xyz);
                    float2 dt1=ddx(i.rawUv.xy),dt2=ddy(i.rawUv.xy);
                    float3 tangent=normalize(cross(dp2,n)*dt1.x+cross(n,dp1)*dt2.x);
                    float3 bitangent=normalize(cross(n,tangent));
                    float2 detail=rough.xy*2+sv.xy-1;
                    float3 normal=normalize(tangent*detail.x+bitangent*detail.y+n*sv.z);
                    float3 light=_Param4.x==0?_Param2.xyz:_Param2.xyz-(_WorldSpaceCameraPos-i.view.xyz);
                    light=dot(light,light)>.00001?normalize(light):float3(0,1,0);
                    float lit=(dot(normal,light)*.5+.5)*lerp(1,silhouette.a*rough.a,_Param0.w);
                    lit=lerp(lit,rough.a*silhouette.a,_Param0.z);
                    float2 toonUv=float2(Address(_Param2.w,_ExtraAddressU,_ExtraTex_TexelSize.x),Address(1-lit,_ExtraAddressV,_ExtraTex_TexelSize.y));
                    float3 color=tex2D(_ExtraTex,toonUv).rgb*i.color.rgb*_Gain;
                    clip(opacity-.001);return float4(color,opacity);
                }
                if(_Family>4.5){
                    // Effect_T3Threshold resolves through effect_test_threshold.fxbin
                    // to Effect_TestThreshold, not Effect_T3ThresholdF. Its two scalar
                    // masks use UV3/UV4 and three offset samples; COLOR1 and flow are
                    // exclusive to the six-texture F variant.
                    if(_Flow<.5){
                        float3 p3=float3(i.normal.w,i.view.w,1);
                        float3 p4=float3(i.rawUv.zw,1);
                        float2 m3=float2(dot(_Tex3Row0.xyz,p3),dot(_Tex3Row1.xyz,p3));
                        float2 m4=float2(dot(_Tex4Row0.xyz,p4),dot(_Tex4Row1.xyz,p4));
                        float3 a=float3(ThresholdMask3(m3+_Param0.zw),ThresholdMask3(m3+_Param1.zw),ThresholdMask3(m3));
                        float3 b=float3(ThresholdMask4(m4+_Param0.zw),ThresholdMask4(m4+_Param1.zw),ThresholdMask4(m4));
                        float strength=lerp(1,i.alpha,_Param2.z)*_Param2.y;
                        float3 boundary=float3(2-_Param0.y,2-_Param1.y,1)-a*strength;
                        float3 weights=saturate((b-boundary)/clamp(1-float3(_Param0.x,_Param1.x,_Param2.x),.01,1));
                        float4 c=lerp(tex2D(_MaskTex,uv),tex2D(_MainTex,uv),weights.x);
                        c=lerp(tex2D(_ExtraTex,uv),c,weights.y);
                        c.rgb*=i.color.rgb*_Gain;
                        c.a*=weights.z*lerp(i.alpha,1,_Param2.z)*_Color.a;
                        return c;
                    }
                    // Six-texture ThresholdF: COLOR1 is control data, not a tint.
                    float2 flowUv=Mapped(i.flowUv,_Tex5Row0,_Tex5Row1,_Tex5AddressU,_Tex5AddressV,_Tex5_TexelSize);
                    float2 flow=(tex2D(_Tex5,flowUv).xy*2-1)*float2(1,-1)*_Flow;
                    float2 noiseUv=Mapped(float2(i.normal.w,i.view.w),_Tex3Row0,_Tex3Row1,_Tex3AddressU,_Tex3AddressV,_Tex3_TexelSize)+flow*i.controls.a*_Param5.x;
                    float4 noise=tex2D(_Tex3,noiseUv);
                    float2 direction=(noise.xy*2-1)*float2(1,-1);
                    float2 colorUv=Mapped(i.rawUv.xy+direction*_Param4.y+flow*i.controls.a*_Param4.w,_UvRow0,_UvRow1,_AddressModes.x,_AddressModes.y,_MainTex_TexelSize);
                    float2 coverageUv=Mapped(i.rawUv.zw+direction*_Param4.z+flow*i.controls.a*_Param5.y,_Tex4Row0,_Tex4Row1,_Tex4AddressU,_Tex4AddressV,_Tex4_TexelSize);
                    float coverage=tex2D(_Tex4,coverageUv).r;
                    float range=saturate(_Param5.w+_Param6.x);
                    float facing=range>0?saturate(abs(dot(n,view))/range):1;
                    facing=max((facing-_Param6.x)/max(1-_Param6.x,.00001),0);
                    float strength=noise.a*facing*lerp(1,i.alpha,_Param2.z)*_Param2.y;
                    float3 boundary=float3(2-_Param0.y,2-_Param1.y,1)-strength*i.controls.rgb;
                    float3 weights=saturate((coverage-boundary)/clamp(1-float3(_Param0.x,_Param1.x,_Param2.x),.01,1));
                    float4 c=lerp(tex2D(_MaskTex,colorUv),tex2D(_MainTex,colorUv),weights.x);
                    c=lerp(tex2D(_ExtraTex,colorUv),c,weights.y);
                    c.rgb*=i.color.rgb*_Gain;c.a*=weights.z*lerp(i.alpha,1,_Param2.z)*_Color.a;return c;
                }
                if(_Pattern>.5){
                    float2 tiles=max(floor(_Param0.yz+.5),1);float frame=max(floor(_Param0.x),0);
                    frame=fmod(frame,tiles.x*tiles.y);
                    float2 inset=_MainTex_TexelSize.xy*tiles*.5;
                    uv=(clamp(uv,inset,1-inset)+float2(fmod(frame,tiles.x),floor(frame/tiles.x)))/tiles;
                }
                if(_Threshold>.5){
                    float2 extra=float2(Address(i.extra.x,_ExtraAddressU,_ExtraTex_TexelSize.x),Address(i.extra.y,_ExtraAddressV,_ExtraTex_TexelSize.y));
                    float alphaControls=(_Param2.z<1||(_Param2.z>=2&&_Param2.z<3))?1:0;
                    float thresholdAlpha=lerp(1,i.alpha,alphaControls),opacityAlpha=lerp(i.alpha,1,alphaControls);
                    float range=saturate(_Param1.z+_Param1.w);
                    float facing=range>0?saturate(abs(dot(n,view))/range):1;
                    facing=max((facing-_Param1.w)/max(1-_Param1.w,.00001),0);
                    float amount=tex2D(_MaskTex,mask).r*_Param1.y*facing*thresholdAlpha+tex2D(_ExtraTex,extra).r-1;
                    float first=saturate((amount-(1-_Param0.y))/max(1-_Param0.x,.005));
                    float second=saturate((amount-(1-_Param0.w))/max(1-_Param0.z,.005));
                    float2 gradient=float2(_Param2.w,1-second*(first+1)*.5);
                    gradient=float2(Address(gradient.x,_AddressModes.x,_MainTex_TexelSize.x),Address(gradient.y,_AddressModes.y,_MainTex_TexelSize.y));
                    float4 c=tex2D(_MainTex,gradient);c.rgb*=i.color.rgb*_Gain;
                    c.a*=saturate(amount/max(1-_Param1.x,.005))*opacityAlpha*_Color.a;return c;
                }
                float4 c=tex2D(_MainTex,uv)*i.color;c.rgb*=_Gain;c.a*=lerp(1,tex2D(_MaskTex,mask).r,_UseMask);return c;
            }
            ENDCG
        }
    }
}
