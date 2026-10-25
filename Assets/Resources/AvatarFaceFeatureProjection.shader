Shader "Hidden/G4/AvatarFaceFeatureProjection"
{
 Properties { _MainTex("Feature",2D)="white"{} }
 SubShader { Cull Off ZWrite Off ZTest Always Pass {
 CGPROGRAM
 #pragma vertex vert
 #pragma fragment frag
 #include "UnityCG.cginc"
 sampler2D _MainTex;
 float2 _CellOffset;
 struct Output { float4 position:SV_POSITION; float2 uv:TEXCOORD0; };
 Output vert(appdata_base i) {
  Output o;
  // Render into atlas UV space independently of the active scene camera.
  o.position=float4((i.vertex.xy + _CellOffset) * 2 - 1, 0, 1);
  #if UNITY_UV_STARTS_AT_TOP
  o.position.y=-o.position.y;
  #endif
  o.uv=i.texcoord.xy;
  return o;
 }
 fixed4 frag(Output i):SV_Target { return tex2D(_MainTex,i.uv); }
 ENDCG
 }}
}