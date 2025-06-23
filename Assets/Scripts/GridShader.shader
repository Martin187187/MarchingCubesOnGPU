Shader "Custom/GeneratedMeshLighting"
{
    Properties
    {
        _MainTexArray ("Albedo Array", 2DArray) = "" {} // Texture array
        _Tiling ("Tiling", Vector) = (1,1,0,0)
        _NormalArray ("Albedo Array", 2DArray) = "" {} // Texture array
        _MaskArray ("Albedo Array", 2DArray) = "" {} // Texture array
        _NormalStrength ("Normal Map Strength", Float) = 1.0

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
            float4 _MainTex_ST;
            float2 _Tiling;
            float _NormalStrength;
            struct Varyings
            {
                float4 positionCS  : SV_POSITION;
                float3 position : TEXCOORD0;
                float3 barycentric : TEXCOORD1;
                float4 shadowCoords : TEXCOORD3;
                float textureIndex0 : TEXCOORD4;
                float textureIndex1 : TEXCOORD5;
                float textureIndex2 : TEXCOORD6;
                float3 normal : TEXCOORD7;
            };
            float GetSmoothnessPower(float rawSmoothness) {
                return exp2(10 * rawSmoothness + 1);
            }

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            TEXTURE2D_ARRAY(_MainTexArray);
            SAMPLER(sampler_MainTexArray);
            
            TEXTURE2D_ARRAY(_NormalArray);
            SAMPLER(sampler_NormalArray);
            
            TEXTURE2D_ARRAY(_MaskArray);
            SAMPLER(sampler_MaskArray);

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
                float3 worldNormal = -normalize(mul((float3x3)_ObjectToWorld, vertex.normal));
                OUT.position = worldPosition;
                OUT.normal = worldNormal;

                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 normal = normalize(IN.normal  * 0.1);
                float3 pos = IN.position  * 0.1;
                float3 x_axis = float3(1.0, 0.0, 0.0);
                float3 y_axis = float3(0.0, 1.0, 0.0);
                float3 z_axis = float3(0.0, 0.0, 1.0);
                
                float x_weight = abs(dot(normal, x_axis));
                float y_weight = abs(dot(normal, y_axis));
                float z_weight = abs(dot(normal, z_axis));

                float total_weight = x_weight + y_weight + z_weight;
                total_weight = total_weight == 0 ? 1 : total_weight;
                // Sample the texture array for all three texture indices
                half4 tex0_x = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.yz, IN.textureIndex0);
                half4 tex0_y = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xz, IN.textureIndex0);
                half4 tex0_z = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xy, IN.textureIndex0);
                
                float4 color0 = tex0_x * x_weight + tex0_y * y_weight + tex0_z * z_weight;
                color0 /= (x_weight + y_weight + z_weight); 
                half4 tex1_x = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.yz, IN.textureIndex1);
                half4 tex1_y = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xz, IN.textureIndex1);
                half4 tex1_z = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xy, IN.textureIndex1);
                
                float4 color1 = tex1_x * x_weight + tex1_y * y_weight + tex1_z * z_weight;
                color1 /= (x_weight + y_weight + z_weight); 
                half4 tex2_x = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.yz, IN.textureIndex2);
                half4 tex2_y = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xz, IN.textureIndex2);
                half4 tex2_z = SAMPLE_TEXTURE2D_ARRAY(_MainTexArray, sampler_MainTexArray, pos.xy, IN.textureIndex2);
                
                float4 color2 = tex2_x * x_weight + tex2_y * y_weight + tex2_z * z_weight;
                color2 /= (x_weight + y_weight + z_weight); 

                half4 blendedTexture = color0 * IN.barycentric.x + color1 * IN.barycentric.y + color2 * IN.barycentric.z;

                // Compute shadow amount
                half shadowAmount = MainLightRealtimeShadow(IN.shadowCoords);


                
                half3 norm0x = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.yz, IN.textureIndex0).rgb ;
                half3 norm0y = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xz, IN.textureIndex0).rgb;
                half3 norm0z = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xy, IN.textureIndex0).rgb ;
                float3 norm0 = norm0x * x_weight + norm0y * y_weight + norm0z * z_weight;
                norm0 /= (x_weight + y_weight + z_weight);
                
                half3 norm1x = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.yz, IN.textureIndex1).rgb ;
                half3 norm1y = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xz, IN.textureIndex1).rgb ;
                half3 norm1z = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xy, IN.textureIndex1).rgb ;
                float3 norm1 = norm1x * x_weight + norm1y * y_weight + norm1z * z_weight;
                norm1 /= (x_weight + y_weight + z_weight);
                
                half3 norm2x = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.yz, IN.textureIndex2).rgb ;
                half3 norm2y = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xz, IN.textureIndex2).rgb;
                half3 norm2z = SAMPLE_TEXTURE2D_ARRAY(_NormalArray, sampler_NormalArray, pos.xy, IN.textureIndex2).rgb;
                float3 norm2 = norm2x * x_weight + norm2y * y_weight + norm2z * z_weight;
                norm2 /= (x_weight + y_weight + z_weight); 

                float3 norm = (norm0 * IN.barycentric.x + norm1 * IN.barycentric.y + norm2 * IN.barycentric.z) * 2 - 1;
                half4 specularData0x = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.yz, IN.textureIndex0); 
                half4 specularData0y = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xz, IN.textureIndex0); 
                half4 specularData0z = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xy, IN.textureIndex0); 
                float3 spec0 = specularData0x * x_weight + specularData0y * y_weight + specularData0z * z_weight;
                spec0 /= (x_weight + y_weight + z_weight);

                half4 specularData1x = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.yz, IN.textureIndex1); 
                half4 specularData1y = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xz, IN.textureIndex1); 
                half4 specularData1z = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xy, IN.textureIndex1); 
                float3 spec1 = specularData1x * x_weight + specularData1y * y_weight + specularData1z * z_weight;
                spec1 /= (x_weight + y_weight + z_weight);

                half4 specularData2x = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.yz, IN.textureIndex2); 
                half4 specularData2y = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xz, IN.textureIndex2); 
                half4 specularData2z = SAMPLE_TEXTURE2D_ARRAY(_MaskArray, sampler_MaskArray, pos.xy, IN.textureIndex2); 
                float3 spec2 = specularData2x * x_weight + specularData2y * y_weight + specularData2z * z_weight;
                spec2 /= (x_weight + y_weight + z_weight);
                
                float3 spec = spec0 * IN.barycentric.x + spec1 * IN.barycentric.y + spec2 * IN.barycentric.z;
                // Extract individual channels
                half specularIntensity = spec.r;  // Red channel: specular intensity
                half roughness = spec.g;          // Green channel: roughness

                float3 combinedNormal = normalize(IN.normal + _NormalStrength * norm);
                
                Light mainLight = GetMainLight();
                // Phong Specular Model
                half3 viewDir = GetWorldSpaceViewDir(pos);
                half3 lightAmount = LightingLambert(mainLight.color, mainLight.direction, combinedNormal);
                half3 reflectDir = reflect(-mainLight.direction, combinedNormal);
                half specularFactor = pow(max(dot(viewDir, reflectDir), 0.0), 1);
                half3 finalLightAmount = lightAmount + specularIntensity * specularFactor * (1.0 - roughness);

                // Apply minimum brightness threshold
                half minBrightness = 0.1;
                half finalLight = max(shadowAmount * finalLightAmount, minBrightness);

                // Additional lights
                int additionalLightCount = GetAdditionalLightsCount();
                for (int i = 0; i < additionalLightCount; i++) {
                    float3 combinedNormal2 = normalize(IN.normal + 0.3 * norm);
                    Light pointLight = GetAdditionalLight(i, IN.position, 1);
                    
                    float3 radiance = pointLight.color * (pointLight.distanceAttenuation * pointLight.shadowAttenuation);

                    float diffuse = saturate(dot(combinedNormal2, pointLight.direction));
                    
                    half3 reflectDir2 = reflect(-pointLight.direction, combinedNormal2);
                    half specularFactor2 = pow(max(dot(viewDir, reflectDir2), 0.0), 1);
                    
                    finalLight += radiance * (diffuse  + specularIntensity * specularFactor2 * (1.0 - roughness));
                    
                }

                // Apply lighting to blended texture
                return finalLight * blendedTexture;
                }
            ENDHLSL
        }
        Pass
        {
            Cull Off
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
