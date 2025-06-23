Shader "Unlit/ProceduralGridShader"
{
    Properties
    {
        _Color ("Color", Color) = (1,1,1,1)
        _GridSize ("Grid Size", Float) = 3
        _CellSize ("Cell Size", Float) = 0.1
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma geometry geom
            #pragma fragment frag
            #include "UnityCG.cginc"

            float4 _Color;
            int _GridSize;
            float _CellSize;

            struct appdata
            {
                uint vertexID : SV_VertexID;
            };

            struct v2g
            {
                float4 pos : SV_POSITION;
                float3 normal : NORMAL;
            };

            // Vertex Shader (Procedurally Generate Grid Positions)
            v2g vert(appdata v)
            {
                v2g o;
                int index = v.vertexID;
                int x = index % (_GridSize - 1);
                int y = index / (_GridSize - 1);

                // Compute world position
                float3 position = float3(x * _CellSize, 0.0, y * _CellSize);
                o.pos = UnityObjectToClipPos(float4(position, 1.0));

                // Upward normal
                o.normal = float3(0, 1, 0);
                return o;
            }

            // Geometry Shader (Construct Triangles)
            [maxvertexcount(6)]
            void geom(point v2g input[1], inout TriangleStream<v2g> triStream)
            {
                v2g v0, v1, v2, v3;

                float3 basePos = input[0].pos.xyz;
                float cell = _CellSize;

                // Create four corners of the grid cell
                v0.pos = float4(basePos, 1);
                v1.pos = float4(basePos + float3(cell, 0, 0), 1);
                v2.pos = float4(basePos + float3(0, 0, cell), 1);
                v3.pos = float4(basePos + float3(cell, 0, cell), 1);

                v0.normal = v1.normal = v2.normal = v3.normal = float3(0, 1, 0);

                // First Triangle
                triStream.Append(v0);
                triStream.Append(v2);
                triStream.Append(v1);

                // Second Triangle
                triStream.Append(v1);
                triStream.Append(v2);
                triStream.Append(v3);
            }

            // Fragment Shader (Simple Normal-based Shading)
            fixed4 frag(v2g i) : SV_Target
            {
                return fixed4(i.normal * 0.5 + 0.5, 1.0);
            }

            ENDCG
        }
    }
}
