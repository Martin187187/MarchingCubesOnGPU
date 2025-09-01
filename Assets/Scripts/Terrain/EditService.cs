using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class EditService
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly ChunkWorld world;
    readonly MeshStage meshStage;
    internal readonly TerrainConfig cfg;
    internal readonly Dictionary<TerrainType, int> inventory;

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

    // -------------------- Existing immediate API (unchanged) --------------------
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

    // -------------------- NEW: World-space batching API --------------------
    public WorldEditBatch CreateBatch() => new WorldEditBatch(this);

    public sealed class WorldEditBatch : IDisposable
    {
        private readonly EditService _svc;
        private readonly Dictionary<ChunkRuntime, bool> _touched = new();
        private readonly HashSet<ChunkRuntime> _enqueued = new();
        private readonly Dictionary<TerrainType,int> _totalDelta = new();

        internal WorldEditBatch(EditService svc) { _svc = svc; }

        public void Sphere(Vector3 centerWorld, float radiusWorld, float strengthWorld, TerrainType fillType,
                           float breakingProgress = 0, bool forceSameBlock = false, bool previewOnly = false, bool forceReplace = false)
        {
            float toGrid = (_svc.cfg.gridSize - 3f) / _svc.cfg.chunkSize;
            Vector3 centerGrid = (centerWorld - _svc.world.Origin) * toGrid;
            float radiusGrid = radiusWorld * toGrid;

            _svc.ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
            {
                StartChunk(rt);
                var delta = ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().UpdateVoxelGridWithSphere(
                    localCenter, radiusGrid / 2f, strengthWorld * toGrid, fillType,
                    _svc.inventory, breakingProgress, true, forceSameBlock, previewOnly, forceReplace
                );
                Merge(_totalDelta, delta);
            });
        }

        public void Cube(Vector3 centerWorld, Vector3 sizeWorld, Quaternion rotationWorld,
                         float strengthWorld, TerrainType fillType, float breakingProgress = 0, bool previewOnly = false, bool forceReplace = false)
        {
            float toGrid = (_svc.cfg.gridSize - 3f) / _svc.cfg.chunkSize;
            Vector3 centerGrid = (centerWorld - _svc.world.Origin) * toGrid;
            float radiusForAABB = Mathf.Max(sizeWorld.x, Mathf.Max(sizeWorld.y, sizeWorld.z));
            float radiusGrid = radiusForAABB * toGrid;

            _svc.ApplyToAffectedChunks(centerGrid, radiusGrid, (rt, localCenter) =>
            {
                StartChunk(rt);
                var delta = ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().UpdateVoxelGridWithCube(
                    localCenter, sizeWorld, rotationWorld, strengthWorld * toGrid, fillType,
                    _svc.inventory, breakingProgress, false, false, previewOnly, forceReplace
                );
                Merge(_totalDelta, delta);
            });
        }

        public Dictionary<TerrainType,int> Commit()
        {
            foreach (var kv in _touched)
            {
                var rt = kv.Key;
                var chunkDelta = ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().EndBatch();
                Merge(_totalDelta, chunkDelta);

                if (_enqueued.Add(rt))
                {
                    _svc.meshStage.EnqueueHigh(rt);
                    rt.colliderCooked = false;
                }
            }
            _touched.Clear();
            return new Dictionary<TerrainType,int>(_totalDelta);
        }

        public void Dispose() => Commit();

        private void StartChunk(ChunkRuntime rt)
        {
            if (!_touched.ContainsKey(rt))
            {
                ((ChunkCellAdapter)rt.cell).GetComponent<ChunkCell>().BeginBatch();
                _touched.Add(rt, true);
            }
        }

        private static void Merge(Dictionary<TerrainType,int> total, Dictionary<TerrainType,int> delta)
        {
            if (delta == null) return;
            foreach (var kv in delta)
                total[kv.Key] = total.TryGetValue(kv.Key, out var v) ? v + kv.Value : kv.Value;
        }
    }

    // -------------------- Internal helpers --------------------
    internal void ApplyToAffectedChunks(Vector3 centerGrid, float radiusGrid, Action<ChunkRuntime, Vector3> perChunk)
    {
        // usable voxels along one chunk side
        int n = cfg.gridSize - 3;

        const float paddingCells = 1f;

        Vector3 minGrid = centerGrid - Vector3.one * (radiusGrid + paddingCells);
        Vector3 maxGrid = centerGrid + Vector3.one * (radiusGrid + paddingCells);

        Vector3 minChunkF = minGrid / n;
        Vector3 maxChunkF = maxGrid / n;

        Vector3Int minIdx = new Vector3Int(
            Mathf.FloorToInt(minChunkF.x),
            Mathf.FloorToInt(minChunkF.y),
            Mathf.FloorToInt(minChunkF.z)
        );
        Vector3Int maxIdx = new Vector3Int(
            Mathf.FloorToInt(maxChunkF.x),
            Mathf.FloorToInt(maxChunkF.y),
            Mathf.FloorToInt(maxChunkF.z)
        );

        for (int cx = minIdx.x; cx <= maxIdx.x; cx++)
        for (int cy = minIdx.y; cy <= maxIdx.y; cy++)
        for (int cz = minIdx.z; cz <= maxIdx.z; cz++)
        {
            var idx = new Vector3Int(cx, cy, cz);
            if (!loaded.TryGetValue(idx, out var rt) || rt.cell == null) continue;

            Vector3 localCenter = new Vector3(
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
