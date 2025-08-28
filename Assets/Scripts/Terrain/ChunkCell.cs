using System.Collections.Generic;
using UnityEngine;
using System.Diagnostics; // Stopwatch

/// <summary>
/// Pool-friendly, reusable chunk cell.
/// Heavy work is opt-in via explicit calls (no implicit work in Start/OnEnable).
/// Controller is responsible for staging: density -> mesh -> collider -> foliage.
/// </summary>
public class ChunkCell : MonoBehaviour
{
    // -------------------- CONFIG TYPES --------------------
    [System.Serializable]
    public struct ChunkSettings
    {
        public int gridSize;     // voxels per edge (including padding)
        public int chunkSize;    // world units per edge
        public float isoLevel;
    }

    [System.Serializable]
    public struct FoliageSettings
    {
        public GameObject[] prefabs;
        public float maxSlopeDeg;
        public float targetsPerArea;
        [Range(0f, 360f)] public float yawJitterDeg;
        [Range(0f, 20f)]  public float tiltJitterDeg;
        public float positionJitter;
        public Vector2 uniformScaleRange;
    }

    // -------------------- PUBLIC STATE --------------------
    public Vector3Int Index { get; private set; }
    public ChunkSettings Settings { get; private set; }

    public List<TerrainNoiseProfile> TerrainLayers { get; private set; }

    public ComputeShader MarchingCubesShader { get; private set; }
    public ComputeShader NoiseShader { get; private set; }
    public Material[] MaterialInstances { get; private set; }

    public Transform FoliageParent { get; private set; }

    // -------------------- RUNTIME BUFFERS --------------------
    private Voxel[] _voxelData;                       
    private ComputeBuffer _triangleBuffer;            
    private ComputeBuffer _counterBuffer;             
    private ComputeBuffer _voxelBuffer;               

    // Rendering components
    private MeshRenderer _meshRenderer;
    private MeshFilter   _meshFilter;
    private MeshCollider _meshCollider;

    // Reusable mesh + temp lists
    private Mesh _mesh;
    private readonly List<Vector3> _verts = new();
    private readonly List<Vector3> _norms = new();
    private readonly List<int> _tris = new();
    private readonly List<Vector3> _uvs2 = new(); // type triplet
    private readonly List<Vector3> _uvs3 = new(); // breakingProgress triplet
    private readonly List<Color> _cols = new();

    // Foliage
    private FoliageSettings _foliage;
    private bool _foliageInitialized = false;

    // -------------------- LIFECYCLE (POOL-FRIENDLY) --------------------
    /// <summary>
    /// One-time component setup. Call once after Instantiate (when creating the pool).
    /// </summary>
    public void Initialize(Material[] materials,
                           ComputeShader marchingCubes,
                           ComputeShader noise,
                           List<TerrainNoiseProfile> terrainLayers,
                           FoliageSettings foliageDefaults)
    {
        MaterialInstances = materials;
        MarchingCubesShader = marchingCubes;
        NoiseShader = noise;
        TerrainLayers = terrainLayers;
        _foliage = foliageDefaults;

        gameObject.layer = LayerMask.NameToLayer("Terrain");

        _meshRenderer = gameObject.GetComponent<MeshRenderer>();
        if (_meshRenderer == null) _meshRenderer = gameObject.AddComponent<MeshRenderer>();
        _meshRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.TwoSided;
        _meshRenderer.receiveShadows = true;
        _meshRenderer.materials = MaterialInstances;

        _meshFilter = gameObject.GetComponent<MeshFilter>();
        if (_meshFilter == null) _meshFilter = gameObject.AddComponent<MeshFilter>();

        _meshCollider = gameObject.GetComponent<MeshCollider>();
        if (_meshCollider == null) _meshCollider = gameObject.AddComponent<MeshCollider>();

        if (_mesh == null)
        {
            _mesh = new Mesh();
            _mesh.MarkDynamic();
        }
        _meshFilter.sharedMesh = _mesh;
        _meshCollider.sharedMesh = _mesh;

        if (FoliageParent == null) FoliageParent = transform;

        // start disabled; controller will activate after ResetFor
        gameObject.SetActive(false);
    }

