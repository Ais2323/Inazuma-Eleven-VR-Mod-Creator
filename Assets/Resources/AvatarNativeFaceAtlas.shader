Shader "Hidden/G4/AvatarNativeFaceAtlas"
{
 Properties { _MainTex("Expressions",2D)="black"{} _FeatureTex("Feature foundation",2D)="white"{} }
 SubShader { Cull Off ZWrite Off ZTest Always Pass {
 CGPROGRAM
 #pragma vertex vert_img
 #pragma fragment frag
 #include "UnityCG.cginc"
 sampler2D _MainTex, _FeatureTex;
 float4 _SkinColor;
 fixed4 frag(v2f_img i):SV_Target {
  fixed4 feature = tex2D(_FeatureTex, i.uv);
  fixed4 expression = tex2D(_MainTex, i.uv);
  fixed3 rgb = lerp(feature.rgb, expression.rgb, expression.a);
  // A small plain edge is shared by shell and ear UVs, as in native faces.
  if(i.uv.x < 4.0/2048.0) rgb = _SkinColor.rgb;
  return fixed4(rgb,1);
 }
 ENDCG
 }}
}