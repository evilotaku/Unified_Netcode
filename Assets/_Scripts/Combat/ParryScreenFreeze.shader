// Stylized freeze frame for a parry, drawn by ParryScreenFreeze over a captured frame: a white flash, a zoom punch,
// a high-contrast duotone, an expanding shock ring with chromatic split, and flickering radial speed lines.
Shader "Hidden/Combat/ParryScreenFreeze"
{
    Properties
    {
        [PerRendererData] _MainTex ("Frozen Frame", 2D) = "white" {}
        _DarkColor ("Dark", Color) = (0.06, 0.02, 0.12, 1)
        _LightColor ("Light", Color) = (1, 0.97, 0.9, 1)
        _AccentColor ("Accent", Color) = (1, 0.3, 0.12, 1)
        _Stylize ("Duotone Amount", Range(0, 1)) = 0.85
        _LineCount ("Speed Line Count", Float) = 110
        _LineDensity ("Speed Line Density", Range(0, 1)) = 0.3
        _RingSpeed ("Shock Ring Speed", Float) = 2.2
    }
    SubShader
    {
        Tags { "Queue" = "Overlay" "RenderType" = "Transparent" "IgnoreProjector" = "True" "PreviewType" = "Plane" }
        Cull Off ZWrite Off ZTest Always
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata { float4 vertex : POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };
            struct v2f { float4 pos : SV_POSITION; float2 uv : TEXCOORD0; float4 color : COLOR; };

            sampler2D _MainTex;
            float4 _DarkColor, _LightColor, _AccentColor;
            float _Stylize, _LineCount, _LineDensity, _RingSpeed;
            // Set by ParryScreenFreeze each frame.
            float4 _Center;   // xy: the parry's position in screen UV
            float _Elapsed;   // seconds since the freeze began
            float _Strength;  // 1 while frozen, falling to 0 as it fades out
            float _Aspect;    // screen width / height
            float _FlipY;     // 1 where the captured frame is upside down

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.uv;
                o.color = v.color;
                return o;
            }

            float hash(float n) { return frac(sin(n) * 43758.5453); }

            float3 frame(float2 uv)
            {
                uv = saturate(uv);
                if (_FlipY > 0.5) uv.y = 1 - uv.y;
                return tex2D(_MainTex, uv).rgb;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 aspect = float2(_Aspect, 1);
                float2 p = (i.uv - _Center.xy) * aspect;
                float d = length(p);
                float2 dir = d > 1e-4 ? p / d : float2(0, 0);
                float t = _Elapsed;

                // Shock ring expanding from the parry, bending the image as it passes.
                float ring = exp(-pow((d - t * _RingSpeed) * 16, 2)) * saturate(1 - t * 1.2);
                float punch = 0.04 * exp(-t * 10);
                float2 uv = i.uv - (i.uv - _Center.xy) * punch - dir / aspect * ring * 0.035;

                // Chromatic split, strongest on the ring.
                float2 split = dir / aspect * (0.003 + ring * 0.012);
                float3 col = float3(frame(uv + split).r, frame(uv).g, frame(uv - split).b);

                // High-contrast duotone.
                float lum = dot(col, float3(0.299, 0.587, 0.114));
                float3 duo = lerp(_DarkColor.rgb, _LightColor.rgb, smoothstep(0.22, 0.55, lum));
                col = lerp(col, duo, _Stylize);

                // Radial speed lines, re-rolled a few times a second so they flicker like an anime freeze.
                float angle = atan2(p.y, p.x) / 6.2831853 + 0.5;
                float sector = floor(angle * _LineCount);
                float step24 = floor(t * 18);
                float lineOn = step(1 - _LineDensity, hash(sector * 1.37 + step24 * 17.1));
                float across = frac(angle * _LineCount);
                float taper = smoothstep(0, 0.35, across) * smoothstep(1, 0.65, across);
                float inner = 0.22 + 0.2 * hash(sector + step24 * 3.3);
                float lines = lineOn * taper * smoothstep(inner, inner + 0.3, d);
                col = lerp(col, _LightColor.rgb, lines * 0.9);

                // Ring and burst in the accent color, then a dark vignette.
                col = lerp(col, _AccentColor.rgb, saturate(ring * 1.3));
                col += _AccentColor.rgb * exp(-d * 8) * exp(-t * 5) * 1.5;
                col = lerp(col, _DarkColor.rgb, saturate((d - 0.55) * 1.4) * 0.85);

                // White flash on the first frames.
                col = lerp(col, float3(1, 1, 1), exp(-t * 22));

                return fixed4(col, _Strength * i.color.a);
            }
            ENDCG
        }
    }
}
