using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class StructureStage : IPipelineStage
{
    private readonly Dictionary<Vector3Int, ChunkRuntime> loaded;

    private readonly IChunkQueue<ChunkRuntime> input;
    private readonly IChunkQueue<ChunkRuntime> output;

    private readonly ChunkWorld world;
    private readonly TerrainConfig config;
    private readonly TreeSpawnerSO treeSpawner;

    // How many neighbor "owner" chunks to scan around the current one when discovering structures
    // (1 means 3x3x3 cube; set to 1 for most foliage; raise if your structures are huge).
    private readonly int scanHaloChunks = 1;

    public StructureStage(
        Dictionary<Vector3Int, ChunkRuntime> loaded,
        IChunkQueue<ChunkRuntime> input,
        IChunkQueue<ChunkRuntime> output,
        ChunkWorld world,
        TerrainConfig cfg,
        TreeSpawnerSO spawner)
    {
        this.loaded = loaded;
        this.input = input;
        this.output = output;
        this.world = world;
        this.config = cfg;
        this.treeSpawner = spawner;

        // Optional: if your spawner exposes a radius, use it; otherwise keep default = 1.
        try
        {
            var field = typeof(TreeSpawnerSO).GetField("scanRadiusChunks");
            if (field != null && field.FieldType == typeof(int))
            {
                int v = (int)field.GetValue(spawner);
                if (v >= 0) scanHaloChunks = Mathf.Max(0, v);
            }
        }
        catch { /* safe to ignore */ }
    }

    public string Name => "Structures";
    public bool HasWork => input.Count > 0;

    public void Run(int budget, in StageContext ctx)
    {
        int n = 0;
        while (n < budget && input.TryDequeue(out var rt))
        {
            if (rt == null || !loaded.ContainsKey(rt.coord)) continue;

            // Cross-chunk aware spawning: discover from neighbor owners, stamp only what intersects this chunk
            var points = DeterministicCircleSampler.GeneratePointsInCircleXZ(rt.cell.Transform.position, 32, 16, 124, 0.1f);

            var toStamp = new List<TreeInstance>(64);
            foreach (var point in points)
            {
                TerrainHeightQuery.TerrainProbe probe = TerrainHeightQuery.Instance.TryQuery(point);
                Vector3 pos = new Vector3(point.x, probe.height, point.z);
                // Archetype + soil checks (world)
                var archetype = treeSpawner.PickArchetype(new System.Random(123));
                if (archetype == null) continue;
                if (!GroundAllowedForArchetype(probe.terrainType, archetype)) continue;

                if (archetype.requiredSoilDepth > 0f &&
                    !HasSoilDepthWorld(rt, pos, probe.terrainType, archetype.requiredSoilDepth))
                    continue;

                // Stable per-tree seed so branches/leaves are identical in every affected chunk
                int treeSeed = Hash(pos, 1234 ^ unchecked((int)0x9E3779B9));
                toStamp.Add(new TreeInstance { posW = pos, archetype = archetype, seed = treeSeed });

            }
            
            
            // STAMPING: write only into THIS chunk, clipped per-sphere, limited by budget.
            int emitted = 0;
            rt.cell.BeginBatch();
            try
            {
                for (int i = 0; i < toStamp.Count && emitted < 10000; i++)
                {
                    var inst = toStamp[i];
                    var rng = new System.Random(inst.seed);

                    // Note: BuildTreeLocal_NoValidation counts *actual* sphere stamps (0 if fully outside)
                    emitted += BuildTreeLocal_NoValidation(rt, inst.posW, rng, inst.archetype);
                }
            }
            finally
            {
                rt.cell.EndBatch();
            }

            // Hand-off to next stage
            rt.stage = Stage.StructureCompleted;
            output.Enqueue(rt);
            n++;
        }
    }

    // ---------------------------------------------------------------------
    // WORLD READ HELPERS (reads may cross chunk boundaries)
    // ---------------------------------------------------------------------

    private Vector3Int WorldToCoordRelative(ChunkRuntime rt, in Vector3 posWorld)
    {
        Vector3 originThis = world.ChunkOriginWorld(rt.coord);
        float s = config.chunkSize;

        int dx = Mathf.FloorToInt((posWorld.x - originThis.x) / s);
        int dy = Mathf.FloorToInt((posWorld.y - originThis.y) / s);
        int dz = Mathf.FloorToInt((posWorld.z - originThis.z) / s);

        return rt.coord + new Vector3Int(dx, dy, dz);
    }

    private bool TryGetCellAtWorld(ChunkRuntime rt, in Vector3 posWorld, out ChunkRuntime owner, out Vector3 localWorld)
    {
        owner = null;
        localWorld = default;

        var c = WorldToCoordRelative(rt, posWorld);
        if (!loaded.TryGetValue(c, out owner)) return false;

        Vector3 o = world.ChunkOriginWorld(c);
        localWorld = posWorld - o;

        float s = config.chunkSize, eps = 1e-4f;
        if (localWorld.x < -eps || localWorld.y < -eps || localWorld.z < -eps ||
            localWorld.x > s + eps || localWorld.y > s + eps || localWorld.z > s + eps)
            return false;

        return true;
    }

    private bool GetTerrainTypeAtWorld(ChunkRuntime rt, in Vector3 posWorld, out TerrainType t)
    {
        t = default;
        if (!TryGetCellAtWorld(rt, posWorld, out var owner, out var localW)) return false;
        t = owner.cell.GetTerrainTypeAtLocal(localW);
        return true;
    }

    // Vertical probe allowed to cross chunk boundaries
    private bool TryFindGroundWorld(ChunkRuntime rt, ref Vector3 posWorld, out TerrainType groundType)
    {
        groundType = default;

        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxelWorld = config.chunkSize / denom;

        float top = posWorld.y + config.chunkSize;
        float bottom = posWorld.y - config.chunkSize;

        Vector3 probe = new Vector3(posWorld.x, top, posWorld.z);

        for (float y = top; y >= bottom; y -= voxelWorld)
        {
            probe.y = y;
            if (!GetTerrainTypeAtWorld(rt, probe, out var t)) continue;

            if (t != default && t != TerrainType.Leaf && t != TerrainType.Wood)
            {
                groundType = t;
                posWorld.y = y + voxelWorld * 0.5f;
                return true;
            }
        }
        return false;
    }

    private bool HasSoilDepthWorld(ChunkRuntime rt, Vector3 baseWorld, TerrainType targetSoil, float depthWorld)
    {
        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxelWorld = config.chunkSize / denom;

        Vector3 pW = baseWorld;
        float acc = 0f;

        for (int i = 0; i < 4096 && acc < depthWorld; i++)
        {
            pW.y -= voxelWorld;
            if (!GetTerrainTypeAtWorld(rt, pW, out var t)) break;
            if (t == targetSoil) acc += voxelWorld;
            else break;
        }
        return acc >= depthWorld;
    }

    // ---------------------------------------------------------------------
    // SPAWNING (discover from owner chunks; stamp only intersecting writes)
    // ---------------------------------------------------------------------

    private struct TreeInstance
    {
        public Vector3 posW;
        public TreeArchetypeSO archetype;
        public int seed;
    }

    private int SpawnTreesForLocalChunk(ChunkRuntime rt, int stampsBudget)
    {
        if (treeSpawner == null || treeSpawner.archetypes == null || treeSpawner.archetypes.Length == 0)
            return 0;

        // DISCOVERY: build a list of trees whose *owners* are within scan halo.
        var toStamp = new List<TreeInstance>(64);
        var placedByOwner = new List<Vector3>(128); // Poisson per-owner chunk

        int halo = Mathf.Max(0, scanHaloChunks);
        for (int dz = -halo; dz <= halo; dz++)
        for (int dy = -halo; dy <= halo; dy++)
        for (int dx = -halo; dx <= halo; dx++)
        {
            var ownerCoord = rt.coord + new Vector3Int(dx, dy, dz);

            // Option: require owner loaded to guarantee reproducibility with neighbors
            if (!loaded.ContainsKey(ownerCoord)) continue;

            Vector3 ownerOrigin = world.ChunkOriginWorld(ownerCoord);
            int ownerSeed = Hash(ownerOrigin, treeSpawner.worldSeed);
            var rng = new System.Random(ownerSeed);

            int targetCount = treeSpawner.treesPerChunk.Random(rng);
            placedByOwner.Clear();

            for (int i = 0; i < targetCount; i++)
            {
                // Candidate inside the *owner* chunk AABB
                float size = config.chunkSize;
                Vector3 posW = ownerOrigin + new Vector3(
                    (float)rng.NextDouble() * size,
                    0f,
                    (float)rng.NextDouble() * size
                );

                // Find ground with world-probe (may cross chunks)
                if (!TryFindGroundWorld(rt, ref posW, out var groundType))
                    continue;

                // Altitude gate (world)
                if (posW.y < treeSpawner.altitudeWorld.min || posW.y > treeSpawner.altitudeWorld.max)
                    continue;

                // Global soil whitelist
                if (treeSpawner.allowedGroundTypes != null && treeSpawner.allowedGroundTypes.Length > 0)
                {
                    bool ok = false;
                    foreach (var gt in treeSpawner.allowedGroundTypes)
                        if (gt.Equals(groundType)) { ok = true; break; }
                    if (!ok) continue;
                }

                // Poisson-like spacing, *per-owner* (deterministic)
                bool farEnough = true;
                float minDist2 = treeSpawner.minDistanceBetweenTrees * treeSpawner.minDistanceBetweenTrees;
                for (int p = 0; p < placedByOwner.Count; p++)
                {
                    if ((placedByOwner[p] - posW).sqrMagnitude < minDist2) { farEnough = false; break; }
                }
                if (!farEnough) continue;

                // Archetype + soil checks (world)
                var archetype = treeSpawner.PickArchetype(rng);
                if (archetype == null) continue;
                if (!GroundAllowedForArchetype(groundType, archetype)) continue;

                if (archetype.requiredSoilDepth > 0f &&
                    !HasSoilDepthWorld(rt, posW, groundType, archetype.requiredSoilDepth))
                    continue;

                // Stable per-tree seed so branches/leaves are identical in every affected chunk
                int treeSeed = Hash(posW, ownerSeed ^ unchecked((int)0x9E3779B9));
                toStamp.Add(new TreeInstance { posW = posW, archetype = archetype, seed = treeSeed });

                placedByOwner.Add(posW);
            }
        }

        // STAMPING: write only into THIS chunk, clipped per-sphere, limited by budget.
        int emitted = 0;
        rt.cell.BeginBatch();
        try
        {
            for (int i = 0; i < toStamp.Count && emitted < stampsBudget; i++)
            {
                var inst = toStamp[i];
                var rng = new System.Random(inst.seed);

                // Note: BuildTreeLocal_NoValidation counts *actual* sphere stamps (0 if fully outside)
                emitted += BuildTreeLocal_NoValidation(rt, inst.posW, rng, inst.archetype);
            }
        }
        finally
        {
            rt.cell.EndBatch();
        }

        return emitted;
    }

    // ---------------------------------------------------------------------
    // STAMPING (writes are clipped to this chunk via AABB check)
    // ---------------------------------------------------------------------

    private int BuildTreeLocal_NoValidation(ChunkRuntime rt, Vector3 baseWorld, System.Random rng, TreeArchetypeSO a)
    {
        int stamps = 0;

        float trunkH = a.trunkHeight.Random(rng);
        float trunkR = a.trunkRadius.Random(rng);
        float step = Mathf.Max(0.2f, a.voxelStampStep);

        Vector3 trunkTopW = baseWorld + Vector3.up * trunkH;

        // Trunk capsule sweep
        stamps += StampCapsuleLocal(rt, baseWorld, trunkTopW, trunkR, step, a.strengthWorld, TerrainType.Wood, a.forceReplace);

        // Roots
        stamps += BuildRootsLocal(rt, baseWorld, rng, a, step);

        // Branches + leaves
        int bCount = a.branches.Random(rng);
        float startY = baseWorld.y + trunkH * Mathf.Clamp01(a.branchStartHeight01);

        for (int b = 0; b < bCount; b++)
        {
            float yaw = a.branchYawJitterDeg.Random(rng) * Mathf.Deg2Rad;
            float pitch = a.branchPitchDeg.Random(rng) * Mathf.Deg2Rad;

            Quaternion q = Quaternion.AngleAxis(Mathf.Rad2Deg * yaw, Vector3.up)
                         * Quaternion.AngleAxis(-Mathf.Rad2Deg * pitch, Vector3.right);

            Vector3 dir = q * Vector3.forward;
            float len = a.branchLen.Random(rng);
            float rad = a.branchRadius.Random(rng);

            Vector3 startW = new Vector3(baseWorld.x,
                                         startY + (float)rng.NextDouble() * (trunkH - (startY - baseWorld.y)),
                                         baseWorld.z);
            Vector3 endW = startW + dir.normalized * len;

            stamps += StampCapsuleLocal(rt, startW, endW, rad, step, a.strengthWorld, TerrainType.Wood, a.forceReplace);

            // leaf blobs near branch end
            int leafBlobs = a.leafBlobsPerBranch.Random(rng);
            for (int i = 0; i < leafBlobs; i++)
            {
                float t = Mathf.Lerp(0.6f, 1.0f, (float)rng.NextDouble());
                Vector3 c = Vector3.Lerp(startW, endW, t) + RandomInsideSphere(rng, step * 1.5f);
                float rr = a.leafBlobRadius.Random(rng);
                stamps += StampSphereLocal(rt, c, rr, a.strengthWorld, TerrainType.Leaf, a.forceReplace);
            }
        }

        return stamps;
    }

    private int BuildRootsLocal(ChunkRuntime rt, Vector3 baseWorld, System.Random rng, TreeArchetypeSO a, float step)
    {
        if (a.roots.max <= 0) return 0;

        int stamps = 0;

        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxelWorld = config.chunkSize / denom;

        float startDepth = Mathf.Max(voxelWorld * 0.75f, a.rootStartDepth);
        Vector3 startBaseW = baseWorld - Vector3.up * startDepth;

        int rootCount = a.roots.Random(rng);
        for (int r = 0; r < rootCount; r++)
        {
            float totalLen = a.rootLen.Random(rng);
            float r0 = a.rootRadius.Random(rng);
            float r1 = Mathf.Max(0.05f, r0 * Mathf.Clamp01(a.rootTaper01));

            float yaw = a.rootYawJitterDeg.Random(rng) * Mathf.Deg2Rad;
            float pitch = a.rootPitchDeg.Random(rng) * Mathf.Deg2Rad;

            Quaternion q = Quaternion.AngleAxis(Mathf.Rad2Deg * yaw, Vector3.up)
                         * Quaternion.AngleAxis(Mathf.Rad2Deg * -pitch, Vector3.right);
            Vector3 dir = (q * Vector3.forward).normalized;

            int segments = Mathf.Max(1, a.rootSegments.Random(rng));
            float segLen = totalLen / segments;

            Vector3 p0 = startBaseW;
            for (int s = 0; s < segments; s++)
            {
                float turnYaw = a.rootTurnJitterDeg.Random(rng) * Mathf.Deg2Rad;
                float turnPitch = a.rootTurnJitterDeg.Random(rng) * 0.5f * Mathf.Deg2Rad;
                Quaternion turn = Quaternion.AngleAxis(Mathf.Rad2Deg * turnYaw, Vector3.up)
                                * Quaternion.AngleAxis(Mathf.Rad2Deg * -turnPitch, Vector3.right);

                dir = (turn * dir).normalized;

                Vector3 wobble = RandomInsideSphere(rng, a.rootWobble.Random(rng));
                Vector3 p1 = p0 + (dir * segLen) + wobble * 0.15f;

                p1.y -= Mathf.Abs(segLen) * a.rootDownBiasPerSegment;

                if (a.rootDepthCap > 0f && p1.y < baseWorld.y - a.rootDepthCap)
                    break;

                float t = (segments <= 1) ? 1f : (float)(s + 1) / segments;
                float rNow = Mathf.Lerp(r0, r1, t);

                stamps += StampCapsuleLocal(rt, p0, p1, rNow, step, a.rootStrengthWorld, TerrainType.Wood, forceReplace: false);
                p0 = p1;
            }
        }

        return stamps;
    }

    // Clip writes to the current chunk using a fast AABB test
    private int StampSphereLocal(ChunkRuntime rt, Vector3 centerWorld, float radiusWorld, float strength, TerrainType type, bool forceReplace)
    {
        Vector3 origin = world.ChunkOriginWorld(rt.coord);
        float toGrid = (config.gridSize - 3f) / Mathf.Max(1f, (float)config.chunkSize);

        // Quick reject: check sphere AABB vs this chunk’s local-world AABB
        float s = config.chunkSize;
        Vector3 cLW = centerWorld - origin;
        Vector3 r = new Vector3(radiusWorld, radiusWorld, radiusWorld);

        Vector3 min = cLW - r;
        Vector3 max = cLW + r;
        if (max.x < 0f || max.y < 0f || max.z < 0f || min.x > s || min.y > s || min.z > s)
            return 0; // no intersection with this chunk

        // Convert world → this chunk's grid
        Vector3 centerGrid = cLW * toGrid;
        float radiusGrid = radiusWorld * toGrid;

        rt.cell.UpdateVoxelGridWithSphere(
            centerGrid, radiusGrid, strength, type,
            inventory: null, breakingProgress: 0,
            doFallOff: true, oneBlockOnly: false,
            previewOnly: false, forceReplace: forceReplace
        );
        return 1;
    }

    private int StampCapsuleLocal(ChunkRuntime rt, Vector3 aWorld, Vector3 bWorld, float radiusWorld, float stepWorld, float strength, TerrainType type, bool forceReplace)
    {
        int count = 0;
        float len = Vector3.Distance(aWorld, bWorld);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / Mathf.Max(0.0001f, stepWorld)));
        for (int i = 0; i <= steps; i++)
        {
            float t = (steps == 0) ? 0f : (float)i / steps;
            Vector3 pW = Vector3.Lerp(aWorld, bWorld, t);
            count += StampSphereLocal(rt, pW, radiusWorld, strength, type, forceReplace);
        }
        return count;
    }

    // ---------------------------------------------------------------------
    // CONVERSIONS (kept for potential reuse)
    // ---------------------------------------------------------------------

    private void WorldToGrid(ChunkRuntime rt, in Vector3 worldPos, out Vector3 gridPos)
    {
        Vector3 origin = world.ChunkOriginWorld(rt.coord);
        float toGrid = (config.gridSize - 3f) / Mathf.Max(1f, (float)config.chunkSize);
        gridPos = (worldPos - origin) * toGrid;
    }

    private float WorldRadiusToGrid(float radiusWorld)
    {
        float toGrid = (config.gridSize - 3f) / Mathf.Max(1f, (float)config.chunkSize);
        return radiusWorld * toGrid;
    }

    // ---------------------------------------------------------------------
    // UTILS
    // ---------------------------------------------------------------------

    private static int Hash(Vector3 origin, int seed)
    {
        unchecked
        {
            int x = Mathf.FloorToInt(origin.x * 73856093);
            int y = Mathf.FloorToInt(origin.y * 19349663);
            int z = Mathf.FloorToInt(origin.z * 83492791);
            return x ^ y ^ z ^ seed;
        }
    }
    
    private static Vector3 RandomInsideSphere(System.Random r, float scale)
    {
        for (int i = 0; i < 32; i++)
        {
            float x = (float)r.NextDouble() * 2f - 1f;
            float y = (float)r.NextDouble() * 2f - 1f;
            float z = (float)r.NextDouble() * 2f - 1f;
            var v = new Vector3(x, y, z);
            if (v.sqrMagnitude <= 1f) return v * scale;
        }
        return UnityEngine.Random.insideUnitSphere * scale;
    }

    // Keep if needed elsewhere
    private bool GroundAllowedForArchetype(TerrainType ground, TreeArchetypeSO a)
    {
        var list = a.allowedGroundTypes;
        if (list == null || list.Length == 0) return true;
        for (int i = 0; i < list.Length; i++)
            if (list[i].Equals(ground)) return true;
        return false;
    }
}
