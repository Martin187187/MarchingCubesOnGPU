using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class EditService
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly ChunkWorld world;
    readonly MeshStage meshStage;
    readonly TerrainConfig cfg;
    readonly Dictionary<TerrainType, int> inventory;

    public EditService(
        Dictionary<Vector3Int, ChunkRuntime> loaded,
        ChunkWorld world,
        MeshStage meshStage,
        TerrainConfig cfg,
        Dictionary<TerrainType, int> inventory)
    {
        this.loaded = loaded;
        this.world = world;
        this.meshStage = meshStage;
        this.cfg = cfg;
        this.inventory = inventory;
    }

    public TerrainType GetTerrainTypeAtWorld(Vector3 worldPos)
    {
        Vector3Int coord = world.WorldToChunkCoord(worldPos);
        if (!loaded.TryGetValue(coord, out var rt) || rt.cell == null) return default;

        Vector3 local = worldPos - world.ChunkOriginWorld(coord);
        return rt.cell.GetTerrainTypeAtLocal(local);
    }

    public Dictionary<TerrainType, int> EditSphere(
        Vector3 centerWorld, float radiusWorld, float strengthWorld, TerrainType fillType,
        float breakingProgress = 0, bool forceSameBlock = false, bool previewOnly = false, bool forceReplace = false)
    {
        Dictionary<TerrainType, int> totalChanges = new();
        float toGrid = (cfg.gridSize - 3f) / cfg.chunkSize;

        Vector3 centerGrid = (centerWorld - world.Origin) * toGrid;
        float radiusGrid = radiusWorld * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            var result = ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().UpdateVoxelGridWithSphere(
                localCenter,
                radiusGrid / 2f,
                strengthWorld * toGrid,
                fillType,
                inventory,
                breakingProgress,
                true,
                forceSameBlock,
                previewOnly,
                forceReplace
            );
            MergeInventoryChanges(totalChanges, result);

            meshStage.EnqueueHigh(rt);
            rt.colliderCooked = false;
        });

        return totalChanges;
    }

    public Dictionary<TerrainType, int> EditCube(
        Vector3 centerWorld, Vector3 sizeWorld, Quaternion rotationWorld,
        float strengthWorld, TerrainType fillType, float breakingProgress = 0, bool previewOnly = false, bool forceReplace = false)
    {
        Dictionary<TerrainType, int> totalChanges = new();
        float toGrid = (cfg.gridSize - 3f) / cfg.chunkSize;

        Vector3 centerGrid = (centerWorld - world.Origin) * toGrid;
        float radiusForAABB = Mathf.Max(sizeWorld.x, Mathf.Max(sizeWorld.y, sizeWorld.z));
        float radiusGrid = radiusForAABB * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            var result = ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().UpdateVoxelGridWithCube(
                localCenter,
                sizeWorld,
                rotationWorld,
                strengthWorld * toGrid,
                fillType,
                inventory,
                breakingProgress,
                false,
                false,
                previewOnly,
                forceReplace
            );
            MergeInventoryChanges(totalChanges, result);

            meshStage.EnqueueHigh(rt);
            rt.colliderCooked = false;
        });

        return totalChanges;
    }

    public void SmoothSphere(Vector3 centerWorld, float radiusWorld, float intensity)
    {
        float toGrid = (cfg.gridSize - 3f) / cfg.chunkSize;
        Vector3 centerGrid = (centerWorld - world.Origin) * toGrid;
        float radiusGrid = radiusWorld * toGrid;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
        {
            ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().SmoothSphere(localCenter, radiusGrid, intensity, true);
            meshStage.EnqueueHigh(rt);
            rt.colliderCooked = false;
        });
    }

    void ApplyToAffectedChunks(Vector3 centerGrid, float radiusGrid, Action<ChunkRuntime, Vector3> perChunk)
    {
        int n = cfg.gridSize - 3;
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
            if (!loaded.TryGetValue(idx, out var rt) || rt.cell == null) continue;

            Vector3 localCenter = new(
                centerGrid.x - idx.x * n,
                centerGrid.y - idx.y * n,
                centerGrid.z - idx.z * n
            );

            perChunk?.Invoke(rt, localCenter);
        }
    }

    static void MergeInventoryChanges(Dictionary<TerrainType, int> total, Dictionary<TerrainType, int> delta)
    {
        foreach (var kv in delta)
            total[kv.Key] = total.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
    }
}
