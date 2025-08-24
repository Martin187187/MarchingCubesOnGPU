using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics; // Stopwatch

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

    // Single, reusable mesh + reusable temp lists (avoid churn)
    private Mesh _mesh;
    private readonly List<Vector3> _verts = new();
    private readonly List<Vector3> _norms = new();
    private readonly List<int> _tris = new();
    private readonly List<Vector3> _uvs2 = new(); // type triplet
    private readonly List<Vector3> _uvs3 = new(); // breakingProgress triplet
    private readonly List<Color> _cols = new();

    public List<TerrainNoiseProfile> terrainLayers;

    // ---------------- FOLIAGE CONFIG ----------------
    [Header("Foliage")]
    public GameObject[] foliagePrefabs;
    public float foliageMaxSlopeDeg = 25f;
    public float foliageTargetsPerArea = 10f;
    [Range(0f, 360f)] public float yawJitterDeg = 360f;
    [Range(0f, 20f)] public float tiltJitterDeg = 4f;
    public float positionJitter = 0.05f;
    public Vector2 uniformScaleRange = new Vector2(0.9f, 1.1f);
    public Transform foliageParent;

    private bool foliageInitialized = false;

    void Start()
    {
        gameObject.layer = LayerMask.NameToLayer("Terrain");

        meshRenderer = gameObject.AddComponent<MeshRenderer>();
        meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
        meshRenderer.receiveShadows = true;
        meshRenderer.materials = materialInstances;

        meshFilter = gameObject.AddComponent<MeshFilter>();
        meshCollider = gameObject.AddComponent<MeshCollider>();

        foliageParent = transform;

        // Create one dynamic mesh and reuse it forever
        _mesh = new Mesh();
        _mesh.MarkDynamic();
        meshFilter.sharedMesh = _mesh;
        meshCollider.sharedMesh = _mesh;

        InitChunk();
    }

    public void InitChunk()
    {
        int voxelGridSize = gridSize * gridSize * gridSize;
        voxelData = new Voxel[voxelGridSize];

        // (Re)create compute buffers for this grid size
        ReleaseBuffers();

        triangleBuffer = new ComputeBuffer(voxelGridSize * 5, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Triangle)), ComputeBufferType.Append);
        counterBuffer = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        voxelBuffer = new ComputeBuffer(voxelGridSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Voxel)));

        foreach (TerrainNoiseProfile layer in terrainLayers)
            GenerateGPUVoxels(layer);
    }

    void OnDestroy()
    {
        ReleaseBuffers();

        // Explicitly destroy the mesh (native memory), collider will release cooked data
        if (meshCollider) meshCollider.sharedMesh = null;
        if (_mesh) Destroy(_mesh);
    }

    private void ReleaseBuffers()
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

        noiseShader.Dispatch(kernelIndex, Mathf.Max(1, gridSize / 4), Mathf.Max(1, gridSize / 4), Mathf.Max(1, gridSize / 4));
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
                    voxelData[idx] = new Voxel(type, isoValue, 0);
                }
            }
        }

        voxelBuffer.SetData(voxelData);
    }

    public TerrainType GetTerrainTypeAtLocal(Vector3 localPos)
    {
        if (voxelData == null || voxelData.Length == 0) return default;

        float toGrid = (gridSize - 3f) / Mathf.Max(1f, (float)chunkSize);

        int x = Mathf.Clamp(Mathf.RoundToInt(localPos.x * toGrid), 0, gridSize - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(localPos.y * toGrid), 0, gridSize - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt(localPos.z * toGrid), 0, gridSize - 1);

        int idx = x + y * gridSize + z * gridSize * gridSize;
        return voxelData[idx].type;
    }

    // -------------------- EDIT API (SPHERE / CUBE) --------------------

    // Break/build preview should pass forceReplace=true (only writes breakingProgress)
    public void UpdateVoxelGridWithSphere(
        Vector3 position, float radius, float strength, TerrainType terrainType,
        Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
    {
        UpdateVoxelGrid(
            BrushShape.Sphere,
            position,
            radius,
            Vector3.zero,
            Quaternion.identity,
            strength,
            terrainType,
            inventory,
            breakingProgress,
            doFallOff,
            oneBlockOnly,
            forceReplace
        );
    }

    public void UpdateVoxelGridWithCube(
        Vector3 center, Vector3 halfExtents, Quaternion rotation, float strength,
        TerrainType terrainType, Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
    {
        UpdateVoxelGrid(
            BrushShape.Wall,
            center,
            0f,
            halfExtents,
            rotation,
            strength,
            terrainType,
            inventory,
            breakingProgress,
            doFallOff,
            oneBlockOnly,
            forceReplace
        );
    }

    private void UpdateVoxelGrid(
        BrushShape shape,
        Vector3 centerGrid,
        float radiusGrid,
        Vector3 halfExtentsGrid,
        Quaternion rotation,
        float strength,
        TerrainType fillType,
        Dictionary<TerrainType, int> inventory,
        float breakingProgress = 0,
        bool doFallOff = true,
        bool oneBlockOnly = false,
        bool forceReplace = false
    )
    {
        if (voxelData == null || voxelData.Length == 0) return;

        bool previewOnly = forceReplace; // preview fast path: only breakingProgress

        float SafeDiv(float a, float b) => a / (Mathf.Abs(b) < 1e-6f ? 1e-6f : b);

        float r2 = radiusGrid * radiusGrid;
        Quaternion invRot = shape == BrushShape.Wall ? Quaternion.Inverse(rotation) : Quaternion.identity;

        // FULL-GRID LOOPS (reverted)
        for (int z = 0; z < gridSize; z++)
        for (int y = 0; y < gridSize; y++)
        for (int x = 0; x < gridSize; x++)
        {
            Vector3 p = new Vector3(x, y, z);
            int idx = x + y * gridSize + z * gridSize * gridSize;

            bool inside = false;
            float falloff = 1f;

            if (shape == BrushShape.Sphere)
            {
                float d2 = (p - centerGrid).sqrMagnitude;
                if (d2 <= r2)
                {
                    if (doFallOff && radiusGrid > 1e-6f)
                    {
                        float dist = Mathf.Sqrt(d2);
                        falloff = 1f - Mathf.Clamp01(dist / radiusGrid);
                    }
                    inside = true;
                }
            }
            else
            {
                Vector3 local = invRot * (p - centerGrid);
                Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
                if (a.x <= halfExtentsGrid.x && a.y <= halfExtentsGrid.y && a.z <= halfExtentsGrid.z)
                {
                    if (doFallOff)
                    {
                        float nx = SafeDiv(a.x, halfExtentsGrid.x);
                        float ny = SafeDiv(a.y, halfExtentsGrid.y);
                        float nz = SafeDiv(a.z, halfExtentsGrid.z);
                        float normalized = Mathf.Clamp01(Mathf.Max(nx, Mathf.Max(ny, nz)));
                        falloff = 1f - normalized;
                    }
                    inside = true;
                }
            }

            if (!inside) continue;

            if (previewOnly)
            {
                voxelData[idx].breakingProgress = breakingProgress * falloff;
                continue;
            }

            // REAL edit:
            voxelData[idx].breakingProgress = breakingProgress * falloff;

            float oldIso = voxelData[idx].iso;
            TerrainType oldType = voxelData[idx].type;

            // Build: if coming from air, set type to fill
            if (strength > 0f && oldIso <= 0.5f)
                voxelData[idx].type = fillType;

            // Break: if oneBlockOnly and type mismatch, skip iso change
            if (!(strength < 0f && oneBlockOnly && oldType != fillType))
            {
                if (voxelData[idx].type != default)
                    voxelData[idx].iso = Mathf.Clamp(oldIso + strength * falloff, 0f, 1f);

                // Inventory transitions at iso 0.5
                if (inventory != null)
                {
                    if (strength > 0f)
                    {
                        if (oldIso <= 0.5f && voxelData[idx].iso >= 0.5f)
                        {
                            if (!inventory.ContainsKey(voxelData[idx].type)) inventory[voxelData[idx].type] = 0;
                            inventory[voxelData[idx].type]--;
                        }
                    }
                    else if (strength < 0f)
                    {
                        if (oldIso >= 0.5f && voxelData[idx].iso <= 0.5f)
                        {
                            if (!inventory.ContainsKey(voxelData[idx].type)) inventory[voxelData[idx].type] = 0;
                            inventory[voxelData[idx].type]++;
                        }
                    }
                }
            }
        }

        // Single full upload (reverted)
        voxelBuffer.SetData(voxelData);

        // Remove foliage only on real edits (not preview)
        if (!previewOnly)
        {
            if (shape == BrushShape.Sphere)
                RemoveFoliageInsideSphere(centerGrid, radiusGrid);
            else
                RemoveFoliageInsideBox(centerGrid, halfExtentsGrid, rotation);
        }
    }

    // ---- SMOOTHING (sphere) ----  (full-grid upload)
    public void SmoothSphere(Vector3 centerGrid, float radiusGrid, float strength, bool doFallOff = true)
    {
        strength = Mathf.Clamp01(strength);
        if (strength <= 1e-6f || voxelData == null || voxelData.Length == 0) return;

        int gs = gridSize;
        float r = Mathf.Max(0f, radiusGrid);
        float r2 = r * r;

        // Copy iso field so we read from src and write to dst without feedback
        float[] srcIso = new float[voxelData.Length];
        for (int i = 0; i < voxelData.Length; i++) srcIso[i] = voxelData[i].iso;

        int[] w1 = { 1, 2, 1 }; // separable weights

        for (int z = 0; z < gs; z++)
        for (int y = 0; y < gs; y++)
        for (int x = 0; x < gs; x++)
        {
            Vector3 p = new Vector3(x, y, z);
            float dist2 = (p - centerGrid).sqrMagnitude;
            if (dist2 > r2) continue;

            int accumW = 0;
            float accumIso = 0f;

            for (int dz = -1; dz <= 1; dz++)
            {
                int zz = Mathf.Clamp(z + dz, 0, gs - 1);
                int wz = w1[dz + 1];

                for (int dy = -1; dy <= 1; dy++)
                {
                    int yy = Mathf.Clamp(y + dy, 0, gs - 1);
                    int wy = w1[dy + 1];

                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int xx = Mathf.Clamp(x + dx, 0, gs - 1);
                        int wx = w1[dx + 1];

                        int w = wx * wy * wz;
                        int nIdx = xx + yy * gs + zz * gs * gs;

                        accumW += w;
                        accumIso += srcIso[nIdx] * w;
                    }
                }
            }

            float avgIso = (accumW > 0) ? (accumIso / accumW) : voxelData[x + y * gs + z * gs * gs].iso;

            float t = strength;
            if (doFallOff && r > 1e-6f)
            {
                float dist = Mathf.Sqrt(dist2);
                float normalized = Mathf.Clamp01(dist / r);
                float fall = 1f - normalized;
                t *= fall;
            }

            if (t > 1e-6f)
            {
                int idx = x + y * gs + z * gs * gs;
                float cur = voxelData[idx].iso;
                voxelData[idx].iso = Mathf.Lerp(cur, avgIso, t);
            }
        }

        // Single full upload (reverted)
        voxelBuffer.SetData(voxelData);
    }

    // -------------------- READ + BUILD MESH + (OPTIONAL) FOLIAGE --------------------
    // Default: rebuild collider (keeps your existing calls working)
    public void ReadVerticesFromComputeShader() => ReadVerticesFromComputeShader(true);

    // Pass rebuildCollider=false during preview frames to avoid PhysX allocations.
    public void ReadVerticesFromComputeShader(bool rebuildCollider)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        int kernelIndex = marchingCubesShader.FindKernel("March");

        triangleBuffer.SetCounterValue(0);
        marchingCubesShader.SetBuffer(kernelIndex, "triangles", triangleBuffer);
        marchingCubesShader.SetBuffer(kernelIndex, "voxels", voxelBuffer);
        marchingCubesShader.SetInt("numPointsPerAxis", gridSize);
        marchingCubesShader.SetFloat("isoLevel", isoLevel);
        marchingCubesShader.SetFloat("chunkSize", chunkSize);

        marchingCubesShader.Dispatch(kernelIndex, Mathf.Max(1, gridSize / 4), Mathf.Max(1, gridSize / 4), Mathf.Max(1, gridSize / 4));

        ComputeBuffer.CopyCount(triangleBuffer, counterBuffer, 0);
        int[] triCountArray = { 0 };
        counterBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        Triangle[] tris = new Triangle[numTris];
        triangleBuffer.GetData(tris, 0, 0, numTris);

        int vCount = numTris * 3;
        EnsureListCapacity(_verts, vCount);
        EnsureListCapacity(_norms, vCount);
        EnsureListCapacity(_tris, vCount);
        EnsureListCapacity(_uvs2, vCount);
        EnsureListCapacity(_uvs3, vCount);
        EnsureListCapacity(_cols, vCount);

        _verts.Clear(); _norms.Clear(); _tris.Clear(); _uvs2.Clear(); _uvs3.Clear(); _cols.Clear();

        float spawnProbPerVertex = foliageTargetsPerArea / (gridSize * gridSize);
        float cosMaxSlope = Mathf.Cos(foliageMaxSlopeDeg * Mathf.Deg2Rad);

        for (int i = 0; i < numTris; i++)
        {
            int baseIndex = _verts.Count;

            var va = tris[i].a;
            var vb = tris[i].b;
            var vc = tris[i].c;

            _verts.Add(va.position); _norms.Add(va.normal);
            _verts.Add(vb.position); _norms.Add(vb.normal);
            _verts.Add(vc.position); _norms.Add(vc.normal);

            _uvs2.Add(new Vector3(va.data, vb.data, vc.data));
            _uvs2.Add(new Vector3(va.data, vb.data, vc.data));
            _uvs2.Add(new Vector3(va.data, vb.data, vc.data));

            _uvs3.Add(new Vector3(va.breakingProgress, vb.breakingProgress, vc.breakingProgress));
            _uvs3.Add(new Vector3(va.breakingProgress, vb.breakingProgress, vc.breakingProgress));
            _uvs3.Add(new Vector3(va.breakingProgress, vb.breakingProgress, vc.breakingProgress));

            _cols.Add(new Color(1f, 0f, 0f));
            _cols.Add(new Color(0f, 1f, 0f));
            _cols.Add(new Color(0f, 0f, 1f));

            _tris.Add(baseIndex + 0);
            _tris.Add(baseIndex + 1);
            _tris.Add(baseIndex + 2);

            if (!foliageInitialized)
            {
                TrySpawnFoliage(va, spawnProbPerVertex, cosMaxSlope);
                TrySpawnFoliage(vb, spawnProbPerVertex, cosMaxSlope);
                TrySpawnFoliage(vc, spawnProbPerVertex, cosMaxSlope);
            }
        }
        foliageInitialized = true;

        _mesh.Clear(false);
        _mesh.indexFormat = (vCount > 65535)
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        _mesh.SetVertices(_verts);
        _mesh.SetNormals(_norms);
        _mesh.SetTriangles(_tris, 0, true);
        _mesh.SetColors(_cols);
        _mesh.SetUVs(2, _uvs2);
        _mesh.SetUVs(3, _uvs3);
        _mesh.RecalculateBounds();

        if (rebuildCollider)
        {
            meshCollider.sharedMesh = null;   // free cooked data first
            meshCollider.sharedMesh = _mesh;  // recook once
        }
    }

    private void TrySpawnFoliage(Vertex v, float spawnProbPerVertex, float cosMaxSlope)
    {
        if (foliagePrefabs == null || foliagePrefabs.Length == 0) return;
        if (v.data != (int)TerrainType.Grass) return;

        Vector3 n = v.normal.normalized;
        if (Vector3.Dot(n, Vector3.up) < cosMaxSlope) return;

        var worldPos = transform.position + v.position;
        uint seed = Hash(worldPos);
        if (Next01(ref seed) < spawnProbPerVertex)
        {
            SpawnFoliage(worldPos, ref seed);
        }
    }

    // -------------------- FOLIAGE HELPERS --------------------
    private void SpawnFoliage(Vector3 worldPos, ref uint seed)
    {
        if (foliagePrefabs == null || foliagePrefabs.Length == 0) return;

        int idx = NextRange(ref seed, 0, foliagePrefabs.Length);
        var prefab = foliagePrefabs[idx];

        float jx = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
        float jy = (Next01(ref seed) - 0.5f) * 2f * positionJitter;
        Vector3 jitteredPos = worldPos + new Vector3(jx, 0f, jy);

        float yaw = (yawJitterDeg <= 0f) ? 0f : Next01(ref seed) * yawJitterDeg;
        float pitch = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
        float roll = (tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * tiltJitterDeg;
        Quaternion rot = Quaternion.Euler(pitch, yaw, roll);

        float sMin = Mathf.Min(uniformScaleRange.x, uniformScaleRange.y);
        float sMax = Mathf.Max(uniformScaleRange.x, uniformScaleRange.y);
        float s = sMin + Next01(ref seed) * (sMax - sMin);

        var parent = foliageParent != null ? foliageParent : transform;
        var go = Instantiate(prefab, jitteredPos, rot, parent);
        go.transform.localScale = go.transform.localScale * s;

        if (!go.TryGetComponent<FoliageTag>(out _))
            go.AddComponent<FoliageTag>();
    }

    private void RemoveFoliageInsideBox(Vector3 centerGrid, Vector3 halfExtentsGrid, Quaternion rotation)
    {
        if (foliageParent == null) return;

        float scale = chunkSize / Mathf.Max(1f, (gridSize - 1f));
        Vector3 worldCenter = transform.position + centerGrid * scale;
        Vector3 worldHalf = halfExtentsGrid * scale;

        Quaternion inv = Quaternion.Inverse(rotation);

        for (int i = foliageParent.childCount - 1; i >= 0; i--)
        {
            var child = foliageParent.GetChild(i);
            if (child.GetComponent<FoliageTag>() == null) continue;

            Vector3 local = inv * (child.position - worldCenter);
            Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));

            if (a.x <= worldHalf.x && a.y <= worldHalf.y && a.z <= worldHalf.z)
            {
                Destroy(child.gameObject);
            }
        }
    }

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

    // marker so we know what to delete
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
        public float breakingProgress;
    }

    struct Voxel
    {
        public TerrainType type;
        public float iso;
        public float breakingProgress;

        public Voxel(TerrainType type, float iso, float breakingProgress)
        {
            this.type = type;
            this.iso = iso;
            this.breakingProgress = breakingProgress;
        }
    }

    // -------------------- UTILS --------------------
    static void EnsureListCapacity<T>(List<T> list, int target)
    {
        if (list.Capacity < target) list.Capacity = target;
    }
}
