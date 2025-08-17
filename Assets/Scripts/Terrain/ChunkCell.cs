using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using System.Diagnostics; // Stopwatch
// If you want a short alias: using Stopwatch = System.Diagnostics.Stopwatch;

public class ChunkCell : MonoBehaviour
{
    public Vector3Int index;
    public int gridSize = 10;
    public int chunkSize = 16;
    public float isoLevel = 0.5f;

    private Voxel[] voxelData;

    public ComputeShader marchingCubesShader;
    public ComputeShader noiseShader;

    private ComputeBuffer triangleBuffer;
    private ComputeBuffer counterBuffer;
    private ComputeBuffer voxelBuffer;

    public Material[] materialInstances;
    private MeshRenderer meshRenderer;
    private MeshFilter meshFilter;
    private MeshCollider meshCollider;

    public List<TerrainNoiseProfile> terrainLayers;

    // ---------------- FOLIAGE CONFIG ----------------
    [Header("Foliage")]
    public GameObject[] foliagePrefabs;          // Assign one or more prefabs
    public float foliageMaxSlopeDeg = 25f;       // Max slope for spawning (0 = only perfectly up; 90 = any)
    public float foliageTargetsPerArea = 10f;    // Expected spawns per (gridSize * gridSize) per chunk
    [Range(0f, 360f)] public float yawJitterDeg = 360f;   // full spin randomness (set smaller if you want less)
    [Range(0f, 20f)] public float tiltJitterDeg = 4f;    // small pitch/roll wobble
    public float positionJitter = 0.05f;                 // world-space XY jitter (meters)
    public Vector2 uniformScaleRange = new Vector2(0.9f, 1.1f); // min..max
    public Transform foliageParent;              // Optional parent for hierarchy
    
    private bool foliageInitialized = false;

    void Start()
    {
        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
        meshRenderer.receiveShadows = true;
        meshRenderer.materials = materialInstances;

        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshCollider = gameObject.AddComponent<MeshCollider>();
        foliageParent = transform;
        InitChunk();
    }

