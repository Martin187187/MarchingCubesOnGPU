Shader "Custom/TerrainShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)

        _MainTex   ("Terrain Texture Array (Albedo)", 2DArray) = "white" {}
        _NormalTex ("Normal Texture Array (_n)",      2DArray) = "bump"  {}
        _MaskTex   ("Spec/Mask Texture Array (_s)",   2DArray) = "white" {}

        _BumpStrength ("Bump Strength", Float) = 0.35
        _TextureScale ("Texture Scale", Float) = 0.02

        _Metallic   ("Fallback Metallic",   Float) = 0.5
        _AO         ("Fallback AO",         Float) = 0.5
        _Smoothness ("Fallback Smoothness", Float) = 0.5

        // --- LabPBR controls ---
        [Toggle] _UseLabPBR ("_MaskTex is LabPBR _s", Float) = 1
        [Range(0,1)] _AOMin ("AO Floor", Float) = 0.35
        [Range(0,1)] _LabMetalThreshold ("LabPBR Metal Threshold (G >=)", Float) = 0.902 // ~230/255
        [Toggle] _FlipGreen ("Flip Normal Y (if needed)", Float) = 0
        _EmissionBoost ("Emission Boost", Float) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.5

        UNITY_DECLARE_TEX2DARRAY(_MainTex);
        UNITY_DECLARE_TEX2DARRAY(_NormalTex);
        UNITY_DECLARE_TEX2DARRAY(_MaskTex);

        fixed4 _Color;
        float _BumpStrength;
        float _TextureScale;

        float _Metallic;
        float _AO, _AOMin ;
        float _Smoothness;

        float _UseLabPBR;
        float _LabMetalThreshold;
        float _FlipGreen;
        float _EmissionBoost;

        struct Input
        {
            float4 color : COLOR;     // layer weights in RGB (assuming your mesh packs weights here)
            float3 worldPos;
            float3 normal;
            float3 terrain;           // indices for array layers (x,y,z slots)
            float3 meshNormalTangent; // your original approximation
        };

        void vert (inout appdata_full v, out Input data)
        {
            UNITY_INITIALIZE_OUTPUT(Input, data);
            data.terrain = v.texcoord2.xyz;
            data.normal  = v.normal;
            TANGENT_SPACE_ROTATION;
            data.meshNormalTangent = mul(rotation, v.normal);
        }

        // --- Helpers ---
        float3 GetTriplanarBlendWeights(float3 n)
        {
            float3 b = abs(n);
            b = pow(b, 4.0);
            return b / (b.x + b.y + b.z + 1e-5);
        }

        float4 SampleTriplanarColor(Input IN, int idx)
        {
            float3 blend = GetTriplanarBlendWeights(IN.normal);

            float4 cx = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(IN.worldPos.yz * _TextureScale, IN.terrain[idx]));
            float4 cy = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(IN.worldPos.xz * _TextureScale, IN.terrain[idx]));
            float4 cz = UNITY_SAMPLE_TEX2DARRAY(_MainTex, float3(IN.worldPos.xy * _TextureScale, IN.terrain[idx]));

            return cx * blend.x + cy * blend.y + cz * blend.z;
        }

        // Decode LabPBR normal raw sample:
        // RG: normal XY in [0..1] -> [-1..1], B: AO (0=full occlusion, 1=no occlusion), A: height
        // Reconstruct Z, optional flip green.
        float4 DecodeLabPBR_NormalAO(float4 nRaw)
        {
            float2 xy = nRaw.rg * 2.0 - 1.0;
            if (_FlipGreen > 0.5) xy.y = -xy.y; // only if you see inverted lighting
            float z = sqrt(saturate(1.0 - dot(xy, xy)));
            float ao = 1.0 - nRaw.b;           // invert (0=dark crevices -> 1=unoccluded)
            return float4(normalize(float3(xy, z)), ao);
        }

        // Triplanar normal + AO from the _n texture array (LabPBR)
        float4 SampleTriplanarNormalAO(Input IN, int idx)
        {
            float3 blend = GetTriplanarBlendWeights(IN.normal);

            float4 nx = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.yz * _TextureScale, IN.terrain[idx]));
            float4 ny = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.xz * _TextureScale, IN.terrain[idx]));
            float4 nz = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.xy * _TextureScale, IN.terrain[idx]));

            float4 dx = DecodeLabPBR_NormalAO(nx);
            float4 dy = DecodeLabPBR_NormalAO(ny);
            float4 dz = DecodeLabPBR_NormalAO(nz);

            // Approximate axis-space to world-ish reorientation (same as your original)
            float3 wx = normalize(dx.x * float3(0,0,1) + dx.y * float3(0,1,0) + dx.z * float3(1,0,0));
            float3 wy = normalize(dy.x * float3(1,0,0) + dy.y * float3(0,1,0) + dy.z * float3(0,0,1));
            float3 wz = normalize(dz.x * float3(1,0,0) + dz.y * float3(0,0,1) + dz.z * float3(0,1,0));

            float3 n = normalize(wx * blend.x + wy * blend.y + wz * blend.z);
            float  ao = dx.a * blend.x + dy.a * blend.y + dz.a * blend.z; // weighted average

            return float4(n, ao);
        }

        float4 SampleTriplanarMask(Input IN, int idx)
        {
            float3 blend = GetTriplanarBlendWeights(IN.normal);

            float4 mx = UNITY_SAMPLE_TEX2DARRAY(_MaskTex, float3(IN.worldPos.yz * _TextureScale, IN.terrain[idx]));
            float4 my = UNITY_SAMPLE_TEX2DARRAY(_MaskTex, float3(IN.worldPos.xz * _TextureScale, IN.terrain[idx]));
            float4 mz = UNITY_SAMPLE_TEX2DARRAY(_MaskTex, float3(IN.worldPos.xy * _TextureScale, IN.terrain[idx]));

            return mx * blend.x + my * blend.y + mz * blend.z;
        }

        void surf (Input IN, inout SurfaceOutputStandard o)
        {
            float4 accColor = 0;
            float3 accNormal = 0;
            float  accMetal = 0;
            float  accSmooth = 0;
            float  accAO = 0;
            float3 baseNormal = normalize(IN.meshNormalTangent);
            float3 nSum = 0;
            float  eSum = 0;

            // 3 layers, weights from vertex color (RGB)
            [unroll]
            for (int i = 0; i < 3; i++)
            {
                float w = IN.color[i];
                if (w <= 0) continue;

                // Albedo (triplanar)
                float4 c = SampleTriplanarColor(IN, i);

                // Normal + AO (from LabPBR _n)
                float4 nAO = SampleTriplanarNormalAO(IN, i);

                // Spec/Mask (LabPBR _s)
                float4 s = SampleTriplanarMask(IN, i);

                // --- LabPBR mapping ---
                float smooth = s.r; // perceptual smoothness in [0..1]
                float metal  = step(_LabMetalThreshold, s.g); // 1 if metal selector region, else 0
                float emiss  = s.b; // emissive 0..~1 (255 reserved; safe normalize)

                // Accumulate with layer weight
                accColor   += c * w;
                nSum       += nAO.xyz * w;
                accAO      += nAO.w * w;
                accMetal   += metal * w;
                accSmooth  += smooth * w;
                eSum       += emiss * w;
            }

            // Fallbacks if no layer weights present
            if (accColor.a <= 0)
            {
                accColor = 1;
                accMetal = _Metallic;
                accAO    = _AO;
                accSmooth= _Smoothness;
            }

            float3 finalNormal = normalize(lerp(normalize(baseNormal), normalize(nSum), _BumpStrength));

            o.Albedo     = accColor.rgb * _Color.rgb;
            o.Normal     = finalNormal;
            o.Metallic   = saturate( lerp(_Metallic, accMetal, _UseLabPBR) );
            o.Smoothness = saturate( lerp(_Smoothness, accSmooth, _UseLabPBR) );
            float aoRaw = saturate( lerp(_AO, accAO, _UseLabPBR) );
            o.Occlusion = lerp(_AOMin, 1.0, aoRaw);   // same as _AOMin + (1 - _AOMin) * aoRaw

            // Optional emission from LabPBR alpha
            float emissive = saturate(eSum * _EmissionBoost);
            o.Emission = accColor.rgb * emissive; // tint emission by albedo; change if you prefer white emission
        }
        ENDCG
    }
    FallBack "Diffuse"
}
