// Converts a Lottie texture (premultiplied, sRGB-encoded) into straight alpha for consumers that expect it
// (UI Toolkit). Fully transparent texels take the color of their most opaque neighbour, so bilinear filtering
// does not darken the edges.
Shader "Hidden/Mynell/Lottie/Straight"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }

    SubShader
    {
        Cull Off
        ZWrite Off
        ZTest Always
        Blend Off

        Pass
        {
        CGPROGRAM
            #pragma vertex vert_img
            #pragma fragment frag
            #pragma target 2.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;

            half3 SrgbToLinear(half3 c)
            {
                return c <= 0.04045 ? c / 12.92 : pow((c + 0.055) / 1.055, 2.4);
            }

            half4 frag(v2f_img i) : SV_Target
            {
                half4 t = tex2D(_MainTex, i.uv);
                half alpha = t.a;
                if (alpha <= 0)
                {
                    float2 d = _MainTex_TexelSize.xy;
                    half4 n = tex2D(_MainTex, i.uv + float2(d.x, 0));
                    half4 s = tex2D(_MainTex, i.uv - float2(d.x, 0));
                    if (s.a > n.a) n = s;
                    s = tex2D(_MainTex, i.uv + float2(0, d.y));
                    if (s.a > n.a) n = s;
                    s = tex2D(_MainTex, i.uv - float2(0, d.y));
                    if (s.a > n.a) n = s;
                    t = n;
                }
                half3 c = t.a > 0 ? saturate(t.rgb / t.a) : half3(0, 0, 0);
                #if !defined(UNITY_COLORSPACE_GAMMA)
                c = SrgbToLinear(c);
                #endif
                return half4(c, alpha);
            }
        ENDCG
        }
    }
}
