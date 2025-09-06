using System.Collections.Generic;
using UnityEngine;

[RequireComponent(typeof(TerrainHeightQuery))]
public class TerrainController : MonoBehaviour
{
    [Header("Assets / Shaders")]
    public Material[] materialInstances;
    public ComputeShader marchingCubesShader;
    public ComputeShader noiseShader;
    public List<TerrainNoiseProfile> terrainLayers;

    [Header("Config")]
    public TerrainConfig config;

    [Header("Streaming")]
    public Transform player;

    // Inventory
    public Dictionary<TerrainType, int> inventory = new()
    {
        { TerrainType.Grass, 0 }, { TerrainType.Dirt, 0 }, { TerrainType.Rock, 0 },
        { TerrainType.CrackedRock, 0 }, { TerrainType.IronOre, 0 }, { TerrainType.Beton, 0 },
    };

    // Internals
    ChunkWorld world;
    ChunkPool pool;
    WantedSetCalculator wanted;
    MeshStage meshStage;
    DensityStage densityStage;
    StructureStage structureStage;
    ColliderPromotionStage colliderPromoter;
    EditService edits;
    
    
    IChunkQueue<ChunkRuntime> rawQueue;
    IChunkQueue<ChunkRuntime> densQueue;
    IChunkQueue<ChunkRuntime> structQueue;
    IChunkQueue<ChunkRuntime> meshQueue;
    IChunkQueue<ChunkRuntime> meshPrioQueue;
    IChunkQueue<ChunkRuntime> collQueue;
    private TerrainHeightQuery _terrainHeightQuery;

    readonly Dictionary<Vector3Int, ChunkRuntime> loaded = new();
    float nextWantedTime;

    // Debug sampling (optional)
    public TerrainType terrainType = default;

    void Awake()
    {
        _terrainHeightQuery = GetComponent<TerrainHeightQuery>();
        _terrainHeightQuery.controller = this;
        _terrainHeightQuery.Init();
        if (!config) { enabled = false; return; }

        world = new ChunkWorld(transform.position, config.chunkSize, config.gridSize);

        var foliageSettings = config.foliageSettings ? config.foliageSettings.ToChunkFoliage() : default;
        pool = new ChunkPool(transform, materialInstances, marchingCubesShader, noiseShader, terrainLayers, foliageSettings, config.maxChunks);
        pool.Prewarm(Mathf.Min(config.prewarmChunks, config.maxChunks));
        
        rawQueue = new ChunkQueue<ChunkRuntime>();
        densQueue = new ChunkQueue<ChunkRuntime>();
        structQueue = new ChunkQueue<ChunkRuntime>();
        meshPrioQueue = new ChunkQueue<ChunkRuntime>();
        meshQueue = new ChunkQueue<ChunkRuntime>();
        collQueue = new ChunkQueue<ChunkRuntime>();
        
        densityStage = new DensityStage(loaded, rawQueue, densQueue);
        structureStage = new StructureStage(loaded, densQueue, structQueue, world, config, config.treeSpawner);
        meshStage = new MeshStage(loaded, config.colliderRadiusChunks, config.verticalRadiusChunks, meshPrioQueue, structQueue, collQueue);
        colliderPromoter = new ColliderPromotionStage(loaded, config.colliderRadiusChunks, config.verticalRadiusChunks, collQueue);
        
        
        edits = new EditService(loaded, world, meshStage, config, inventory);
        wanted = new WantedSetCalculator(config.viewRadiusChunks, config.verticalRadiusChunks, config.unloadHysteresis);
    }

    void OnDestroy()
    {
        foreach (var kv in loaded)
        {
            var rt = kv.Value;
            if (rt?.cell != null)
            {
                rt.cell.DisposeAll();
                pool.Release(rt.cell);
            }
        }
        loaded.Clear();
    }

