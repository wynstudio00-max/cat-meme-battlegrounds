// Battle-royale storm wall: an additive energy curtain on the zone cylinder (ZoneController). World-space pattern (no UVs
// needed, tiles evenly at any radius): rising diagonal energy lines, a hex-like grid, scan bands sweeping upward, a bright
// band where it meets the ground, fading out towards the top and close to the camera (so it never blinds the player who
// stands at it). HDR colour so bloom makes it glow.
Shader "CatBattleground/ZoneWall"
{
    Properties
    {
        [HDR] _Color("Color", Color) = (0.55, 0.35, 1.6, 1)
        [HDR] _EdgeColor("Ground Edge Color", Color) = (1.6, 1.1, 3.0, 1)
        _Height("Fade Height (m)", Float) = 45
        _Opacity("Opacity", Range(0, 2)) = 1
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" "IgnoreProjector" = "True" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            Name "ZoneWall"
            Tags { "LightMode" = "UniversalForward" }

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _Color;
                half4 _EdgeColor;
                float _Height;
                half _Opacity;
            CBUFFER_END

            struct Attributes { float4 positionOS : POSITION; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 centerWS : TEXCOORD1;
            };

            Varyings vert(Attributes input)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(input.positionOS.xyz);
                o.centerWS = TransformObjectToWorld(float3(0, 0, 0));
                o.positionCS = TransformWorldToHClip(o.positionWS);
                return o;
            }

            float lineMask(float x, float width) { float f = abs(frac(x) - 0.5); return saturate(1.0 - f / width); }

            half4 frag(Varyings i) : SV_Target
            {
                float2 d = i.positionWS.xz - i.centerWS.xz;
                float radius = length(d);
                float arc = atan2(d.y, d.x) * radius;            // metres along the wall
                float y = i.positionWS.y;
                float t = _Time.y;

                // diagonal energy lines climbing the wall
                float diag = lineMask((arc + y) * 0.12 - t * 0.35, 0.06) + lineMask((arc - y) * 0.12 + t * 0.2, 0.04) * 0.6;
                // hex-ish grid
                float grid = max(lineMask(arc * 0.25, 0.03), lineMask(y * 0.25 + 0.5 * step(0.5, frac(arc * 0.125)), 0.03)) * 0.35;
                // scan bands sweeping up
                float scan = pow(saturate(1.0 - abs(frac(y * 0.02 - t * 0.12) - 0.5) * 6.0), 3.0) * 0.8;
                // soft noise shimmer
                float shimmer = 0.5 + 0.5 * sin(arc * 0.7 + t * 2.3) * sin(y * 0.9 - t * 1.7);

                float heightFade = saturate(1.0 - y / _Height);
                float ground = exp(-max(y, 0.0) * 0.45);       // bright where it meets the ground
                float baseA = 0.13 + shimmer * 0.06;

                float camDist = distance(_WorldSpaceCameraPos, i.positionWS);
                float nearFade = saturate((camDist - 1.5) / 7.0);

                half3 col = _Color.rgb * (baseA + diag * 0.55 + grid + scan) + _EdgeColor.rgb * ground * 0.9;
                half a = saturate((baseA + diag * 0.5 + grid * 0.8 + scan * 0.6) * heightFade + ground * 0.8) * nearFade * _Opacity;
                return half4(col, a);
            }
            ENDHLSL
        }
    }
}