    /// <summary>
    /// Prepare this chunk for a new location/index. Does NOT generate anything yet.
    /// Use this when spawning or reusing from pool.
    /// </summary>
    public void ResetFor(Vector3Int index, Vector3 worldPosition, ChunkSettings settings,
                         FoliageSettings? foliageOverride = null)
    {
        Index = index;
        Settings = settings;
        if (foliageOverride.HasValue) _foliage = foliageOverride.Value;

        transform.position = worldPosition;          // world-space placement

        EnsureBuffersForGridSize(Settings.gridSize);
        ClearMeshAndFoliage();
        if (_meshRenderer) _meshRenderer.enabled = false;

        int voxelGridSize = Settings.gridSize * Settings.gridSize * Settings.gridSize;
        if (_voxelData == null || _voxelData.Length != voxelGridSize)
            _voxelData = new Voxel[voxelGridSize];
        else
            System.Array.Clear(_voxelData, 0, _voxelData.Length); // zero CPU mirror

        // zero GPU buffer once so noise kernel can't accidentally blend with old data
        if (_voxelBuffer != null) _voxelBuffer.SetData(_voxelData);

        _foliageInitialized = false;
    }

    /// <summary>Release GPU buffers and mesh. Call only when destroying the pool altogether.</summary>
    public void DisposeAll()
    {
        ReleaseBuffers();
        if (_meshCollider) _meshCollider.sharedMesh = null;
        if (_mesh) Destroy(_mesh);
        _mesh = null;
    }

    // -------------------- DENSITY GENERATION --------------------
    public void GenerateVoxelsGPU_AllLayers()
    {
        foreach (var layer in TerrainLayers)
            GenerateVoxelsGPU_Layer(layer);

        // Keep a CPU mirror for edits/sampling
        _voxelBuffer.GetData(_voxelData);
    }

    public void GenerateVoxelsGPU_Layer(TerrainNoiseProfile layer)
    {
        int gridSize = Settings.gridSize;
        int kernelIndex = NoiseShader.FindKernel("Density");
        NoiseShader.SetVector("chunkPosition", new Vector4(transform.position.x, transform.position.y, transform.position.z));
        NoiseShader.SetFloat("isoLevel", Settings.isoLevel);
        NoiseShader.SetBuffer(kernelIndex, "voxels", _voxelBuffer);
        NoiseShader.SetInt("numPointsPerAxis", gridSize);
        NoiseShader.SetFloat("chunkSize", Settings.chunkSize);
        NoiseShader.SetVector("offset", new Vector4(layer.offset.x, layer.offset.y, layer.offset.z));
        NoiseShader.SetInt("octaves", layer.octaves);
        NoiseShader.SetFloat("lacunarity", layer.lacunarity);
        NoiseShader.SetFloat("persistence", layer.persistence);
        NoiseShader.SetFloat("noiseScale", layer.noiseScale);
        NoiseShader.SetFloat("noiseWeight", layer.noiseWeight);
        NoiseShader.SetFloat("floorOffset", layer.floorOffset);
        NoiseShader.SetFloat("weightMultiplier", layer.weightMultiplier);
        NoiseShader.SetFloat("hardFloor", layer.hardFloor);
        NoiseShader.SetFloat("hardFloorWeight", layer.hardFloorWeight);
        NoiseShader.SetInt("val", (int)layer.type);

        int groups = Mathf.Max(1, gridSize / 4);
        NoiseShader.Dispatch(kernelIndex, groups, groups, groups);
    }

