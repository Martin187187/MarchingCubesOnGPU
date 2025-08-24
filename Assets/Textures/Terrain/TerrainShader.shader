Shader "Custom/TerrainShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)

        _MainTex   ("Terrain Texture Array (Albedo)", 2DArray) = "white" {}
        _NormalTex ("Normal Texture Array (_n)",      2DArray) = "bump"  {}
        _MaskTex   ("Spec/Mask Texture Array (_s)",   2DArray) = "white" {}

        _BumpStrength ("Bump Strength", Float) = 0.35
        _TextureScale ("Terrain Texture Scale", Float) = 0.02

        _Metallic   ("Fallback Metallic",   Float) = 0.5
        _AO         ("Fallback AO",         Float) = 0.5
        _Smoothness ("Fallback Smoothness", Float) = 0.5

        // --- LabPBR controls ---
        [Toggle] _UseLabPBR ("_MaskTex is LabPBR _s", Float) = 1
        [Range(0,1)] _AOMin ("AO Floor", Float) = 0.35
        [Range(0,1)] _LabMetalThreshold ("LabPBR Metal Threshold (G >=)", Float) = 0.902
        [Toggle] _FlipGreen ("Flip Normal Y (if needed)", Float) = 0
        _EmissionBoost ("Emission Boost", Float) = 1.0

        // --- Crack overlay ---
        [Toggle(_CRACKS_ON)] _EnableCracks ("Enable Cracks", Float) = 0
        _CrackTex      ("Crack Texture (RGBA, A=mask)", 2D) = "white" {}
        _CrackNormal   ("Crack Normal", 2D) = "bump" {}
        _CrackColor    ("Crack Tint", Color) = (0,0,0,1)
        _CrackTiling   ("Crack Tiling", Float) = 1.0
        [Range(0,2)] _CrackStrength ("Crack Darkness", Float) = 1.0
        [Range(0,1)] _CrackSmoothMul("Smoothness Multiplier in Cracks", Float) = 0.7
        _CrackNormalStrength ("Crack Normal Strength", Range(0,2)) = 1.0
    }

    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 200

        CGPROGRAM
        #pragma surface surf Standard fullforwardshadows vertex:vert
        #pragma target 3.5

        // --- keyword for on/off ---
        #pragma shader_feature _ _CRACKS_ON

        UNITY_DECLARE_TEX2DARRAY(_MainTex);
        UNITY_DECLARE_TEX2DARRAY(_NormalTex);
        UNITY_DECLARE_TEX2DARRAY(_MaskTex);

        sampler2D _CrackTex;
        sampler2D _CrackNormal;

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

        float4 _CrackColor;
        float  _CrackTiling;
        float  _CrackStrength;
        float  _CrackSmoothMul;
        float  _CrackNormalStrength;

        struct Input
        {
            float4 color : COLOR;     
            float3 worldPos;
            float3 normal;
            float3 terrain;           
            float3 meshNormalTangent; 

            float crackWeight : TEXCOORD3; // z-Komponente aus uv3
        };

        void vert (inout appdata_full v, out Input data)
        {
            UNITY_INITIALIZE_OUTPUT(Input, data);
            data.terrain = v.texcoord2.xyz;
            data.normal  = v.normal;
            TANGENT_SPACE_ROTATION;
            data.meshNormalTangent = mul(rotation, v.normal);

            data.crackWeight = v.texcoord3.z;
        }

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

        float4 DecodeLabPBR_NormalAO(float4 nRaw)
        {
            float2 xy = nRaw.rg * 2.0 - 1.0;
            if (_FlipGreen > 0.5) xy.y = -xy.y;
            float z = sqrt(saturate(1.0 - dot(xy, xy)));
            float ao = 1.0 - nRaw.b;
            return float4(normalize(float3(xy, z)), ao);
        }

        float4 SampleTriplanarNormalAO(Input IN, int idx)
        {
            float3 blend = GetTriplanarBlendWeights(IN.normal);
            float4 nx = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.yz * _TextureScale, IN.terrain[idx]));
            float4 ny = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.xz * _TextureScale, IN.terrain[idx]));
            float4 nz = UNITY_SAMPLE_TEX2DARRAY(_NormalTex, float3(IN.worldPos.xy * _TextureScale, IN.terrain[idx]));
            float4 dx = DecodeLabPBR_NormalAO(nx);
            float4 dy = DecodeLabPBR_NormalAO(ny);
            float4 dz = DecodeLabPBR_NormalAO(nz);

            float3 wx = normalize(dx.x * float3(0,0,1) + dx.y * float3(0,1,0) + dx.z * float3(1,0,0));
            float3 wy = normalize(dy.x * float3(1,0,0) + dy.y * float3(0,1,0) + dy.z * float3(0,0,1));
            float3 wz = normalize(dz.x * float3(1,0,0) + dz.y * float3(0,0,1) + dz.z * float3(0,1,0));

            float3 n = normalize(wx * blend.x + wy * blend.y + wz * blend.z);
            float  ao = dx.a * blend.x + dy.a * blend.y + dz.a * blend.z;

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

        // --- Crack Triplanar ---
        float4 SampleTriplanarCrackTex(float3 worldPos, float3 normal)
        {
            float3 blend = GetTriplanarBlendWeights(normal);
            float2 uvx = worldPos.yz * _CrackTiling;
            float2 uvy = worldPos.xz * _CrackTiling;
            float2 uvz = worldPos.xy * _CrackTiling;

            float4 cx = tex2D(_CrackTex, uvx);
            float4 cy = tex2D(_CrackTex, uvy);
            float4 cz = tex2D(_CrackTex, uvz);

            return cx * blend.x + cy * blend.y + cz * blend.z;
        }

        float3 SampleTriplanarCrackNormal(float3 worldPos, float3 normal)
        {
            float3 blend = GetTriplanarBlendWeights(normal);
            float2 uvx = worldPos.yz * _CrackTiling;
            float2 uvy = worldPos.xz * _CrackTiling;
            float2 uvz = worldPos.xy * _CrackTiling;

            float3 nx = UnpackNormal(tex2D(_CrackNormal, uvx));
            float3 ny = UnpackNormal(tex2D(_CrackNormal, uvy));
            float3 nz = UnpackNormal(tex2D(_CrackNormal, uvz));

            return normalize(nx * blend.x + ny * blend.y + nz * blend.z);
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

            [unroll]
            for (int i = 0; i < 3; i++)
            {
                float w = IN.color[i];
                if (w <= 0) continue;

                float4 c = SampleTriplanarColor(IN, i);
                float4 nAO = SampleTriplanarNormalAO(IN, i);
                float4 s = SampleTriplanarMask(IN, i);

                float smooth = s.r;
                float metal  = step(_LabMetalThreshold, s.g);
                float emiss  = s.b;

                accColor   += c * w;
                nSum       += nAO.xyz * w;
                accAO      += nAO.w * w;
                accMetal   += metal * w;
                accSmooth  += smooth * w;
                eSum       += emiss * w;
            }

            if (accColor.a <= 0)
            {
                accColor = 1;
                accMetal = _Metallic;
                accAO    = _AO;
                accSmooth= _Smoothness;
            }

            float3 finalNormal = normalize(lerp(normalize(baseNormal), normalize(nSum), _BumpStrength));

            // --- Crack overlay (only if enabled) ---
            #if _CRACKS_ON
            float4 crack = SampleTriplanarCrackTex(IN.worldPos, IN.normal);
            float crackMask = saturate(crack.a * IN.crackWeight);

            // Apply cracks to albedo
            float3 darkened = accColor.rgb * (1.0 - _CrackStrength * crackMask);
            float3 tinted   = lerp(darkened, _CrackColor.rgb, crackMask * _CrackColor.a);
            float3 albedoWithCracks = lerp(accColor.rgb, tinted, crackMask);

            // Apply cracks to smoothness
            float smoothWithCracks = lerp(accSmooth, accSmooth * _CrackSmoothMul, crackMask);

            // Apply cracks to normal
            float3 crackNormal = SampleTriplanarCrackNormal(IN.worldPos, IN.normal);
            finalNormal = normalize(lerp(finalNormal, crackNormal, crackMask * _CrackNormalStrength));

            accColor.rgb = albedoWithCracks;
            accSmooth = smoothWithCracks;
            #endif

            o.Albedo     = accColor.rgb * _Color.rgb;
            o.Normal     = finalNormal;
            o.Metallic   = saturate( lerp(_Metallic, accMetal, _UseLabPBR) );
            o.Smoothness = saturate( lerp(_Smoothness, accSmooth, _UseLabPBR) );

            float aoRaw = saturate( lerp(_AO, accAO, _UseLabPBR) );
            #if _CRACKS_ON
            aoRaw = saturate( aoRaw * (1.0 - 0.25 * crackMask));
            #endif
            o.Occlusion = lerp(_AOMin, 1.0, aoRaw);

            float emissive = saturate(eSum * _EmissionBoost);
            o.Emission = accColor.rgb * emissive;
        }
        ENDCG
    }
    FallBack "Diffuse"
}
