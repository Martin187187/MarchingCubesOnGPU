using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class ComputeShaderMeshData2 : MonoBehaviour
{
    public ComputeShader computeShader;
    public Material materialInstance;

    private ComputeBuffer verticesBuffer;
    private ComputeBuffer counterBuffer;
    private ComputeBuffer voxelBuffer;
    
    private int vertexCount;
    private MaterialPropertyBlock mpb;
    private Voxel[] voxelData;

    [Header("Grid Settings")]
    public int gridSize = 10; // 10x10x10 voxel grid

    public int chunkSize = 16;
    

    private MeshCollider meshCollider;
    public Vector3Int index;
    
    public TerrainManager terrainManager;
    public static double Sigmoid(double x)
    {
        return 1.0 / (1.0 + Math.Exp(-x));
    }
    void Start()
    {
        int voxelGridSize = gridSize * gridSize * gridSize;
        vertexCount = voxelGridSize * 15; // Max possible vertices per voxel cell

        // Create buffers
        verticesBuffer = new ComputeBuffer(vertexCount, (sizeof(float) * 6 + sizeof(uint)) * 3 , ComputeBufferType.Append);
        counterBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments);

        // Create the voxel buffer to store positions and iso values
        voxelBuffer = new ComputeBuffer(voxelGridSize, sizeof(float) + sizeof(int));

        // Initialize voxel data (position + iso value)
        voxelData = new Voxel[voxelGridSize];

        float noiseScale = 0.09f; // Controls terrain smoothness
        float heightScale = gridSize * 1f; // Determines terrain elevation

        float oreNoiseScale = 0.06f; // Higher values create smaller blobs
        float oreThreshold = 0.5f;  // higher = rarer, Lower = more frequent
        int minOreDepth = (int)(chunkSize * 0.5f);  // Ore starts below 40% of terrain height
         float Perlin3D(float x, float y, float z)
        {
            float xy = Mathf.PerlinNoise(x, y);
            float yz = Mathf.PerlinNoise(y, z);
            float xz = Mathf.PerlinNoise(x, z);
            float xyz = Mathf.PerlinNoise(xy, xz);
            return xyz;
        }
        for (int z = 0; z < gridSize; z++)
{
    for (int y = 0; y < gridSize; y++)
    {
        for (int x = 0; x < gridSize; x++)
        {
            int index = x + y * gridSize + z * gridSize * gridSize;

            Vector3 pos = transform.position + 
                          new Vector3(x, y, z) * chunkSize / (gridSize - 3) + 
                          new Vector3(100000, 0, 100000);

            // Generate base Perlin noise
            float baseNoise = Perlin3D(pos.x * noiseScale, 1, pos.z * noiseScale);

            // Normalize height in [0, 1] and apply exponent for sharper increase
            float heightFactor = Mathf.Pow(y / (float)gridSize, 1f); // Try 1.5f–2.5f for sharper peaks

            // Increase Perlin amplitude with height
            float amplifiedNoise = baseNoise * (1f + pos.y * 1.25f); // 4f is gain factor

            // Final terrain height
            float perlinHeight = -5f + amplifiedNoise;

            // Compute isoValue (terrain solidness)
            float isoValue = ((pos.y - perlinHeight) / (float)gridSize) * 2f;

            // Default terrain type based on isoValue thresholds
            TerrainType type = (TerrainType)(isoValue < 0.47f ? (isoValue < 0.40f ? 2 : 1) : 0);

            voxelData[index] = new Voxel(type, isoValue);
        }
    }
}


        // Set the voxel data into the buffer
        voxelBuffer.SetData(voxelData);

        // Pass parameters to compute shader
        computeShader.SetInt("gridSize", gridSize);
        computeShader.SetFloat("isoLevel", 0.5f);
        int kernelIndex = computeShader.FindKernel("March");
        computeShader.SetBuffer(kernelIndex,"voxelGrid", voxelBuffer);
        computeShader.SetBuffer(kernelIndex,"vertices", verticesBuffer);
        computeShader.SetBuffer(kernelIndex,"counter", counterBuffer);
        verticesBuffer.SetCounterValue(0);
        
        int[] args = { 0, 1, 0, 0 };
        counterBuffer.SetData(args);

        // Dispatch compute shader
        computeShader.Dispatch(kernelIndex, gridSize/4, gridSize/4, gridSize/4);

        // Assign buffers to material
        mpb = new MaterialPropertyBlock();
        mpb.SetBuffer("vertices", verticesBuffer);
        mpb.SetMatrix("_ObjectToWorld", transform.localToWorldMatrix);
        
        
        meshCollider = gameObject.AddComponent<MeshCollider>();
        ReadVerticesFromComputeShader();
    }

    void Update()
    {
        
        Graphics.DrawProceduralIndirect(materialInstance, new Bounds(transform.position,  chunkSize*2 * Vector3.one), MeshTopology.Triangles, counterBuffer, 0, null, mpb);
    }

    public TerrainType GetType(Vector3 position)
    {
        Vector3Int roundedVector = new Vector3Int(
            Mathf.RoundToInt(position.x),
            Mathf.RoundToInt(position.y),
            Mathf.RoundToInt(position.z)
        );
        int index = roundedVector.x + roundedVector.y * gridSize + roundedVector.z * gridSize * gridSize;
        return voxelData[index].type;
    }
    public void UpdateVoxelGridWithCube()
    {

        int cubeSize = gridSize / 2;
        int start = (gridSize - cubeSize) / 2;
        int end = start + cubeSize;

        for (int z = start; z < end; z++)
        {
            for (int y = start; y < end; y++)
            {
                for (int x = start; x < end; x++)
                {
                    int index = x + y * gridSize + z * gridSize * gridSize;
                    voxelData[index].iso = 1f; // Set iso value to 0 inside the cube
                }
            }
        }
        
        int kernelIndex = computeShader.FindKernel("March");
        computeShader.SetBuffer(kernelIndex,"voxelGrid", voxelBuffer);
        computeShader.SetBuffer(kernelIndex,"vertices", verticesBuffer);
        computeShader.SetBuffer(kernelIndex,"counter", counterBuffer);
        voxelBuffer.SetData(voxelData); // Update buffer

        verticesBuffer.SetCounterValue(0);
        
        int[] args = { 0, 1, 0, 0 };
        counterBuffer.SetData(args);
        
        computeShader.Dispatch(kernelIndex, gridSize / 4, gridSize / 4, gridSize / 4);
        
    }
    
    
    public void UpdateVoxelGridWithSphere(Vector3 position, float radius, float strength, TerrainType terrainType, bool doFallOff = true)
    {
        float radiusSqr = radius * radius; // Precompute squared radius for efficiency

        for (int z = 0; z < gridSize; z++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    Vector3 voxelPosition = new Vector3(x, y, z);

                    // Check if voxel is within the radius (using squared distance for performance)
                    float sqrDistance = (voxelPosition - position).sqrMagnitude;
                    if (sqrDistance <= radiusSqr)
                    {
                        float distance = Mathf.Sqrt(sqrDistance); // Get the distance from the center
                        float normalizedDistance = distance / radius; // Normalize distance to [0, 1]

                        // Apply strength falloff
                        float falloff = Mathf.Clamp01(1 - normalizedDistance); // Linear falloff
                        if (!doFallOff)
                            falloff = 1;
                        // float falloff = Mathf.Pow(Mathf.Clamp01(1 - normalizedDistance), 2); // Squared falloff
                        // float falloff = Mathf.Pow(Mathf.Clamp01(1 - normalizedDistance), 3); // Cubic falloff

                        int index = x + y * gridSize + z * gridSize * gridSize;
                        if(strength<0 && voxelData[index].iso >= 0.5)
                            voxelData[index].type = TerrainType.Marble;
                        if (strength > 0)
                        {
                            // remove terrain
                            float oldIso = voxelData[index].iso;
                            voxelData[index].iso += strength * falloff * terrainManager.terrainDictionary[terrainType]; 
                            if (oldIso <= 0.5f && voxelData[index].iso >= 0.5f)
                                terrainManager.inventory[voxelData[index].type]++; 
                            
                        }
                        else
                        {
                            // add terrain
                            float oldIso = voxelData[index].iso;
                            voxelData[index].iso += strength * falloff * terrainManager.terrainDictionary[terrainType]; 
                            if (oldIso >= 0.5f && voxelData[index].iso <= 0.5f)
                                terrainManager.inventory[voxelData[index].type]--; 
                            
                        }
                    }
                }
            }
        }

        // Update Compute Shader Buffers
        int kernelIndex = computeShader.FindKernel("March");
        computeShader.SetBuffer(kernelIndex, "voxelGrid", voxelBuffer);
        computeShader.SetBuffer(kernelIndex, "vertices", verticesBuffer);
        computeShader.SetBuffer(kernelIndex, "counter", counterBuffer);

        voxelBuffer.SetData(voxelData); // Send updated voxel data to GPU
        verticesBuffer.SetCounterValue(0);

        int[] args = { 0, 1, 0, 0 };
        counterBuffer.SetData(args);

        computeShader.Dispatch(kernelIndex, gridSize / 4, gridSize / 4, gridSize / 4);
    }
    
        public void UpdateVoxelGridWithSphereVoxel(Vector3 position, int size, float strength, TerrainType terrainType)
    {
        
        Vector3Int roundedVector = new Vector3Int(
            Mathf.RoundToInt(position.x),
            Mathf.RoundToInt(position.y),
            Mathf.RoundToInt(position.z)
        );

        for (int z = roundedVector.z; z < roundedVector.z + size; z++)
        {
            for (int y = roundedVector.y ; y < roundedVector.y + size; y++)
            {
                for (int x = roundedVector.x; x < roundedVector.x + size; x++)
                {
                    Vector3 voxelPosition = new Vector3(x, y, z);


                        // Apply strength falloff
                        // float falloff = Mathf.Clamp01(1 - normalizedDistance); // Linear falloff
                        // float falloff = Mathf.Pow(Mathf.Clamp01(1 - normalizedDistance), 2); // Squared falloff
                        // float falloff = Mathf.Pow(Mathf.Clamp01(1 - normalizedDistance), 3); // Cubic falloff

                        int index = x + y * gridSize + z * gridSize * gridSize;
                        if(strength<0 && voxelData[index].iso >= 0.5)
                            voxelData[index].type = TerrainType.Marble;
                        if (strength > 0)
                        {
                            // remove terrain
                            float oldIso = voxelData[index].iso;
                            voxelData[index].iso += strength * terrainManager.terrainDictionary[terrainType]; 
                            if (oldIso <= 0.5f && voxelData[index].iso >= 0.5f)
                                terrainManager.inventory[voxelData[index].type]++; 
                            
                        }
                        else
                        {
                            // add terrain
                            float oldIso = voxelData[index].iso;
                            voxelData[index].iso = 0.45f; 
                            if (oldIso >= 0.5f && voxelData[index].iso <= 0.5f)
                                terrainManager.inventory[voxelData[index].type]--; 
                            
                        }
                    
                }
            }
        }

        // Update Compute Shader Buffers
        int kernelIndex = computeShader.FindKernel("March");
        computeShader.SetBuffer(kernelIndex, "voxelGrid", voxelBuffer);
        computeShader.SetBuffer(kernelIndex, "vertices", verticesBuffer);
        computeShader.SetBuffer(kernelIndex, "counter", counterBuffer);

        voxelBuffer.SetData(voxelData); // Send updated voxel data to GPU
        verticesBuffer.SetCounterValue(0);

        int[] args = { 0, 1, 0, 0 };
        counterBuffer.SetData(args);

        computeShader.Dispatch(kernelIndex, gridSize / 4, gridSize / 4, gridSize / 4);
    }
    public void ReadVerticesFromComputeShader()
    {
        int[] args = new int[4];
        counterBuffer.GetData(args);
        int numVertices = args[0];
        // Read back data from the vertices buffer (triangle positions and normals)
        Triangle[] triangles = new Triangle[numVertices];
        verticesBuffer.GetData(triangles, 0, 0, numVertices);

        // Create arrays for positions and normals
        Vector3[] meshPositions = new Vector3[numVertices * 3];
        int triangleIndex = 0;

        // Flatten triangle data into positions and normals
        foreach (var triangle in triangles)
        {
            meshPositions[triangleIndex] = triangle.a.position;
            triangleIndex++;

            meshPositions[triangleIndex] = triangle.b.position;
            triangleIndex++;

            meshPositions[triangleIndex] = triangle.c.position;
            triangleIndex++;
        }
        Mesh mesh = new Mesh();
        // Update the mesh with the new data
        mesh.vertices = meshPositions;

        // Set mesh triangles indices
        int[] meshTriangles = new int[numVertices * 3];
        for (int i = 0; i < numVertices; i++)
        {
            meshTriangles[i * 3] = i * 3;
            meshTriangles[i * 3 + 1] = i * 3 + 1;
            meshTriangles[i * 3 + 2] = i * 3 + 2;
        }
        mesh.triangles = meshTriangles;
        
        meshCollider.sharedMesh = mesh;
    }

    void OnDestroy()
    {
        verticesBuffer.Release();
        counterBuffer.Release();
        voxelBuffer.Release();
    }
    
    

    // Override Equals
    public override bool Equals(object obj)
    {
        ComputeShaderMeshData2 other = (ComputeShaderMeshData2)obj;
        if (obj == null || GetType() != obj.GetType())
            return false;


        // Compare the index field
        return index.Equals(other.index);
    }

    // Override GetHashCode
    public override int GetHashCode()
    {
        return index.GetHashCode();
    }

    struct Triangle
    {
        public Vertex a;
        public Vertex b;
        public Vertex c;

    }
    
    public struct Vertex {
        public Vector3 position;
        public Vector3 normal;
        uint data;
    }

    struct Voxel
    {
        public TerrainType type;
        public float iso;

        public Voxel(TerrainType type, float iso)
        {
            this.type = type;
            this.iso = iso;
        }
    }
}