    public void GenerateVoxelsCPU_Debug()
    {
        int gridSize = Settings.gridSize;
        float amplitude = 2f;
        float frequency = 5f * Mathf.PI / (float)(gridSize - 3);
        float spacing = Settings.chunkSize / (float)(gridSize - 3);

        for (int z = 0; z < gridSize; z++)
        for (int y = 0; y < gridSize; y++)
        for (int x = 0; x < gridSize; x++)
        {
            int idx = x + y * gridSize + z * gridSize * gridSize;
            Vector3 voxelWorldPos = transform.position + new Vector3(x, y, z) * spacing + Vector3.down * 20;
            float baseIso = voxelWorldPos.y - voxelWorldPos.x * 0.5f;
            float sinOffset = Mathf.Sin(voxelWorldPos.x * frequency) * amplitude;
            float isoValue = baseIso + sinOffset;
            TerrainType type = (TerrainType)(isoValue < 0.47f ? (isoValue < 0.40f ? 2 : 1) : 0);
            _voxelData[idx] = new Voxel(type, isoValue, 0);
        }
        _voxelBuffer.SetData(_voxelData);
    }

    // -------------------- QUERY --------------------
    public TerrainType GetTerrainTypeAtLocal(Vector3 localPos)
    {
        if (_voxelData == null || _voxelData.Length == 0) return default;
        float toGrid = (Settings.gridSize - 3f) / Mathf.Max(1f, (float)Settings.chunkSize);
        int x = Mathf.Clamp(Mathf.RoundToInt(localPos.x * toGrid), 0, Settings.gridSize - 1);
        int y = Mathf.Clamp(Mathf.RoundToInt(localPos.y * toGrid), 0, Settings.gridSize - 1);
        int z = Mathf.Clamp(Mathf.RoundToInt(localPos.z * toGrid), 0, Settings.gridSize - 1);
        int idx = x + y * Settings.gridSize + z * Settings.gridSize * Settings.gridSize;
        return _voxelData[idx].type;
    }

