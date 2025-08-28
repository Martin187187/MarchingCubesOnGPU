using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Streams poolable ChunkCell instances around the player.
/// Owns voxel edit ops, chunk loading/unloading, and staged generation (density -> mesh).
/// Heavy work is budgeted to avoid frame spikes.
/// Mesh rebuilds triggered by edits have HIGH priority over initial chunk builds.
/// </summary>
public class TerrainController : MonoBehaviour
{
    // -------------------- Assets / Shaders --------------------
    [Header("Rendering / Shaders")]
    public Material[] materialInstances;
    public ComputeShader marchingCubesShader;
    public ComputeShader noiseShader;

    [Header("Terrain Generation")]
    public List<TerrainNoiseProfile> terrainLayers;

    // -------------------- World / Grid --------------------
    [Header("Grid Settings")]
    [Tooltip("Voxels per chunk edge (includes padding). Typical: 32..64")]
    public int gridSize = 32;
    [Tooltip("World units per chunk edge.")]
    public int chunkSize = 16;
    [Range(0f,1f)] public float isoLevel = 0.5f;

    // -------------------- Streaming --------------------
    [Header("Streaming (in chunks)")]
    public Transform player;
    [Tooltip("Horizontal render radius (in chunks).")]
    public int viewRadiusChunks = 6;
    [Tooltip("Vertical radius (in chunks).")]
    public int verticalRadiusChunks = 2;
    [Tooltip("Extra shell kept loaded to reduce thrashing.")]
    public int unloadHysteresis = 1;
    [Tooltip("Physics colliders only within this radius (<= viewRadiusChunks).")]
    public int colliderRadiusChunks = 3;
    [Tooltip("How often to recompute the wanted set (s).")]
    public float wantedUpdateInterval = 0.25f;

    // -------------------- Budgeting --------------------
    [Header("Budgets (per frame)")]
    public int budgetDensityPerFrame = 2;
    public int budgetMeshPerFrame = 1;
    public int budgetColliderPromotionsPerFrame = 2;

    // -------------------- Pool --------------------
    [Header("Pool")]
    public int prewarmChunks = 64;
    public int maxChunks = 512;

    // -------------------- Foliage --------------------
    [Header("Foliage")]
    public GameObject[] foliagePrefabs;
    public float foliageMaxSlopeDeg = 25f;
    public float foliageTargetsPerArea = 10f;
    [Range(0f, 360f)] public float yawJitterDeg = 360f;
    [Range(0f, 20f)]  public float tiltJitterDeg = 4f;
    public float positionJitter = 0.05f;
    public Vector2 uniformScaleRange = new(0.9f, 1.1f);

    // -------------------- Inventory --------------------
    public Dictionary<TerrainType, int> inventory = new()
    {
        { TerrainType.Grass, 0 }, { TerrainType.Dirt, 0 }, { TerrainType.Rock, 0 },
        { TerrainType.CrackedRock, 0 }, { TerrainType.IronOre, 0 }, { TerrainType.Beton, 0 }, { TerrainType.Coal, 0 },
    };

    // -------------------- Internals --------------------
    private Vector3 _origin; // ALL conversions relative to this (transform.position)
    private ChunkCell.ChunkSettings _chunkSettings;
    private ChunkCell.FoliageSettings _foliageSettings;

    // Pool
    private readonly Queue<ChunkCell> _pool = new();
    private int _totalChunkObjects = 0;

    // Loaded set
    private readonly Dictionary<Vector3Int, ChunkRuntime> _loaded = new();

    // Stage queues
    private readonly Queue<ChunkRuntime> _densityQueue = new();

    // 🔸 Mesh queues split into HIGH (edits) and NORMAL (init/loads)
    private readonly Queue<ChunkRuntime> _meshQueueHigh = new();
    private readonly Queue<ChunkRuntime> _meshQueueNormal = new();

    private float _nextWantedTime = 0f;

    public TerrainType terrainType = default;

    private class ChunkRuntime
    {
        public Vector3Int coord;
        public ChunkCell cell;
        public Stage stage;
        public bool colliderCooked; // true if the collider is cooked for the current mesh
    }
    private enum Stage { Allocated, DensityReady, MeshReady, Ready }