    public void InitChunk()
    {
        int voxelGridSize = gridSize * gridSize * gridSize;
        voxelData = new Voxel[voxelGridSize];

        if (triangleBuffer != null) triangleBuffer.Release();
        if (counterBuffer != null) counterBuffer.Release();
        if (voxelBuffer != null) voxelBuffer.Release();

        triangleBuffer = new ComputeBuffer(voxelGridSize * 5, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Triangle)), ComputeBufferType.Append);
        counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        voxelBuffer = new ComputeBuffer(voxelGridSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Voxel)));

        foreach (TerrainNoiseProfile layer in terrainLayers)
            GenerateGPUVoxels(layer);
    }

    void OnDestroy()
    {
        if (triangleBuffer != null) { triangleBuffer.Release(); triangleBuffer = null; }
        if (counterBuffer != null) { counterBuffer.Release(); counterBuffer = null; }
        if (voxelBuffer != null) { voxelBuffer.Release(); voxelBuffer = null; }
    }

    public void GenerateGPUVoxels(TerrainNoiseProfile layer)
    {
        int kernelIndex = noiseShader.FindKernel("Density");
        noiseShader.SetVector("chunkPosition", new Vector4(transform.position.x, transform.position.y, transform.position.z));
        noiseShader.SetFloat("isoLevel", isoLevel);
        noiseShader.SetBuffer(kernelIndex, "voxels", voxelBuffer);
        noiseShader.SetInt("numPointsPerAxis", gridSize);
        noiseShader.SetFloat("chunkSize", chunkSize);
        noiseShader.SetVector("offset", new Vector4(layer.offset.x, layer.offset.y, layer.offset.z));
        noiseShader.SetInt("octaves", layer.octaves);
        noiseShader.SetFloat("lacunarity", layer.lacunarity);
        noiseShader.SetFloat("persistence", layer.persistence);
        noiseShader.SetFloat("noiseScale", layer.noiseScale);
        noiseShader.SetFloat("noiseWeight", layer.noiseWeight);
        noiseShader.SetFloat("floorOffset", layer.floorOffset);
        noiseShader.SetFloat("weightMultiplier", layer.weightMultiplier);
        noiseShader.SetFloat("hardFloor", layer.hardFloor);
        noiseShader.SetFloat("hardFloorWeight", layer.hardFloorWeight);
        noiseShader.SetInt("val", (int)layer.type);

        noiseShader.Dispatch(0, gridSize / 4, gridSize / 4, gridSize / 4);
        voxelBuffer.GetData(voxelData);
    }

    public void GenerateCPUVoxels()
    {
        float amplitude = 2f;
        float frequency = 5f * Mathf.PI / (float)(gridSize - 3);
        float spacing = chunkSize / (float)(gridSize - 3);

        for (int z = 0; z < gridSize; z++)
        {
            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    int idx = x + y * gridSize + z * gridSize * gridSize;

                    Vector3 voxelWorldPos = transform.position + new Vector3(x, y, z) * spacing + Vector3.down * 20;
                    float baseIso = voxelWorldPos.y - voxelWorldPos.x * 0.5f;
                    float sinOffset = Mathf.Sin(voxelWorldPos.x * frequency) * amplitude;
                    float isoValue = baseIso + sinOffset;

                    TerrainType type = (TerrainType)(isoValue < 0.47f ? (isoValue < 0.40f ? 2 : 1) : 0);
                    voxelData[idx] = new Voxel(type, isoValue);
                }
            }
        }

        voxelBuffer.SetData(voxelData);
    }

    public void UpdateVoxelGridWithSphere(
        Vector3 position, float radius, float strength, TerrainType terrainType,
        Dictionary<TerrainType, int> inventory, bool doFallOff = true)
    {
        float radiusSqr = radius * radius;

        for (int z = 0; z < gridSize; z++)
        for (int y = 0; y < gridSize; y++)
        for (int x = 0; x < gridSize; x++)
        {
            Vector3 voxelPosition = new Vector3(x, y, z);
            float sqrDistance = (voxelPosition - position).sqrMagnitude;
            if (sqrDistance <= radiusSqr)
            {
                float distance = Mathf.Sqrt(sqrDistance);
                float normalizedDistance = distance / radius;
                float falloff = Mathf.Clamp01(1 - normalizedDistance);
                if (!doFallOff) falloff = 1f;

                int idx = x + y * gridSize + z * gridSize * gridSize;

                if (strength < 0 && voxelData[idx].iso <= 0.5f)
                    voxelData[idx].type = TerrainType.Beton;

                if (strength > 0)
                {
                    float oldIso = voxelData[idx].iso;
                    voxelData[idx].iso += strength * falloff;
                    if (oldIso <= 0.5f && voxelData[idx].iso >= 0.5f)
                        inventory[voxelData[idx].type]--;
                }
                else
                {
                    float oldIso = voxelData[idx].iso;
                    voxelData[idx].iso += strength * falloff;
                    if (oldIso >= 0.5f && voxelData[idx].iso <= 0.5f)
                        inventory[voxelData[idx].type]++;
                }
            }
        }

        voxelBuffer.SetData(voxelData);

        // <<< ADDED >>> remove foliage in the carved sphere
        RemoveFoliageInsideSphere(position, radius);
    }

    // -------------------- READ + BUILD MESH + (OPTIONAL) FOLIAGE --------------------
    public void ReadVerticesFromComputeShader()
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        long tStart = stopwatch.ElapsedTicks;

        int kernelIndex = marchingCubesShader.FindKernel("March");
        long tAfterFindKernel = stopwatch.ElapsedTicks;

        triangleBuffer.SetCounterValue(0);
        marchingCubesShader.SetBuffer(kernelIndex, "triangles", triangleBuffer);
        marchingCubesShader.SetBuffer(kernelIndex, "voxels", voxelBuffer);
        marchingCubesShader.SetInt("numPointsPerAxis", gridSize);
        marchingCubesShader.SetFloat("isoLevel", isoLevel);
        marchingCubesShader.SetFloat("chunkSize", chunkSize);

        long tBeforeDispatch = stopwatch.ElapsedTicks;
        marchingCubesShader.Dispatch(kernelIndex, gridSize / 4, gridSize / 4, gridSize / 4);
        long tAfterDispatch = stopwatch.ElapsedTicks;

        ComputeBuffer.CopyCount(triangleBuffer, counterBuffer, 0);
        int[] triCountArray = { 0 };
        counterBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        long tAfterCount = stopwatch.ElapsedTicks;

        Triangle[] tris = new Triangle[numTris];
        triangleBuffer.GetData(tris, 0, 0, numTris);

        long tAfterGetTriangles = stopwatch.ElapsedTicks;

        var vertices = new Vector3[numTris * 3];
        var normals = new Vector3[numTris * 3];
        var meshTriangles = new int[numTris * 3];
        var uvs1 = new Vector3[numTris * 3];
        var colors = new Color[numTris * 3];

        int[] type = new int[3];
        Color[] blend = new Color[]
        {
            new Color(1f, 0f, 0f),
            new Color(0f, 1f, 0f),
            new Color(0f, 0f, 1f)
        };

        // ---- FOLIAGE PROBABILITY + ANGLE GATE ----
        float spawnProbPerVertex = foliageTargetsPerArea / (gridSize * gridSize);
        float cosMaxSlope = Mathf.Cos(foliageMaxSlopeDeg * Mathf.Deg2Rad);

        for (int i = 0; i < numTris; i++)
        {
            for (int j = 0; j < 3; j++)
            {
                var vert = tris[i].GetVertex(j);
                int vi = i * 3 + j;

                meshTriangles[vi] = vi;
                vertices[vi] = vert.position;
                normals[vi] = vert.normal;

                type[j] = vert.data;

                if (!foliageInitialized && vert.data == (int)TerrainType.Grass)
                {
                    Vector3 n = vert.normal.normalized;
                    if (Vector3.Dot(n, Vector3.up) >= cosMaxSlope)
                    {
                        var worldPos = transform.position + vert.position;
                        uint seed = Hash(worldPos);
                        if (Next01(ref seed) < spawnProbPerVertex)
                        {
                            SpawnFoliage(worldPos, ref seed);
                        }
                    }
                }
            }

            for (int j = 0; j < 3; j++)
            {
                int vi = i * 3 + j;
                uvs1[vi] = new Vector3(type[0], type[1], type[2]);
                colors[vi] = blend[j];
            }
        }
        foliageInitialized = true;

        long tAfterLoop = stopwatch.ElapsedTicks;

        Mesh mesh = new Mesh();
        mesh.indexFormat = (vertices.Length > 65535)
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;

        mesh.vertices = vertices;
        mesh.normals = normals;
        mesh.triangles = meshTriangles;
        mesh.colors = colors;
        mesh.SetUVs(2, new System.Collections.Generic.List<Vector3>(uvs1));

        meshFilter.mesh = mesh;
        meshCollider.sharedMesh = mesh;

        long tEnd = stopwatch.ElapsedTicks;
    }

    // -------------------- FOLIAGE HELPER --------------------
    private void SpawnFoliage(Vector3 worldPos, ref uint seed)
    {
        if (foliagePrefabs == null || foliagePrefabs.Length == 0) return;

        int idx = NextRange(ref seed, 0, foliagePrefabs.Length);
        var prefab = foliagePrefabs[idx];

        // --- Position jitter (XY plane, keep Z up) ---
        // small, deterministic offset so spawns don't look like a perfect grid
        float jx = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
        float jy = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
        Vector3 jitteredPos = worldPos + new Vector3(jx, 0f, jy);

        // --- Rotation jitter ---
        // Yaw: full spin by default (tunable), Tilt: gentle random lean
        float yaw  = (yawJitterDeg <= 0f)  ? 0f : Next01(ref seed) * yawJitterDeg;
        float pitch = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
        float roll  = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
        Quaternion rot = Quaternion.Euler(pitch, yaw, roll);

        // --- Scale jitter (uniform) ---
        float sMin = Mathf.Min(uniformScaleRange.x, uniformScaleRange.y);
        float sMax = Mathf.Max(uniformScaleRange.x, uniformScaleRange.y);
        float s = sMin + Next01(ref seed) * (sMax - sMin);

        var parent = foliageParent != null ? foliageParent : transform;
        var go = Instantiate(prefab, jitteredPos, rot, parent);

        // Apply uniform scale jitter on top of prefab scale
        go.transform.localScale = go.transform.localScale * s;

        // Keep your tag so we can remove these later if needed
        if (!go.TryGetComponent<FoliageTag>(out _))
            go.AddComponent<FoliageTag>();
    }


    // <<< ADDED >>> remove foliage inside edit sphere
    private void RemoveFoliageInsideSphere(Vector3 centerGrid, float radiusGrid)
    {
        if (foliageParent == null) return;

        float scale = chunkSize / Mathf.Max(1f, (gridSize - 1f));
        Vector3 worldCenter = transform.position + centerGrid * scale;
        float radiusWorld = radiusGrid * scale;
        float radiusWorldSqr = radiusWorld * radiusWorld;

        for (int i = foliageParent.childCount - 1; i >= 0; i--)
        {
            var child = foliageParent.GetChild(i);
            if (child.GetComponent<FoliageTag>() == null) continue;

            if ((child.position - worldCenter).sqrMagnitude <= radiusWorldSqr)
            {
                Destroy(child.gameObject);
            }
        }
    }

    // <<< ADDED >>> marker so we know what to delete
    private class FoliageTag : MonoBehaviour {}

    // ---------- DETERMINISTIC HASH + PRNG ----------
    static uint Hash(Vector3 p)
    {
        const float quant = 0.25f;
        int xi = Mathf.FloorToInt(p.x / quant);
        int yi = Mathf.FloorToInt(p.y / quant);
        int zi = Mathf.FloorToInt(p.z / quant);

        uint h = 2166136261u;
        unchecked
        {
            h = (h ^ (uint)xi) * 16777619u;
            h = (h ^ (uint)yi) * 16777619u;
            h = (h ^ (uint)zi) * 16777619u;
            h ^= h >> 16; h *= 0x7feb352d; h ^= h >> 15; h *= 0x846ca68b; h ^= h >> 16;
        }
        return h;
    }

    static float Next01(ref uint state)
    {
        state ^= state << 13; state ^= state >> 17; state ^= state << 5;
        return (state & 0x00FFFFFF) / 16777216f;
    }

    static int NextRange(ref uint state, int minInclusive, int maxExclusive)
    {
        float r = Next01(ref state);
        return minInclusive + Mathf.FloorToInt(r * (maxExclusive - minInclusive));
    }

    // -------------------- STRUCTS --------------------
    struct Triangle
    {
        public Vertex a;
        public Vertex b;
        public Vertex c;

        public Vertex GetVertex(int index)
        {
            return index switch
            {
                0 => a,
                1 => b,
                2 => c,
                _ => throw new System.IndexOutOfRangeException("Triangle only has 3 vertices: 0, 1, 2")
            };
        }
    }

    public struct Vertex
    {
        public Vector3 position;
        public Vector3 normal;
        public int data; // terrain type id
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
