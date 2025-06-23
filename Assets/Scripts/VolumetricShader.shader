Shader "Custom/VolumetricShader"
{
    Properties
    {
        _MainTex3DArray ("3D Albedo Array", 3D) = "" {} // New 3D texture array 
        _MainTex3DArray2 ("3D Albedo Array2", 3D) = "" {} // New 3D texture array 
        _Tiling ("Tiling", Vector) = (1,1,1,0)
    }

    SubShader
    {
        Tags { "RenderType" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Vertex
            {
	            float3 position;
	            float3 normal;
                uint data;
            };

            struct Triangle
            {
	            Vertex vertices[3];
            };

            StructuredBuffer<Triangle> vertices;
            float4x4 _ObjectToWorld;
            float3 _Tiling;
            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 barycentric : TEXCOORD1;
                half3 lightAmount : TEXCOORD2;
                float4 shadowCoords : TEXCOORD3;
                int textureIndex0 : TEXCOORD4;
                int textureIndex1 : TEXCOORD5;
                int textureIndex2 : TEXCOORD6;
                float3 pos : TEXCOORD7;
            };

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            TEXTURE3D(_MainTex3DArray);
            SAMPLER(sampler_MainTex3DArray);
            TEXTURE3D(_MainTex3DArray2);

            Varyings vert(uint vertexID : SV_VertexID)
            {
                Varyings OUT;

                // Compute triangle and vertex index
                uint triIndex = vertexID / 3;
                uint vertInTri = vertexID % 3;

                // Fetch triangle data
                Triangle tri = vertices[triIndex];

                // Current vertex position
                Vertex vertex = tri.vertices[vertInTri];
                float3 worldPosition = mul(_ObjectToWorld, float4(vertex.position, 1.0)).xyz;
                OUT.positionCS = TransformObjectToHClip(worldPosition);

                // Compute Barycentric coordinates for this vertex
                OUT.barycentric = (vertInTri == 0) ? float3(1, 0, 0) :
                                  (vertInTri == 1) ? float3(0, 1, 0) :
                                                     float3(0, 0, 1);

                // Pass texture indices for interpolation
                OUT.textureIndex0 = tri.vertices[0].data;
                OUT.textureIndex1 = tri.vertices[1].data;
                OUT.textureIndex2 = tri.vertices[2].data;

                // Shadow mapping
                OUT.shadowCoords = GetShadowCoord(GetVertexPositionInputs(worldPosition));

                // Lighting
                Light light = GetMainLight();
                float3 worldNormal = -normalize(mul((float3x3)_ObjectToWorld, vertex.normal));
                OUT.lightAmount = LightingLambert(light.color, light.direction, worldNormal.xyz);
                OUT.pos = worldPosition * _Tiling;
                // UV mapping

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // Sample the texture array for all three texture indices
                half4 tex0 = IN.textureIndex0 == 0 ? SAMPLE_TEXTURE3D(_MainTex3DArray, sampler_MainTex3DArray, IN.pos) : SAMPLE_TEXTURE3D(_MainTex3DArray2, sampler_MainTex3DArray, IN.pos);
                half4 tex1 = IN.textureIndex1 == 0 ? SAMPLE_TEXTURE3D(_MainTex3DArray, sampler_MainTex3DArray, IN.pos) : SAMPLE_TEXTURE3D(_MainTex3DArray2, sampler_MainTex3DArray, IN.pos);
                half4 tex2 = IN.textureIndex2 == 0 ? SAMPLE_TEXTURE3D(_MainTex3DArray, sampler_MainTex3DArray, IN.pos) : SAMPLE_TEXTURE3D(_MainTex3DArray2, sampler_MainTex3DArray, IN.pos);

                half4 blendedTexture = tex0 * IN.barycentric.x + tex1 * IN.barycentric.y + tex2 * IN.barycentric.z;

                // Compute shadow amount
                half shadowAmount = MainLightRealtimeShadow(IN.shadowCoords);

                // Apply minimum brightness threshold
                half minBrightness = 0.05;
                half finalLight = max(shadowAmount * IN.lightAmount, minBrightness);

                // Apply lighting to blended texture
                return finalLight * tex0;
            }
            ENDHLSL
        }
        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }

            HLSLPROGRAM
            #pragma vertex ShadowPassVertex
            #pragma fragment ShadowPassFragment

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
            };

            struct Vertex
            {
	            float3 position;
	            float3 normal;
                uint data;
            };

            struct Triangle
            {
	            Vertex vertices[3];
            };
            StructuredBuffer<Triangle> vertices;
            float4x4 _ObjectToWorld;

            Varyings ShadowPassVertex(appdata v)
            {
                Varyings OUT;

                // Compute triangle and vertex index
                uint triIndex = v.vertexID / 3;
                uint vertInTri = v.vertexID % 3;

                // Fetch triangle data
                Triangle tri = vertices[triIndex];
                float3 worldPos = mul(_ObjectToWorld, float4(tri.vertices[vertInTri].position, 1.0)).xyz;

                // Convert to clip space for shadow rendering
                OUT.positionCS = TransformWorldToHClip(worldPos);

                return OUT;
            }

            half4 ShadowPassFragment(Varyings IN) : SV_Target
            {
                return 0;
            }

            ENDHLSL
        }
    }
}