    // ----------------------------------------------------------------------
    void Awake()
    {
        _origin = transform.position; // establish origin
        if (!player) player = Camera.main ? Camera.main.transform : null;

        _chunkSettings = new ChunkCell.ChunkSettings
        {
            gridSize = gridSize,
            chunkSize = chunkSize,
            isoLevel = isoLevel
        };

        _foliageSettings = new ChunkCell.FoliageSettings
        {
            prefabs = foliagePrefabs,
            maxSlopeDeg = foliageMaxSlopeDeg,
            targetsPerArea = foliageTargetsPerArea,
            yawJitterDeg = yawJitterDeg,
            tiltJitterDeg = tiltJitterDeg,
            positionJitter = positionJitter,
            uniformScaleRange = uniformScaleRange
        };

        PrewarmPool();
    }

    void OnDestroy()
    {
        foreach (var rt in _loaded.Values)
        {
            if (rt?.cell)
            {
                rt.cell.DisposeAll();
                Destroy(rt.cell.gameObject);
            }
        }
        while (_pool.Count > 0)
        {
            var c = _pool.Dequeue();
            if (c) { c.DisposeAll(); Destroy(c.gameObject); }
        }
        _totalChunkObjects = 0;
    }

    void Update()
    {
        if (player && Time.time >= _nextWantedTime)
        {
            UpdateWantedSet();
            _nextWantedTime = Time.time + wantedUpdateInterval;
        }

        RunDensityStage(budgetDensityPerFrame);
        RunMeshStage(budgetMeshPerFrame);
        PromoteCollidersAroundPlayer(budgetColliderPromotionsPerFrame);

        // optional sampling for your tool
        var cam = Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, Mathf.Infinity))
            {
                float voxelSizeWorld = chunkSize / (gridSize - 3f);
                var debugSamplePos = hit.point - hit.normal.normalized * voxelSizeWorld * 0.7f;
                terrainType = GetTerrainTypeAtWorld(debugSamplePos);
            }
        }
    }

    // ----------------------------------------------------------------------
    #region Streaming

    private void UpdateWantedSet()
    {
        if (!player) return;

        Vector3Int pc = WorldToChunkCoord(player.position);

        var wanted = new List<Vector3Int>(EstimateWantedCount());
        for (int dz = -viewRadiusChunks; dz <= viewRadiusChunks; dz++)
        for (int dx = -viewRadiusChunks; dx <= viewRadiusChunks; dx++)
        for (int dy = -verticalRadiusChunks; dy <= verticalRadiusChunks; dy++)
        {
            int distXZ = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            if (distXZ <= viewRadiusChunks)
                wanted.Add(new Vector3Int(pc.x + dx, pc.y + dy, pc.z + dz));
        }

        wanted.Sort((a, b) =>
        {
            int da = Chebyshev2D(a - pc);
            int db = Chebyshev2D(b - pc);
            return da.CompareTo(db);
        });

        foreach (var coord in wanted)
            if (!_loaded.ContainsKey(coord))
                TryLoadChunk(coord);

        int keepRadius = viewRadiusChunks + unloadHysteresis;
        var toUnload = new List<Vector3Int>();
        foreach (var kv in _loaded)
        {
            var coord = kv.Key;
            var d = coord - pc;
            int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            int distY = Mathf.Abs(d.y);
            if (distXZ > keepRadius || distY > verticalRadiusChunks + unloadHysteresis)
                toUnload.Add(coord);
        }
        foreach (var c in toUnload) UnloadChunk(c);
    }

    private int EstimateWantedCount()
    {
        int r = viewRadiusChunks;
        int v = verticalRadiusChunks;
        return (2 * r + 1) * (2 * r + 1) * (2 * v + 1);
    }

    private void TryLoadChunk(Vector3Int coord)
    {
        if (_totalChunkObjects >= maxChunks && _pool.Count == 0) return;

        var cell = AcquireChunkCell();
        if (!cell) return;

        var worldPos = ChunkOriginWorld(coord);

        // Reset/clear while inactive to prevent one-frame flashes
        cell.ResetFor(coord, worldPos, _chunkSettings, _foliageSettings);
        cell.transform.SetParent(transform, true);
        cell.gameObject.SetActive(true); // expose after cleared

        var rt = new ChunkRuntime { coord = coord, cell = cell, stage = Stage.Allocated, colliderCooked = false };
        _loaded.Add(coord, rt);

        _densityQueue.Enqueue(rt);
    }

    private void UnloadChunk(Vector3Int coord)
    {
        if (!_loaded.TryGetValue(coord, out var rt)) return;
        _loaded.Remove(coord);
        ReleaseToPool(rt.cell);
    }

    private void RunDensityStage(int budget)
    {
        int count = 0;
        while (count < budget && _densityQueue.Count > 0)
        {
            var rt = _densityQueue.Dequeue();
            if (!_loaded.ContainsKey(rt.coord)) continue;

            rt.cell.GenerateVoxelsGPU_AllLayers();
            rt.stage = Stage.DensityReady;

            // After density, first mesh build is NORMAL priority (init)
            _meshQueueNormal.Enqueue(rt);
            count++;
        }
    }

    private void RunMeshStage(int budget)
    {
        if (!player) return;
        Vector3Int pc = WorldToChunkCoord(player.position);

        int count = 0;
        while (count < budget)
        {
            ChunkRuntime rt = null;

            // Drain HIGH priority (edits) first
            if (_meshQueueHigh.Count > 0) rt = _meshQueueHigh.Dequeue();
            else if (_meshQueueNormal.Count > 0) rt = _meshQueueNormal.Dequeue();
            else break;

            if (rt == null || !_loaded.ContainsKey(rt.coord)) continue;

            var delta = rt.coord - pc;
            int distXZ = Mathf.Max(Mathf.Abs(delta.x), Mathf.Abs(delta.z));
            int distY = Mathf.Abs(delta.y);
            bool rebuildCollider = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);

            rt.cell.BuildMesh(rebuildCollider);
            rt.stage = Stage.Ready;
            rt.colliderCooked = rebuildCollider; // remember if collider is cooked now
            count++;
        }
    }

    private void PromoteCollidersAroundPlayer(int budget)
    {
        if (!player || budget <= 0) return;
        Vector3Int pc = WorldToChunkCoord(player.position);
        int done = 0;

        foreach (var kv in _loaded)
        {
            if (done >= budget) break;

            var rt = kv.Value;
            if (rt.stage != Stage.Ready || rt.colliderCooked) continue;

            var d = rt.coord - pc;
            int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            int distY  = Mathf.Abs(d.y);
            bool shouldHaveCollider = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);
            if (!shouldHaveCollider) continue;

            if (!rt.cell.HasRenderableMesh())
            {
                rt.cell.RebuildColliderOnly(); // clears stale collider safely
                continue;
            }

            rt.cell.RebuildColliderOnly();
            rt.colliderCooked = true;
            done++;
        }
    }

    #endregion
    // ----------------------------------------------------------------------

    #region Pool

    private void PrewarmPool()
    {
        int target = Mathf.Min(prewarmChunks, maxChunks);
        for (int i = 0; i < target; i++)
        {
            var cell = CreateChunkCell();
            ReleaseToPool(cell);
        }
    }

    private ChunkCell CreateChunkCell()
    {
        var go = new GameObject("ChunkCell");
        go.transform.SetParent(transform, false);

        var cell = go.AddComponent<ChunkCell>();
        cell.Initialize(materialInstances, marchingCubesShader, noiseShader, terrainLayers, _foliageSettings);

        _totalChunkObjects++;
        return cell;
    }

    private ChunkCell AcquireChunkCell()
    {
        return _pool.Count > 0
            ? _pool.Dequeue()
            : (_totalChunkObjects < maxChunks ? CreateChunkCell() : null);
    }

    private void ReleaseToPool(ChunkCell cell)
    {
        if (!cell) return;
        cell.gameObject.SetActive(false);
        cell.transform.SetParent(transform, false);
        _pool.Enqueue(cell);
    }

    #endregion
    // ----------------------------------------------------------------------

    #region Editing API  (edits → HIGH-PRIORITY mesh rebuild)

    public TerrainType GetTerrainTypeAtWorld(Vector3 worldPos)
    {
        Vector3Int coord = WorldToChunkCoord(worldPos);
        if (!_loaded.TryGetValue(coord, out var rt) || rt.cell == null) return default;

        Vector3 localInChunk = worldPos - ChunkOriginWorld(coord); // origin-aware
        return rt.cell.GetTerrainTypeAtLocal(localInChunk);
    }

    public Dictionary<TerrainType, int> EditSphere(
        Vector3 centerWorld, float radiusWorld, float strengthWorld, TerrainType fillType,
        float breakingProgress = 0, bool forceSameBlock = false, bool forceReplace = false)
    {
        Dictionary<TerrainType, int> totalChanges = new();
        float toGrid = (gridSize - 3f) / chunkSize;

        Vector3 centerGrid = (centerWorld - _origin) * toGrid;
        float radiusGrid = radiusWorld * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            var result = rt.cell.UpdateVoxelGridWithSphere(
                localCenter,
                radiusGrid / 2f,
                strengthWorld * toGrid,
                fillType,
                inventory,
                breakingProgress,
                true,
                forceSameBlock,
                forceReplace
            );
            MergeInventoryChanges(totalChanges, result);

            // 🔸 edits jump the line
            _meshQueueHigh.Enqueue(rt);
            rt.colliderCooked = false; // force recook when near player
        });

        return totalChanges;
    }

    public Dictionary<TerrainType, int> EditCube(
        Vector3 centerWorld, Vector3 sizeWorld, Quaternion rotationWorld,
        float strengthWorld, TerrainType fillType, float breakingProgress = 0, bool forceReplace = false)
    {
        Dictionary<TerrainType, int> totalChanges = new();
        float toGrid = (gridSize - 3f) / chunkSize;

        Vector3 centerGrid = (centerWorld - _origin) * toGrid;
        float radiusForAABB = Mathf.Max(sizeWorld.x, Mathf.Max(sizeWorld.y, sizeWorld.z));
        float radiusGrid = radiusForAABB * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            var result = rt.cell.UpdateVoxelGridWithCube(
                localCenter,
                sizeWorld,
                rotationWorld,
                strengthWorld * toGrid,
                fillType,
                inventory,
                breakingProgress,
                false,
                false,
                forceReplace
            );
            MergeInventoryChanges(totalChanges, result);

            // 🔸 edits jump the line
            _meshQueueHigh.Enqueue(rt);
            rt.colliderCooked = false;
        });

        return totalChanges;
    }

    public void SmoothSphere(Vector3 centerWorld, float radiusWorld, float intensity)
    {
        float toGrid = (gridSize - 3f) / chunkSize;
        Vector3 centerGrid = (centerWorld - _origin) * toGrid;
        float radiusGrid = radiusWorld * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            rt.cell.SmoothSphere(localCenter, radiusGrid, intensity, true);

            // 🔸 edits jump the line
            _meshQueueHigh.Enqueue(rt);
            rt.colliderCooked = false;
        });
    }

    private static void MergeInventoryChanges(Dictionary<TerrainType, int> total, Dictionary<TerrainType, int> delta)
    {
        foreach (var kv in delta)
            total[kv.Key] = total.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
    }

    #endregion
    // ----------------------------------------------------------------------

    #region Conversions / Helpers

    private Vector3Int WorldToChunkCoord(Vector3 world)
    {
        Vector3 w = world - _origin;
        return new Vector3Int(
            Mathf.FloorToInt(w.x / chunkSize),
            Mathf.FloorToInt(w.y / chunkSize),
            Mathf.FloorToInt(w.z / chunkSize)
        );
    }

    private Vector3 ChunkOriginWorld(Vector3Int coord)
    {
        return _origin + new Vector3(coord.x * chunkSize, coord.y * chunkSize, coord.z * chunkSize);
    }

    private static int Chebyshev2D(Vector3Int v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.z));

    /// <summary>
    /// Finds loaded chunks possibly affected by a brush centered at centerGrid (grid coords relative to origin)
    /// with radiusGrid. Converts the center into each chunk’s local grid space and invokes the action.
    /// </summary>
    private void ApplyToAffectedChunks(Vector3 centerGrid, float radiusGrid, System.Action<ChunkRuntime, Vector3> perChunk)
    {
        int n = gridSize - 3; // chunk-local grid span

        HashSet<Vector3Int> candidates = new();

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            Vector3 sample = new(
                centerGrid.x + dx * (radiusGrid + 1f),
                centerGrid.y + dy * (radiusGrid + 1f),
                centerGrid.z + dz * (radiusGrid + 1f)
            );

            Vector3Int index = new(
                Mathf.FloorToInt(sample.x / n),
                Mathf.FloorToInt(sample.y / n),
                Mathf.FloorToInt(sample.z / n)
            );
            candidates.Add(index);
        }

        foreach (var idx in candidates)
        {
            if (!_loaded.TryGetValue(idx, out var rt) || rt.cell == null) continue;

            Vector3 localCenter = new(
                centerGrid.x - idx.x * (gridSize - 3),
                centerGrid.y - idx.y * (gridSize - 3),
                centerGrid.z - idx.z * (gridSize - 3)
            );

            perChunk?.Invoke(rt, localCenter);
        }
    }

    public Vector3 SnapToGrid(Vector3 position, float snapFactor)
    {
        float step = chunkSize / (gridSize - 3f) * Mathf.Max(0.0001f, snapFactor);
        Vector3 p = position - _origin;
        float x = Mathf.Round(p.x / step) * step;
        float y = Mathf.Round(p.y / step) * step;
        float z = Mathf.Round(p.z / step) * step;
        return _origin + new Vector3(x, y, z);
    }

    #endregion
}
