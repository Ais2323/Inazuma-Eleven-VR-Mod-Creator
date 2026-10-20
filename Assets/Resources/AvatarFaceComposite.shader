Shader "Hidden/AvatarFaceComposite"
{
    Properties {
        _MainTex("Base", 2D)="black"{}
        _LayerTex("Layer",2D)="black"{}
        _LayerMask("Region map",2D)="black"{}
        _EyeMask("Eye opening",2D)="black"{}
    }
    SubShader { Cull Off ZWrite Off ZTest Always Pass {
        CGPROGRAM
        #pragma vertex vert_img
        #pragma fragment frag
        #include "UnityCG.cginc"
        sampler2D _MainTex, _LayerTex, _LayerMask, _EyeMask;
        float4 _LayerPlacement, _EyePlacement, _AtlasGrid, _SourceGrid;
        float4 _LayerRotation, _EyeRotation;
        float4 _ExpressionOffset;
        float4 _SkinColor, _PrimaryColor, _SecondaryColor;
        float _HasLayerMask, _HasEyeMask, _LayerKind;
        float Inside(float2 p) { return step(0, p.x) * step(p.x, 1) * step(0, p.y) * step(p.y, 1); }
        fixed4 frag(v2f_img i) : SV_Target {
            float2 cell = floor(i.uv * _AtlasGrid.xy);
            float2 sourceCell = fmod(cell + _ExpressionOffset.xy, _AtlasGrid.xy);
            float2 local = frac(i.uv * _AtlasGrid.xy);
            float2 delta = local - _LayerPlacement.xy;
            float2 rotated = float2(dot(_LayerRotation.xy, delta), dot(_LayerRotation.zw, delta));
            float2 p = rotated / max(.001, _LayerPlacement.zw) + .5;
            fixed4 under = tex2D(_MainTex, i.uv);
            float2 layerUV = (_SourceGrid.x > 1) ? (sourceCell + p) / _SourceGrid.xy : p;
            fixed4 over = tex2D(_LayerTex, layerUV);
            fixed4 mask = tex2D(_LayerMask, layerUV);
            // The opaque RGB mask identifies skin and recolorable regions.
            // Source alpha remains coverage; mask alpha is never coverage.
            float green = saturate(mask.g - max(mask.r, mask.b));
            float blue = saturate(mask.b - max(mask.r, mask.g));
            float background = saturate(mask.r - max(mask.g, mask.b));
            float coverage = max(over.a * (1 - background), green + blue);
            float alpha = lerp(over.a, coverage, _HasLayerMask) * Inside(p);
            float3 rgb = over.rgb;
            rgb = lerp(rgb, over.rgb * _PrimaryColor.rgb, green * _HasLayerMask);
            rgb = lerp(rgb, over.rgb * _SecondaryColor.rgb, blue * _HasLayerMask);
            if (_LayerKind < .5) {
                // 00_face is an opaque painted foundation. Red is skin tint,
                // green/blue use Feature 1's color; source RGB retains scars.
                float3 weights = saturate(mask.rgb);
                float weightSum = max(.001, weights.r + weights.g + weights.b);
                float3 tint = (_SkinColor.rgb * weights.r + _PrimaryColor.rgb * weights.g + _SecondaryColor.rgb * weights.b) / weightSum;
                rgb = over.rgb * lerp(_SkinColor.rgb, tint, _HasLayerMask);
                alpha = over.a * Inside(p);
            }
            if (_LayerKind > 1.5 && _LayerKind < 3.5 && _HasEyeMask > .5) {
                float2 eyeDelta = local - _EyePlacement.xy;
                float2 eyeRotated = float2(dot(_EyeRotation.xy, eyeDelta), dot(_EyeRotation.zw, eyeDelta));
                float2 eyeP = eyeRotated / max(.001, _EyePlacement.zw) + .5;
                fixed3 eye = tex2D(_EyeMask, (sourceCell + eyeP) / _AtlasGrid.xy).rgb;
                alpha *= saturate(1 - max(eye.r, max(eye.g, eye.b))) * Inside(eyeP);
            }
            return fixed4(lerp(under.rgb, rgb, alpha), alpha + under.a * (1 - alpha));
        }
        ENDCG
    }}
}