    void Update()
    {
        if (!player) return;

        // Update wanted set on interval
        if (Time.time >= nextWantedTime)
        {
            var pc = world.WorldToChunkCoord(player.position);
            wanted.Compute(pc, loaded.Keys, out var sortedWanted, out var toUnload);

            foreach (var coord in sortedWanted)
                if (!loaded.ContainsKey(coord)) TryLoad(coord);

            foreach (var coord in toUnload)
                Unload(coord);

            nextWantedTime = Time.time + config.wantedUpdateInterval;
        }

        // Run pipeline stages with budgets
        var ctx = new StageContext(player ? world.WorldToChunkCoord(player.position) : (Vector3Int?)null, Time.time);

        if (densityStage.HasWork) densityStage.Run(config.budgetDensityPerFrame, in ctx);
        if (structureStage.HasWork) structureStage.Run(config.budgetStructureChunksPerFrame, in ctx);
        if (meshStage.HasWork) meshStage.Run(config.budgetMeshPerFrame, in ctx);
        if (colliderPromoter.HasWork) colliderPromoter.Run(config.budgetColliderPromotionsPerFrame, in ctx);

        // Optional debug sampling from camera
        var cam = Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, Mathf.Infinity))
            {
                float voxelSizeWorld = config.chunkSize / (config.gridSize - 3f);
                var debugSamplePos = hit.point - hit.normal.normalized * voxelSizeWorld * 0.7f;
                terrainType = edits.GetTerrainTypeAtWorld(debugSamplePos);
            }
        }
    }

    void TryLoad(Vector3Int coord)
    {
        var cell = pool.Acquire();
        if (cell == null) return;

        var origin = world.ChunkOriginWorld(coord);
        cell.ResetFor(coord, origin, config.ChunkSettings, config.foliageSettings ? config.foliageSettings.ToChunkFoliage() : default);
        cell.Transform.SetParent(transform, true);
        cell.GameObject.SetActive(true);

        var rt = new ChunkRuntime { coord = coord, cell = cell, stage = Stage.Raw, colliderCooked = false };
        loaded.Add(coord, rt);
        densityStage.Enqueue(rt);
    }

    void Unload(Vector3Int coord)
    {
        if (!loaded.TryGetValue(coord, out var rt)) return;
        loaded.Remove(coord);

        if (rt.cell != null)
        {
            pool.Release(rt.cell);
        }
    }

    // ---------- Public API ----------
    public TerrainType GetTerrainTypeAtWorld(Vector3 worldPos) => edits.GetTerrainTypeAtWorld(worldPos);

    public Dictionary<TerrainType, int> EditSphere(
        Vector3 centerWorld, float radiusWorld, float strengthWorld, TerrainType fillType,
        float breakingProgress = 0, bool forceSameBlock = false, bool previewOnly = false, bool forceReplace = false)
        => edits.EditSphere(centerWorld, radiusWorld, strengthWorld, fillType, breakingProgress, forceSameBlock, previewOnly, forceReplace);

    public Dictionary<TerrainType, int> EditCube(
        Vector3 centerWorld, Vector3 sizeWorld, Quaternion rotationWorld,
        float strengthWorld, TerrainType fillType, float breakingProgress = 0, bool previewOnly = false, bool forceReplace = false)
        => edits.EditCube(centerWorld, sizeWorld, rotationWorld, strengthWorld, fillType, breakingProgress, previewOnly, forceReplace);

    public void SmoothSphere(Vector3 centerWorld, float radiusWorld, float intensity)
        => edits.SmoothSphere(centerWorld, radiusWorld, intensity);

    public Vector3 SnapToGrid(Vector3 position, float snapFactor)
        => world.SnapToGrid(position, snapFactor);
    
    // ---------- Debug/Telemetry accessors ----------
    public int QueueRawCount        => rawQueue?.Count     ?? 0;
    public int QueueDensityCount    => densQueue?.Count    ?? 0;
    public int QueueStructureCount  => structQueue?.Count  ?? 0;
    public int QueueMeshPrioCount   => meshPrioQueue?.Count?? 0;
    public int QueueMeshCount       => meshQueue?.Count    ?? 0;
    public int QueueColliderCount   => collQueue?.Count    ?? 0;
    public int LoadedCount          => loaded?.Count       ?? 0;

    // Small histogram of current chunk stages
    public void GetStageHistogram(Dictionary<Stage,int> dst)
    {
        dst.Clear();
        foreach (var kv in loaded)
        {
            var s = kv.Value.stage;
            if (!dst.TryGetValue(s, out var c)) c = 0;
            dst[s] = c + 1;
        }
    }

    // optional: expose player chunk for UI
    public Vector3Int? CurrentPlayerChunk =>
        player ? world.WorldToChunkCoord(player.position) : (Vector3Int?)null;

}