    // -------------------- EDITS --------------------
    public Dictionary<TerrainType, int> UpdateVoxelGridWithSphere(
        Vector3 position, float radius, float strength, TerrainType terrainType,
        Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
    {
        return UpdateVoxelGrid(
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

    public Dictionary<TerrainType, int> UpdateVoxelGridWithCube(
        Vector3 center, Vector3 halfExtents, Quaternion rotation, float strength,
        TerrainType terrainType, Dictionary<TerrainType, int> inventory, float breakingProgress = 0, bool doFallOff = true, bool oneBlockOnly = false, bool forceReplace = false)
    {
        return UpdateVoxelGrid(
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

    private Dictionary<TerrainType, int> UpdateVoxelGrid(
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
        Dictionary<TerrainType, int> blocks = new();
        if (_voxelData == null || _voxelData.Length == 0) return blocks;

        bool previewOnly = forceReplace; // preview fast path: only breakingProgress
        int gridSize = Settings.gridSize;

        float SafeDiv(float a, float b) => a / (Mathf.Abs(b) < 1e-6f ? 1e-6f : b);
        float r2 = radiusGrid * radiusGrid;
        Quaternion invRot = shape == BrushShape.Wall ? Quaternion.Inverse(rotation) : Quaternion.identity;

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
                _voxelData[idx].breakingProgress = breakingProgress * falloff;
                continue;
            }

            // REAL edit:
            _voxelData[idx].breakingProgress = breakingProgress * falloff;

            float oldIso = _voxelData[idx].iso;
            TerrainType oldType = _voxelData[idx].type;

            // Build: if coming from air, set type to fill
            if (strength > 0f && oldIso <= 0.5f)
                _voxelData[idx].type = fillType;

            // Break: if oneBlockOnly and type mismatch, skip iso change
            if (!(strength < 0f && oneBlockOnly && oldType != fillType))
            {
                if (_voxelData[idx].type != default)
                    _voxelData[idx].iso = Mathf.Clamp(oldIso + strength * falloff, 0f, 1f);

                // Inventory transitions at iso 0.5
                if (inventory != null)
                {
                    TerrainType type = _voxelData[idx].type;
                    if (strength > 0f)
                    {
                        if (oldIso <= 0.5f && _voxelData[idx].iso >= 0.5f)
                        {
                            if (!inventory.ContainsKey(type)) inventory[type] = 0;
                            inventory[type]--;
                            if (!blocks.ContainsKey(type)) blocks[type] = 0;
                            blocks[type]--;
                        }
                    }
                    else if (strength < 0f)
                    {
                        if (oldIso >= 0.5f && _voxelData[idx].iso <= 0.5f)
                        {
                            if (!inventory.ContainsKey(type)) inventory[type] = 0;
                            inventory[type]++;
                            if (!blocks.ContainsKey(type)) blocks[type] = 0;
                            blocks[type]++;
                        }
                    }
                }
            }
        }

        // Single full upload
        _voxelBuffer.SetData(_voxelData);

        // Remove foliage only on real edits (not preview)
        if (!previewOnly)
        {
            if (shape == BrushShape.Sphere)
                RemoveFoliageInsideSphere(centerGrid, radiusGrid);
            else
                RemoveFoliageInsideBox(centerGrid, halfExtentsGrid, rotation);
        }

        return blocks;
    }

    public void SmoothSphere(Vector3 centerGrid, float radiusGrid, float strength, bool doFallOff = true)
    {
        strength = Mathf.Clamp01(strength);
        if (strength <= 1e-6f || _voxelData == null || _voxelData.Length == 0) return;

        int gs = Settings.gridSize;
        float r = Mathf.Max(0f, radiusGrid);
        float r2 = r * r;

        // Copy iso field so we read from src and write to dst without feedback
        float[] srcIso = new float[_voxelData.Length];
        for (int i = 0; i < _voxelData.Length; i++) srcIso[i] = _voxelData[i].iso;

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

            float avgIso = (accumW > 0) ? (accumIso / accumW) : _voxelData[x + y * gs + z * gs * gs].iso;

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
                float cur = _voxelData[idx].iso;
                _voxelData[idx].iso = Mathf.Lerp(cur, avgIso, t);
            }
        }

        _voxelBuffer.SetData(_voxelData);
    }

    // -------------------- MESH BUILD --------------------
    public void BuildMesh(bool rebuildCollider)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();

        int gridSize = Settings.gridSize;
        int kernelIndex = MarchingCubesShader.FindKernel("March");

        _triangleBuffer.SetCounterValue(0);
        MarchingCubesShader.SetBuffer(kernelIndex, "triangles", _triangleBuffer);
        MarchingCubesShader.SetBuffer(kernelIndex, "voxels", _voxelBuffer);
        MarchingCubesShader.SetInt("numPointsPerAxis", gridSize);
        MarchingCubesShader.SetFloat("isoLevel", Settings.isoLevel);
        MarchingCubesShader.SetFloat("chunkSize", Settings.chunkSize);

        int groups = Mathf.Max(1, gridSize / 4);
        MarchingCubesShader.Dispatch(kernelIndex, groups, groups, groups);

        ComputeBuffer.CopyCount(_triangleBuffer, _counterBuffer, 0);
        int[] triCountArray = { 0 };
        _counterBuffer.GetData(triCountArray);
        int numTris = triCountArray[0];

        // If nothing to draw: keep renderer disabled and collider null
        if (numTris <= 0)
        {
            _verts.Clear(); _norms.Clear(); _tris.Clear(); _uvs2.Clear(); _uvs3.Clear(); _cols.Clear();
            _mesh.Clear(true);
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (_meshCollider) _meshCollider.sharedMesh = null;
            if (_meshRenderer) _meshRenderer.enabled = false;
            return;
        }

        Triangle[] tris = new Triangle[numTris];
        _triangleBuffer.GetData(tris, 0, 0, numTris);

        int vCount = numTris * 3;
        EnsureListCapacity(_verts, vCount);
        EnsureListCapacity(_norms, vCount);
        EnsureListCapacity(_tris, vCount);
        EnsureListCapacity(_uvs2, vCount);
        EnsureListCapacity(_uvs3, vCount);
        EnsureListCapacity(_cols, vCount);

        _verts.Clear(); _norms.Clear(); _tris.Clear(); _uvs2.Clear(); _uvs3.Clear(); _cols.Clear();

        float spawnProbPerVertex = _foliage.targetsPerArea / (gridSize * gridSize);
        float cosMaxSlope = Mathf.Cos(_foliage.maxSlopeDeg * Mathf.Deg2Rad);

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

            if (!_foliageInitialized)
            {
                TrySpawnFoliage(va, spawnProbPerVertex, cosMaxSlope);
                TrySpawnFoliage(vb, spawnProbPerVertex, cosMaxSlope);
                TrySpawnFoliage(vc, spawnProbPerVertex, cosMaxSlope);
            }
        }
        _foliageInitialized = true;

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

