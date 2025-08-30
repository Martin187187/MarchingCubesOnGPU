Shader "Custom/SimpleGrassQuads_GS_Lit_AllLights_Optimized_ColorJitter_Wind_LOD_Array"
{
    Properties
    {
        _TexArray   ("Grass Texture Array (alpha)", 2DArray) = "" {}
        _ArrayCount ("# Layers Used", Range(1,16)) = 4

        _Tint       ("Tint", Color) = (1,1,1,1)
        _Cutoff     ("Alpha Cutoff", Range(0,1)) = 0.4

        _Width      ("Bottom Width",  Range(0.01,1.0)) = 0.12
        _TopWidth   ("Top Width",     Range(0,1.0))    = 0.04
        _Height     ("Quad Height",   Range(0.05,2.0)) = 0.6
        _Lift       ("Lift above surface", Range(0,0.2)) = 0.02
        
        _BypassFilter ("Bypass spawn filter", Float) = 1
        _UV2XMin    ("Min texcoord2.x to spawn", Range(0,4)) = 0.5
        _MaxSlopeDeg ("Max ground slope (deg)", Range(0,90)) = 55
        _Density    ("Quads per eligible vertex", Range(0,12)) = 1.4
        _MaxQuadsPerVertex ("Runtime max (<= hard cap)", Range(1,12)) = 8

        _JitterRadius ("Pos jitter radius (m)", Range(0,0.8)) = 0.06
        _YawJitterDeg ("Yaw jitter (deg)", Range(0,180)) = 25
        _SizeJitter   ("Size jitter (±%)", Range(0,1)) = 0.25

        _TiltDeg     ("Lean (deg)", Range(0,35)) = 8
        _Bend        ("Bend amount", Range(0,0.6)) = 0.15

        _ShadowDim    ("Shadow dimming", Range(0,1)) = 1.0
        _AmbientBoost ("Ambient boost", Range(0,1)) = 0.15

        _Darkening    ("Bottom Darkening (0..1)", Range(0,1)) = 0.4

        _ColorJitter ("Color jitter (±%)", Range(0,1)) = 0.2

        // --- Wind ---
        _WindTex      ("Wind Noise (R)", 2D) = "gray" {}
        _WindTiling   ("Wind Tiling (x,y)", Vector) = (0.08, 0.08, 0, 0)
        _WindSpeed    ("Wind Speed", Range(0,0.1)) = 0.05
        _WindStrength ("Wind Strength", Range(0,1)) = 0.35
        _WindDirDeg   ("Wind Direction (deg)", Range(0,360)) = 45

        // --- Distance-based thinning ---
        _LODStart     ("LOD start distance (m)", Range(0,100)) = 15
        _LODEnd       ("LOD end distance (m)",   Range(1,400)) = 80
        _MinDensity   ("Min density factor @far", Range(0,1)) = 0.15
    }

    SubShader
    {
        Tags { "RenderType"="AlphaTest" "Queue"="AlphaTest" }
        Cull Off
        ZWrite On
        Blend Off

        // ==================== ForwardBase ====================
        Pass
        {
            Name "ForwardBase"
            Tags { "LightMode"="ForwardBase" }

            CGPROGRAM
            #define MAX_QUADS 8
            #pragma target 4.0
            #pragma vertex   vert
            #pragma geometry geom
            #pragma fragment fragBase

            #pragma multi_compile_fwdbase
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            // Texture Array
            UNITY_DECLARE_TEX2DARRAY(_TexArray);
            half _ArrayCount;

            fixed4 _Tint;
            half _Cutoff;

            half _Width, _TopWidth, _Height, _Lift;
            half _UV2XMin, _BypassFilter, _MaxSlopeDeg;

            half _Density;
            half _MaxQuadsPerVertex;
            half _JitterRadius;
            half _YawJitterDeg;
            half _SizeJitter;

            half _TiltDeg;
            half _Bend;

            half _ShadowDim;
            half _AmbientBoost;
            half _Darkening;

            half _ColorJitter;

            // --- Wind uniforms ---
            sampler2D _WindTex;
            float4 _WindTiling;   // xy used
            half   _WindSpeed;
            half   _WindStrength;
            half   _WindDirDeg;

            // --- LOD uniforms ---
            half _LODStart;
            half _LODEnd;
            half _MinDensity;

            UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata {
                float4 vertex    : POSITION;
                float3 normal    : NORMAL;
                float4 color     : COLOR;
                float4 texcoord2 : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2g {
                float3 posWS  : TEXCOORD0;
                float3 nWS    : TEXCOORD1;
                half   uv2x   : TEXCOORD2;
                half   weight : TEXCOORD3;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct g2f {
                float4 pos     : SV_POSITION;
                half2  uv      : TEXCOORD0; // v=0 bottom, v=1 top
                half3  nWS     : TEXCOORD1;
                float3 posWS   : TEXCOORD2; // keep for shadows/light (safe)
                SHADOW_COORDS(3)
                UNITY_FOG_COORDS(4)
                half3  cMul    : TEXCOORD5; // per-quad color multiplier
                half   layer   : TEXCOORD6; // texture array layer index (as half)
            };

            // ---------- Helpers ----------
            uint HashUint(uint x) {
                x ^= 2747636419u; x *= 2654435769u;
                x ^= x >> 16;     x *= 2654435769u;
                x ^= x >> 16;     x *= 2654435769u;
                return x ^ (x >> 16);
            }
            float Hash01(uint x) {
                return (HashUint(x) & 0x00FFFFFFu) * (1.0/16777216.0);
            }
            uint SeedFromFloat3(float3 p) {
                uint3 u = asuint(p * 131.0);
                uint s = 0u;
                s ^= HashUint(u.x + 0x9E3779B9u);
                s ^= HashUint(u.y + 0x85EBCA77u);
                s ^= HashUint(u.z + 0xC2B2AE3Du);
                return s;
            }
            void BuildBasis(float3 n, out float3 t, out float3 b)
            {
                float3 h = abs(n.y) < 0.999 ? float3(0,1,0) : float3(1,0,0);
                t = normalize(cross(h, n));
                b = cross(n, t);
            }
            float3 TangentWindDir(float3 t, float3 b, half windDirDeg)
            {
                const float DEG2RAD = 0.017453292519943295f;
                float r = windDirDeg * DEG2RAD;
                float s, c; sincos(r, s, c);
                float2 wXZ = float2(c, s);
                return normalize(t * wXZ.x + b * wXZ.y);
            }

            v2g vert(appdata v)
            {
                v2g o;
                UNITY_SETUP_INSTANCE_ID(v);
                o.posWS  = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.nWS    = UnityObjectToWorldNormal(v.normal);
                o.uv2x   = (half)v.texcoord2.x;
                o.weight = (half)v.color.g;
                return o;
            }

            // Emit with optional bend (curvature) along a given tangent-plane direction + wind
            void EmitQuadBent(float3 basePos, float3 n, float3 side, float3 bendDir, half bendAmt,
                              half bottomHalf, half topHalf, half height, half3 cMul,
                              float3 windVec, half windAmt, half layer,
                              inout TriangleStream<g2f> triStream)
            {
                float3 topPos = basePos + n * height
                              + bendDir * (bendAmt * height)
                              + windVec * (windAmt * height);

                float3 A = basePos - side * bottomHalf;
                float3 D = basePos + side * bottomHalf;

                float3 B = topPos  - side * topHalf;
                float3 C = topPos  + side * topHalf;

                g2f o;

                // tri 1
                o.posWS = A;  o.nWS = (half3)n; o.uv=half2(0,0); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);

                o.posWS = D;  o.nWS = (half3)n; o.uv=half2(1,0); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);

                o.posWS = C;  o.nWS = (half3)n; o.uv=half2(1,1); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                triStream.RestartStrip();

                // tri 2
                o.posWS = C;  o.nWS = (half3)n; o.uv=half2(1,1); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);

                o.posWS = B;  o.nWS = (half3)n; o.uv=half2(0,1); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);

                o.posWS = A;  o.nWS = (half3)n; o.uv=half2(0,0); o.cMul = cMul; o.layer = layer;
                o.pos   = UnityWorldToClipPos(o.posWS);
                TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                triStream.RestartStrip();
            }

            [maxvertexcount(6*MAX_QUADS)]
            void geom(point v2g IN[1], inout TriangleStream<g2f> triStream)
            {
                // -------- Early cull (cheapest first) --------
                if (_BypassFilter < 0.5h)
                    if (IN[0].uv2x != _UV2XMin ) return;

                float3 n0 = normalize(IN[0].nWS);
                float minUpDot = cos(radians(_MaxSlopeDeg));
                if (n0.y < minUpDot) return;

                float3 t0, b0; BuildBasis(n0, t0, b0);
                float3 basePos = IN[0].posWS + n0 * _Lift;

                // -------- Distance-based density scaling --------
                float dist = distance(basePos, _WorldSpaceCameraPos);
                float span = max(_LODEnd - _LODStart, 1e-3);
                float t = saturate((dist - _LODStart) / span);
                half densityFactor = (half)lerp(1.0, _MinDensity, t);

                // Density (apply factor)
                half  D  = max(_Density * densityFactor, 0.0h);
                half  Di = floor(D);
                half  Df = frac(D);
                uint seedPoint = SeedFromFloat3(basePos * 0.123 + n0 * 0.371);
                float rndPoint = Hash01(seedPoint);
                int   k        = (int)Di + ((rndPoint < Df) ? 1 : 0);

                int kmax = (int)clamp(_MaxQuadsPerVertex, 1.0h, (half)MAX_QUADS);
                k = min(k, kmax);
                if (k <= 0) return;

                // Hoisted constants
                const float yawScale  = radians(_YawJitterDeg);
                const half  sizeJ     = _SizeJitter;
                const half  bendBase  = _Bend;
                const half  jitterR   = _JitterRadius;
                const half  width0    = _Width     * 0.5h;
                const half  topW0     = _TopWidth  * 0.5h;
                const half  height0   = _Height;
                const float tiltRadK  = radians(_TiltDeg);

                // World wind 2D direction (for UV scroll)
                const float DEG2RAD = 0.017453292519943295f;
                float2 worldWind2D = float2(cos(_WindDirDeg * DEG2RAD), sin(_WindDirDeg * DEG2RAD));

                [loop] for (int q = 0; q < k; q++)
                {
                    // Per-quad RNG
                    uint s = seedPoint ^ (uint)q * 747796405u;
                    float r1 = Hash01(s += 0x9E3779B9u);
                    float r2 = Hash01(s += 0x9E3779B9u);
                    float r3 = Hash01(s += 0x9E3779B9u);
                    float r4 = Hash01(s += 0x9E3779B9u);
                    float r5 = Hash01(s += 0x9E3779B9u);
                    float r6 = Hash01(s += 0x9E3779B9u);
                    float r7 = Hash01(s += 0x9E3779B9u);
                    float r8 = Hash01(s += 0x9E3779B9u);
                    float r9 = Hash01(s += 0x9E3779B9u); // for layer choice

                    // ---------- JITTER in tangent plane (no trig) ----------
                    float2 v = float2(r1 * 2.0 - 1.0, r2 * 2.0 - 1.0);
                    float  invLen = rsqrt(max(dot(v,v), 1e-6));
                    v *= invLen;
                    float  rad = sqrt(r2) * jitterR;
                    float3 jitter = (t0 * v.x + b0 * v.y) * rad;

                    // ---------- Lean / tilt ----------
                    float3 leanDir = normalize(t0 * v.y + b0 * (-v.x));
                    float  tiltSigned = tiltRadK * (r3 * 2.0 - 1.0);
                    float3 nLeaned = normalize(n0 + leanDir * tiltSigned);

                    // Rebuild basis around leaned normal
                    float3 t1, b1; BuildBasis(nLeaned, t1, b1);

                    // ---------- Yaw variation (single sincos) ----------
                    float yaw = (r3 * 2.0 - 1.0) * yawScale;
                    float sYaw, cYaw; sincos(yaw, sYaw, cYaw);
                    float3 side = (t1 * cYaw + b1 * sYaw);

                    // ---------- Size jitter ----------
                    half sizeMul    = (half)(1.0 + (r4 * 2.0 - 1.0) * sizeJ);
                    half bottomHalf = width0  * sizeMul;
                    half topHalf    = topW0   * sizeMul;
                    half heightJ    = height0 * sizeMul;

                    // ---------- Bend ----------
                    float3 bendDir = normalize(cross(nLeaned, side));
                    half   bendAmt = bendBase * (half)(0.6 + 0.4 * r5);

                    // ---------- Color jitter ----------
                    half3 cMul = (half3)1.0h + (half3)((float3(r6, r7, r8) * 2.0 - 1.0) * _ColorJitter);

                    // ---------- WIND (2-octave noise in world XZ) ----------
                    float2 windUVBase = IN[0].posWS.xz * _WindTiling.xy;
                    float windPhase = _Time.y * _WindSpeed;

                    float2 uv1 = windUVBase + worldWind2D * windPhase;
                    float w1 = tex2Dlod(_WindTex, float4(uv1, 0, 0)).r * 2.0 - 1.0;

                    float2 uv2 = windUVBase * 2.31 + 17.0 + worldWind2D * (windPhase * 1.7);
                    float w2 = tex2Dlod(_WindTex, float4(uv2, 0, 0)).r * 2.0 - 1.0;

                    float windScalar = 0.7 * w1 + 0.3 * w2; // ~[-1,1]
                    float3 windVec = TangentWindDir(t1, b1, _WindDirDeg);
                    half windAmt = _WindStrength * (half)windScalar * (half)(0.8 + 0.4 * r5);

                    // ---------- Layer selection ----------
                    // Ensure valid [0, _ArrayCount-1]
                    float layerF = floor(saturate(r9) * max(_ArrayCount, 1.0h));
                    layerF = clamp(layerF, 0.0, max(_ArrayCount - 1.0h, 0.0h));
                    half layerH = (half)layerF;

                    EmitQuadBent(basePos + jitter, nLeaned, side, bendDir, bendAmt,
                                 bottomHalf, topHalf, heightJ, cMul,
                                 windVec, windAmt, layerH,
                                 triStream);
                }
            }

            fixed4 fragBase(g2f i) : SV_Target
            {
                // Sample chosen array layer
                fixed4 albedo = UNITY_SAMPLE_TEX2DARRAY(_TexArray, float3(i.uv, i.layer)) * _Tint;
                albedo.rgb *= i.cMul;                    // color jitter
                clip(albedo.a - _Cutoff);

                half3 N = normalize(i.nWS);
                half3 L = normalize(_WorldSpaceLightPos0.xyz);

                half NdotL = saturate(dot(N, L));
                fixed shadow = SHADOW_ATTENUATION(i);
                shadow = lerp(1.0, shadow, _ShadowDim);

                half3 ambient = max(ShadeSH9(half4(N,1)), 0) + _AmbientBoost.xxx;

                // No translucency term
                half3 lit = albedo.rgb * (ambient + (NdotL * shadow));

                // bottom darkening
                half bottomFactor = lerp(1.0h - _Darkening, 1.0h, saturate(i.uv.y));
                lit *= bottomFactor;

                UNITY_APPLY_FOG(i.fogCoord, lit);
                return fixed4(lit, 1);
            }
            ENDCG
        }

        // ==================== ForwardAdd (extra lights) ====================
        Pass
        {
            Name "ForwardAdd"
            Tags { "LightMode"="ForwardAdd" }
            Blend One One
            ZWrite Off
            Cull Off

            CGPROGRAM
            #define MAX_QUADS 8
            #pragma target 4.0
            #pragma vertex   vert
            #pragma geometry geom
            #pragma fragment fragAdd

            #pragma multi_compile_fwdadd_fullshadows
            #pragma multi_compile_fog
            #pragma multi_compile_instancing

            #include "UnityCG.cginc"
            #include "Lighting.cginc"
            #include "AutoLight.cginc"

            // Texture Array
            UNITY_DECLARE_TEX2DARRAY(_TexArray);
            half _ArrayCount;

            fixed4 _Tint; half _Cutoff;
            half _Width, _TopWidth, _Height, _Lift;
            half _UV2XMin, _BypassFilter, _MaxSlopeDeg;
            half _Density, _MaxQuadsPerVertex, _JitterRadius, _YawJitterDeg, _SizeJitter;
            half _TiltDeg, _Bend;
            half _ShadowDim, _AmbientBoost, _Darkening;
            half _ColorJitter;

            // --- Wind uniforms ---
            sampler2D _WindTex;
            float4 _WindTiling;
            half   _WindSpeed;
            half   _WindStrength;
            half   _WindDirDeg;

            // --- LOD uniforms ---
            half _LODStart;
            half _LODEnd;
            half _MinDensity;

            UNITY_INSTANCING_BUFFER_START(Props)
            UNITY_INSTANCING_BUFFER_END(Props)

            struct appdata {
                float4 vertex:POSITION; float3 normal:NORMAL; float4 color:COLOR; float4 texcoord2:TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };
            struct v2g { float3 posWS:TEXCOORD0; float3 nWS:TEXCOORD1; half uv2x:TEXCOORD2; half weight:TEXCOORD3; UNITY_VERTEX_INPUT_INSTANCE_ID };
            struct g2f {
                float4 pos:SV_POSITION; half2 uv:TEXCOORD0; half3 nWS:TEXCOORD1; float3 posWS:TEXCOORD2;
                SHADOW_COORDS(3) UNITY_FOG_COORDS(4)
                half3 cMul:TEXCOORD5;
                half  layer:TEXCOORD6;
            };

            uint HashUint(uint x) {
                x ^= 2747636419u; x *= 2654435769u;
                x ^= x >> 16;     x *= 2654435769u;
                x ^= x >> 16;     x *= 2654435769u;
                return x ^ (x >> 16);
            }
            float Hash01(uint x) { return (HashUint(x) & 0x00FFFFFFu) * (1.0/16777216.0); }
            uint SeedFromFloat3(float3 p) {
                uint3 u = asuint(p * 131.0);
                uint s = 0u;
                s ^= HashUint(u.x + 0x9E3779B9u);
                s ^= HashUint(u.y + 0x85EBCA77u);
                s ^= HashUint(u.z + 0xC2B2AE3Du);
                return s;
            }
            void BuildBasis(float3 n, out float3 t, out float3 b)
            { float3 h = abs(n.y)<0.999?float3(0,1,0):float3(1,0,0); t=normalize(cross(h,n)); b=cross(n,t); }
            float3 TangentWindDir(float3 t, float3 b, half windDirDeg)
            {
                const float DEG2RAD = 0.017453292519943295f;
                float r = windDirDeg * DEG2RAD;
                float s, c; sincos(r, s, c);
                float2 wXZ = float2(c, s);
                return normalize(t * wXZ.x + b * wXZ.y);
            }

            v2g vert(appdata v)
            {
                v2g o; UNITY_SETUP_INSTANCE_ID(v);
                o.posWS = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.nWS   = UnityObjectToWorldNormal(v.normal);
                o.uv2x  = (half)v.texcoord2.x;
                o.weight= (half)v.color.g;
                return o;
            }

            void EmitQuadBent(float3 basePos, float3 n, float3 side, float3 bendDir, half bendAmt,
                              half bottomHalf, half topHalf, half height, half3 cMul,
                              float3 windVec, half windAmt, half layer,
                              inout TriangleStream<g2f> triStream)
            {
                float3 topPos = basePos + n * height
                              + bendDir * (bendAmt * height)
                              + windVec * (windAmt * height);

                float3 A = basePos - side * bottomHalf;
                float3 D = basePos + side * bottomHalf;
                float3 B = topPos  - side * topHalf;
                float3 C = topPos  + side * topHalf;

                g2f o;
                o.posWS=A; o.nWS=(half3)n; o.uv=half2(0,0); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                o.posWS=D; o.nWS=(half3)n; o.uv=half2(1,0); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                o.posWS=C; o.nWS=(half3)n; o.uv=half2(1,1); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                triStream.RestartStrip();
                o.posWS=C; o.nWS=(half3)n; o.uv=half2(1,1); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                o.posWS=B; o.nWS=(half3)n; o.uv=half2(0,1); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                o.posWS=A; o.nWS=(half3)n; o.uv=half2(0,0); o.cMul=cMul; o.layer=layer; o.pos=UnityWorldToClipPos(o.posWS); TRANSFER_SHADOW(o); UNITY_TRANSFER_FOG(o,o.pos); triStream.Append(o);
                triStream.RestartStrip();
            }

            [maxvertexcount(6*MAX_QUADS)]
            void geom(point v2g IN[1], inout TriangleStream<g2f> triStream)
            {
                if (_BypassFilter < 0.5h)
                    if (IN[0].uv2x != _UV2XMin) return;

                float3 n0 = normalize(IN[0].nWS);
                float minUpDot = cos(radians(_MaxSlopeDeg));
                if (n0.y < minUpDot) return;

                float3 t0, b0; BuildBasis(n0, t0, b0);
                float3 basePos = IN[0].posWS + n0 * _Lift;

                // -------- Distance-based density scaling --------
                float dist = distance(basePos, _WorldSpaceCameraPos);
                float span = max(_LODEnd - _LODStart, 1e-3);
                float t = saturate((dist - _LODStart) / span);
                half densityFactor = (half)lerp(1.0, _MinDensity, t);

                half  D  = max(_Density * densityFactor, 0.0h);
                half  Di = floor(D);
                half  Df = frac(D);
                uint seedPoint = SeedFromFloat3(basePos * 0.123 + n0 * 0.371);
                float rndPoint = Hash01(seedPoint);
                int k = (int)Di + ((rndPoint < Df) ? 1 : 0);
                int kmax=(int)clamp(_MaxQuadsPerVertex,1.0h,(half)MAX_QUADS);
                k = min(k,kmax); if(k<=0) return;

                const float yawScale = radians(_YawJitterDeg);
                const half  sizeJ   = _SizeJitter;
                const half  bendB   = _Bend;
                const half  jitterR = _JitterRadius;
                const half  width0  = _Width*0.5h;
                const half  topW0   = _TopWidth*0.5h;
                const half  height0 = _Height;
                const float tiltRadK= radians(_TiltDeg);

                const float DEG2RAD = 0.017453292519943295f;
                float2 worldWind2D = float2(cos(_WindDirDeg * DEG2RAD), sin(_WindDirDeg * DEG2RAD));

                [loop] for (int q=0;q<k;q++){
                    uint s = seedPoint ^ (uint)q * 747796405u;
                    float r1=Hash01(s+=0x9E3779B9u);
                    float r2=Hash01(s+=0x9E3779B9u);
                    float r3=Hash01(s+=0x9E3779B9u);
                    float r4=Hash01(s+=0x9E3779B9u);
                    float r5=Hash01(s+=0x9E3779B9u);
                    float r6=Hash01(s+=0x9E3779B9u);
                    float r7=Hash01(s+=0x9E3779B9u);
                    float r8=Hash01(s+=0x9E3779B9u);
                    float r9=Hash01(s+=0x9E3779B9u);

                    // jitter dir (no trig)
                    float2 v = float2(r1*2.0-1.0, r2*2.0-1.0);
                    float invLen = rsqrt(max(dot(v,v), 1e-6));
                    v *= invLen;
                    float  rad = sqrt(r2)*jitterR;
                    float3 jitter=(t0*v.x + b0*v.y)*rad;

                    // lean
                    float3 leanDir = normalize(t0*v.y + b0*(-v.x));
                    float  tiltSigned = tiltRadK*(r3*2.0-1.0);
                    float3 nLeaned = normalize(n0 + leanDir*tiltSigned);
                    float3 t1,b1; BuildBasis(nLeaned,t1,b1);

                    // yaw (single sincos)
                    float yaw=(r3*2.0-1.0)*yawScale; float sYaw,cYaw; sincos(yaw,sYaw,cYaw);
                    float3 side=t1*cYaw + b1*sYaw;

                    // sizes
                    half sizeMul= (half)(1.0 + (r4*2.0-1.0)*sizeJ);
                    half bottomHalf=width0*sizeMul, topHalf=topW0*sizeMul, heightJ=height0*sizeMul;

                    // bend: perpendicular to side in tangent plane
                    float3 bendDir=normalize(cross(nLeaned, side));
                    half   bendAmt=bendB*(half)(0.6 + 0.4*r5);

                    // color jitter
                    half3 cMul = (half3)1.0h + (half3)((float3(r6, r7, r8) * 2.0 - 1.0) * _ColorJitter);

                    // WIND
                    float2 windUVBase = IN[0].posWS.xz * _WindTiling.xy;
                    float windPhase = _Time.y * _WindSpeed;

                    float2 uv1 = windUVBase + worldWind2D * windPhase;
                    float w1 = tex2Dlod(_WindTex, float4(uv1, 0, 0)).r * 2.0 - 1.0;

                    float2 uv2 = windUVBase * 2.31 + 17.0 + worldWind2D * (windPhase * 1.7);
                    float w2 = tex2Dlod(_WindTex, float4(uv2, 0, 0)).r * 2.0 - 1.0;

                    float windScalar = 0.7 * w1 + 0.3 * w2;
                    float3 windVec = TangentWindDir(t1, b1, _WindDirDeg);
                    half windAmt = _WindStrength * (half)windScalar * (half)(0.8 + 0.4 * r5);

                    // Layer selection
                    float layerF = floor(saturate(r9) * max(_ArrayCount, 1.0h));
                    layerF = clamp(layerF, 0.0, max(_ArrayCount - 1.0h, 0.0h));
                    half layerH = (half)layerF;

                    EmitQuadBent(basePos + jitter, nLeaned, side, bendDir, bendAmt,
                                 bottomHalf, topHalf, heightJ, cMul,
                                 windVec, windAmt, layerH,
                                 triStream);
                }
            }

            fixed4 fragAdd(g2f i) : SV_Target
            {
                fixed4 albedo = UNITY_SAMPLE_TEX2DARRAY(_TexArray, float3(i.uv, i.layer)) * _Tint;
                albedo.rgb *= i.cMul;
                clip(albedo.a - _Cutoff);

                half3 N = normalize(i.nWS);
                half3 L = normalize(UnityWorldSpaceLightDir(i.posWS));
                UNITY_LIGHT_ATTENUATION(atten, i, i.posWS);

                half NdotL = saturate(dot(N, L));
                half3 col = albedo.rgb * (_LightColor0.rgb * NdotL * atten);

                half bottomFactor = lerp(1.0h - _Darkening, 1.0h, saturate(i.uv.y));
                col *= bottomFactor;

                UNITY_APPLY_FOG(i.fogCoord, col);
                return fixed4(col, 0);
            }
            ENDCG
        }
    }

    FallBack Off
}