        if (_meshRenderer) _meshRenderer.enabled = true;

        if (rebuildCollider && _meshCollider)
        {
            _meshCollider.sharedMesh = null;   // free cooked data first
            _meshCollider.sharedMesh = _mesh;  // recook once
        }
    }

    // -------------------- FOLIAGE --------------------
    private void TrySpawnFoliage(Vertex v, float spawnProbPerVertex, float cosMaxSlope)
    {
        if (_foliage.prefabs == null || _foliage.prefabs.Length == 0) return;
        if (v.data != (int)TerrainType.Grass) return;
        Vector3 n = v.normal.normalized;
        if (Vector3.Dot(n, Vector3.up) < cosMaxSlope) return;

        var worldPos = transform.position + v.position;
        uint seed = Hash(worldPos);
        if (Next01(ref seed) < spawnProbPerVertex)
            SpawnFoliage(worldPos, ref seed);
    }

    private void SpawnFoliage(Vector3 worldPos, ref uint seed)
    {
        if (_foliage.prefabs == null || _foliage.prefabs.Length == 0) return;
        int idx = NextRange(ref seed, 0, _foliage.prefabs.Length);
        var prefab = _foliage.prefabs[idx];

        float jx = (Next01(ref seed) - 0.5f) * 2f * _foliage.positionJitter;
        float jy = (Next01(ref seed) - 0.5f) * 2f * _foliage.positionJitter;
        Vector3 jitteredPos = worldPos + new Vector3(jx, 0f, jy);

        float yaw = (_foliage.yawJitterDeg <= 0f) ? 0f : Next01(ref seed) * _foliage.yawJitterDeg;
        float pitch = (_foliage.tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * _foliage.tiltJitterDeg;
        float roll  = (_foliage.tiltJitterDeg <= 0f) ? 0f : (Next01(ref seed) - 0.5f) * 2f * _foliage.tiltJitterDeg;
        Quaternion rot = Quaternion.Euler(pitch, yaw, roll);

        float sMin = Mathf.Min(_foliage.uniformScaleRange.x, _foliage.uniformScaleRange.y);
        float sMax = Mathf.Max(_foliage.uniformScaleRange.x, _foliage.uniformScaleRange.y);
        float s = sMin + Next01(ref seed) * (sMax - sMin);

        var parent = FoliageParent != null ? FoliageParent : transform;
        var go = Instantiate(prefab, jitteredPos, rot, parent);
        go.transform.localScale = go.transform.localScale * s;
        if (!go.TryGetComponent<FoliageTag>(out _)) go.AddComponent<FoliageTag>();
    }

    private void RemoveFoliageInsideBox(Vector3 centerGrid, Vector3 halfExtentsGrid, Quaternion rotation)
    {
        if (FoliageParent == null) return;
        float scale = Settings.chunkSize / Mathf.Max(1f, (Settings.gridSize - 1f));
        Vector3 worldCenter = transform.position + centerGrid * scale;
        Vector3 worldHalf = halfExtentsGrid * scale;
        Quaternion inv = Quaternion.Inverse(rotation);

        for (int i = FoliageParent.childCount - 1; i >= 0; i--)
        {
            var child = FoliageParent.GetChild(i);
            if (child.GetComponent<FoliageTag>() == null) continue;
            Vector3 local = inv * (child.position - worldCenter);
            Vector3 a = new Vector3(Mathf.Abs(local.x), Mathf.Abs(local.y), Mathf.Abs(local.z));
            if (a.x <= worldHalf.x && a.y <= worldHalf.y && a.z <= worldHalf.z)
                Destroy(child.gameObject);
        }
    }

    private void RemoveFoliageInsideSphere(Vector3 centerGrid, float radiusGrid)
    {
        if (FoliageParent == null) return;
        float scale = Settings.chunkSize / Mathf.Max(1f, (Settings.gridSize - 1f));
        Vector3 worldCenter = transform.position + centerGrid * scale;
        float radiusWorld = radiusGrid * scale;
        float radiusWorldSqr = radiusWorld * radiusWorld;

        for (int i = FoliageParent.childCount - 1; i >= 0; i--)
        {
            var child = FoliageParent.GetChild(i);
            if (child.GetComponent<FoliageTag>() == null) continue;
            if ((child.position - worldCenter).sqrMagnitude <= radiusWorldSqr)
                Destroy(child.gameObject);
        }
    }

    private void ClearMeshAndFoliage()
    {
        if (_mesh != null)
        {
            _mesh.Clear(true); // wipe vertices/indices/layout
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.zero);
            if (_meshCollider) _meshCollider.sharedMesh = null;  // drop any stale cooked collider
        }
        if (FoliageParent != null)
        {
            for (int i = FoliageParent.childCount - 1; i >= 0; i--)
            {
                var child = FoliageParent.GetChild(i);
                if (child.GetComponent<FoliageTag>() != null)
                    Destroy(child.gameObject);
            }
        }
    }

    public void RebuildColliderOnly()
    {
        if (_meshCollider == null) return;

        // If mesh is null or empty, make sure collider is cleared and bail
        if (_mesh == null || _mesh.vertexCount == 0 || _mesh.GetIndexCount(0) == 0)
        {
            if (_meshCollider.sharedMesh != null)
                _meshCollider.sharedMesh = null;
            return;
        }

        // Safe recook
        _meshCollider.sharedMesh = null;
        _meshCollider.sharedMesh = _mesh;
    }

    // -------------------- BUFFERS --------------------
    private void EnsureBuffersForGridSize(int gridSize)
    {
        int voxelGridSize = gridSize * gridSize * gridSize;
        bool needResize = _voxelBuffer == null || _voxelBuffer.count != voxelGridSize;

        if (!needResize) return;
        ReleaseBuffers();

        _triangleBuffer = new ComputeBuffer(voxelGridSize * 5, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Triangle)), ComputeBufferType.Append);
        _counterBuffer  = new ComputeBuffer(1, sizeof(int), ComputeBufferType.Raw);
        _voxelBuffer    = new ComputeBuffer(voxelGridSize, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Voxel)));
    }

    private void ReleaseBuffers()
    {
        if (_triangleBuffer != null) { _triangleBuffer.Release(); _triangleBuffer = null; }
        if (_counterBuffer  != null) { _counterBuffer.Release();  _counterBuffer = null; }
        if (_voxelBuffer    != null) { _voxelBuffer.Release();    _voxelBuffer = null; }
    }

    // -------------------- DATA TYPES --------------------
    private struct Triangle
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

    private struct Voxel
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

    private class FoliageTag : MonoBehaviour {}

    // -------------------- UTILS --------------------
    private static void EnsureListCapacity<T>(List<T> list, int target)
    { if (list.Capacity < target) list.Capacity = target; }

    private static uint Hash(Vector3 p)
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

    private static float Next01(ref uint state)
    { state ^= state << 13; state ^= state >> 17; state ^= state << 5; return (state & 0x00FFFFFF) / 16777216f; }

    private static int NextRange(ref uint state, int minInclusive, int maxExclusive)
    { float r = Next01(ref state); return minInclusive + Mathf.FloorToInt(r * (maxExclusive - minInclusive)); }
    // Add this helper anywhere in the class (e.g., near other public helpers)
    public bool HasRenderableMesh()
    {
        // GetIndexCount(0) is fast and avoids allocating triangles array
        return _mesh != null && _mesh.vertexCount > 0 && _mesh.GetIndexCount(0) > 0;
    }





    // -------------------- ENUMS --------------------
    public enum BrushShape { Sphere, Wall }
}
