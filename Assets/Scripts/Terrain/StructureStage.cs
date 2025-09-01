using System;
using System.Collections.Generic;
using UnityEngine;

public sealed class StructureStage : IPipelineStage
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly EditService edits;
    readonly ChunkWorld world;
    readonly TerrainConfig config;
    readonly TreeSpawnerSO treeSpawner;

    // ensure each chunk gets processed once
    readonly HashSet<Vector3Int> spawnedChunks = new();

    public StructureStage(
        Dictionary<Vector3Int, ChunkRuntime> loaded,
        EditService edits,
        ChunkWorld world,
        TerrainConfig cfg,
        TreeSpawnerSO spawner)
    {
        this.loaded = loaded;
        this.edits = edits;
        this.world = world;
        this.config = cfg;
        this.treeSpawner = spawner;
    }

    public string Name => "Structures";

    public bool HasWork
    {
        get
        {
            if (treeSpawner == null || treeSpawner.archetypes == null || treeSpawner.archetypes.Length == 0)
                return false;

            foreach (var kv in loaded)
                if (kv.Value.stage == Stage.DensityReady && !spawnedChunks.Contains(kv.Key))
                    return true;

            return false;
        }
    }

    public void Run(int budget, in StageContext ctx)
    {
        if (edits == null || config == null || treeSpawner == null) return;

        int chunksProcessed = 0;
        int stampsBudget = Mathf.Max(1, config.budgetStructureStampsPerFrame);

        foreach (var kv in loaded)
        {
            if (chunksProcessed >= Mathf.Max(1, budget)) break;

            var coord = kv.Key;
            var rt = kv.Value;

            if (rt == null || rt.stage != Stage.DensityReady || spawnedChunks.Contains(coord))
                continue;

            int emittedStamps = 0;
            try
            {
                emittedStamps = SpawnTreesInChunk(coord, rt, stampsBudget);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"StructureStage.Run: SpawnTreesInChunk failed at {coord}: {ex.Message}");
            }

            spawnedChunks.Add(coord);
            chunksProcessed++;
            stampsBudget -= emittedStamps;

            // handoff to mesh stage after attempts to stamp
            rt.stage = Stage.MeshReady;

            if (stampsBudget <= 0) break;
        }
    }

    // ------------------------- Tree spawning -------------------------

    int SpawnTreesInChunk(Vector3Int coord, ChunkRuntime rt, int stampsBudget)
    {
        if (treeSpawner == null || treeSpawner.archetypes == null || treeSpawner.archetypes.Length == 0)
            return 0;

        int seed = Hash(world.ChunkOriginWorld(coord), treeSpawner.worldSeed);
        var rng = new System.Random(seed);

        int targetCount = treeSpawner.treesPerChunk.Random(rng);
        var placed = new List<Vector3>();
        int stamps = 0;

        // batch all stamps for this chunk in one go
        using (var batch = edits.CreateBatch())
        {
            for (int i = 0; i < targetCount; i++)
            {
                if (stamps >= stampsBudget) break;

                Vector3 chunkOrigin = world.ChunkOriginWorld(coord);
                float gs = config.chunkSize;
                Vector3 pos = chunkOrigin + new Vector3((float)rng.NextDouble() * gs, 0f, (float)rng.NextDouble() * gs);

                // ground probe
                if (!TryFindGround(ref pos, out var groundType)) continue;

                // altitude constraint
                if (pos.y < treeSpawner.altitudeWorld.min || pos.y > treeSpawner.altitudeWorld.max) continue;

                // slope constraint
                if (!SlopeOK(pos, treeSpawner.maxSlopeDeg)) continue;

                // global soil whitelist (optional)
                if (treeSpawner.allowedGroundTypes != null && treeSpawner.allowedGroundTypes.Length > 0)
                {
                    bool okSoil = false;
                    foreach (var gt in treeSpawner.allowedGroundTypes) if (gt == groundType) { okSoil = true; break; }
                    if (!okSoil) continue;
                }

                // spacing (coarse Poisson)
                bool farEnough = true;
                float minDist2 = treeSpawner.minDistanceBetweenTrees * treeSpawner.minDistanceBetweenTrees;
                for (int p = 0; p < placed.Count; p++)
                {
                    if ((placed[p] - pos).sqrMagnitude < minDist2) { farEnough = false; break; }
                }
                if (!farEnough) continue;

                // pick an archetype and check its soil rules
                var archetype = treeSpawner.PickArchetype(rng);
                if (archetype == null) continue;
                if (!GroundAllowedForArchetype(groundType, archetype)) continue;

                if (archetype.requiredSoilDepth > 0f && !HasSoilDepth(pos, groundType, archetype.requiredSoilDepth))
                    continue;

                // build the tree (trunk + roots + branches/leaves), batched
                stamps += BuildTreeBatched(batch, pos, rng, archetype);
                placed.Add(pos);

                if (stamps >= stampsBudget) break;
            }

            // commit once: single GPU upload + foliage cull per affected chunk
            batch.Commit();
        }

        return stamps;
    }

    // ------------------------- Ground / constraints -------------------------

    bool TryFindGround(ref Vector3 pos, out TerrainType groundType)
    {
        groundType = default;
        if (config == null || edits == null) return false;

        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxel = config.chunkSize / denom;

        float top = pos.y + config.chunkSize;
        float bottom = pos.y - config.chunkSize;

        Vector3 probe = new Vector3(pos.x, top, pos.z);
        for (float y = top; y >= bottom; y -= voxel)
        {
            probe.y = y;

            TerrainType t;
            try { t = edits.GetTerrainTypeAtWorld(probe); }
            catch (Exception ex)
            {
                Debug.LogWarning($"StructureStage.TryFindGround: sampling failed at {probe}: {ex.Message}");
                return false;
            }

            // treat non-air, non-tree materials as ground
            if (t != default && t != TerrainType.Leaf && t != TerrainType.Wood)
            {
                groundType = t;
                pos.y = y + voxel * 0.5f;
                return true;
            }
        }

        return false;
    }

    bool SlopeOK(Vector3 pos, float maxSlopeDeg)
    {
        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxel = config.chunkSize / denom;

        Vector3[] offs = { new(voxel,0,0), new(-voxel,0,0), new(0,0,voxel), new(0,0,-voxel) };
        var pts = new List<Vector3>(4);
        for (int i = 0; i < offs.Length; i++)
        {
            var p = pos + offs[i];
            if (TryFindGround(ref p, out _)) pts.Add(p);
        }
        if (pts.Count < 3) return true;

        var n = Vector3.Cross(pts[1] - pts[0], pts[2] - pts[0]);
        if (n.sqrMagnitude < 1e-6f) return true;
        n.Normalize();

        float slopeDeg = Vector3.Angle(n, Vector3.up);
        return slopeDeg <= maxSlopeDeg;
    }

    bool GroundAllowedForArchetype(TerrainType ground, TreeArchetypeSO a)
    {
        var list = a.allowedGroundTypes;
        if (list == null || list.Length == 0) return true; // unrestricted
        for (int i = 0; i < list.Length; i++)
            if (list[i].Equals(ground)) return true;
        return false;
    }

    bool HasSoilDepth(Vector3 basePos, TerrainType targetSoil, float depth)
    {
        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxel = config.chunkSize / denom;

        float accumulated = 0f;
        Vector3 p = basePos;
        for (int i = 0; i < 512 && accumulated < depth; i++)
        {
            p.y -= voxel;
            var t = edits.GetTerrainTypeAtWorld(p);
            if (t == targetSoil) accumulated += voxel;
            else break;
        }
        return accumulated >= depth;
    }

    // ------------------------- Build tree (batched) -------------------------

    int BuildTreeBatched(EditService.WorldEditBatch batch, Vector3 basePos, System.Random rng, TreeArchetypeSO a)
    {
        int stampCount = 0;

        float trunkH = a.trunkHeight.Random(rng);
        float trunkR = a.trunkRadius.Random(rng);
        float step = Mathf.Max(0.2f, a.voxelStampStep);

        Vector3 trunkTop = basePos + Vector3.up * trunkH;

        // trunk as capsule (sphere sweep)
        stampCount += StampCapsuleBatched(batch, basePos, trunkTop, trunkR, step, a.strengthWorld, TerrainType.Wood, a.forceReplace);

        // --- ROOTS ---
        stampCount += BuildRootsBatched(batch, basePos, rng, a, step);

        // branches
        int bCount = a.branches.Random(rng);
        float startY = basePos.y + trunkH * Mathf.Clamp01(a.branchStartHeight01);

        for (int b = 0; b < bCount; b++)
        {
            float yaw = a.branchYawJitterDeg.Random(rng) * Mathf.Deg2Rad;
            float pitch = a.branchPitchDeg.Random(rng) * Mathf.Deg2Rad;

            // yaw around Y, pitch downward from horizontal
            Quaternion q = Quaternion.AngleAxis(Mathf.Rad2Deg * yaw, Vector3.up)
                         * Quaternion.AngleAxis(-Mathf.Rad2Deg * pitch, Vector3.right);

            Vector3 dir = q * Vector3.forward;

            float len = a.branchLen.Random(rng);
            float rad = a.branchRadius.Random(rng);

            Vector3 start = new Vector3(basePos.x,
                                        startY + (float)rng.NextDouble() * (trunkH - (startY - basePos.y)),
                                        basePos.z);
            Vector3 end = start + dir.normalized * len;

            stampCount += StampCapsuleBatched(batch, start, end, rad, step, a.strengthWorld, TerrainType.Wood, a.forceReplace);

            // leaf blobs near branch end
            int leafBlobs = a.leafBlobsPerBranch.Random(rng);
            for (int i = 0; i < leafBlobs; i++)
            {
                float t = Mathf.Lerp(0.6f, 1.0f, (float)rng.NextDouble());
                Vector3 c = Vector3.Lerp(start, end, t) + RandomInsideSphere(rng, step * 1.5f);
                float rr = a.leafBlobRadius.Random(rng);
                stampCount += StampSphereBatched(batch, c, rr, a.strengthWorld, TerrainType.Leaf, a.forceReplace);
            }
        }

        return stampCount;
    }

    // ------------------------- ROOTS (batched) -------------------------

    int BuildRootsBatched(EditService.WorldEditBatch batch, Vector3 basePos, System.Random rng, TreeArchetypeSO a, float step)
    {
        if (a.roots.max <= 0) return 0;

        int stampCount = 0;

        float denom = Mathf.Max(0.0001f, (config.gridSize - 3f));
        float voxel = config.chunkSize / denom;

        float startDepth = Mathf.Max(voxel * 0.75f, a.rootStartDepth);
        Vector3 startBase = basePos - Vector3.up * startDepth;

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

            Vector3 p0 = startBase;
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

                if (a.rootDepthCap > 0f && p1.y < basePos.y - a.rootDepthCap)
                    break;

                float t = (segments <= 1) ? 1f : (float)(s + 1) / segments;
                float rNow = Mathf.Lerp(r0, r1, t);

                switch (a.rootPlacement)
                {
                    case TreeArchetypeSO.RootPlacementMode.ReplaceOnly:
                        stampCount += StampCapsuleReplaceOnlyBatched(batch, p0, p1, rNow, step, a.rootStrengthWorld, TerrainType.Wood);
                        break;

                    case TreeArchetypeSO.RootPlacementMode.Normal:
                        stampCount += StampCapsuleBatched(batch, p0, p1, rNow, step, a.rootStrengthWorld, TerrainType.Wood, forceReplace: false);
                        break;

                    case TreeArchetypeSO.RootPlacementMode.ForceReplace:
                        stampCount += StampCapsuleBatched(batch, p0, p1, rNow, step, a.rootStrengthWorld, TerrainType.Wood, forceReplace: true);
                        break;
                }

                p0 = p1;
            }
        }

        return stampCount;
    }

    // ------------------------- Generic batched stampers -------------------------

    int StampCapsuleBatched(EditService.WorldEditBatch batch, Vector3 a, Vector3 b, float radius, float step, float strength, TerrainType type, bool forceReplace)
    {
        int count = 0;
        float len = Vector3.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / step));
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0f : (float)i / steps;
            Vector3 p = Vector3.Lerp(a, b, t);
            batch.Sphere(p, radius, strength, type, breakingProgress: 0, forceSameBlock: false, previewOnly: false, forceReplace: forceReplace);
            count++;
        }
        return count;
    }

    int StampSphereBatched(EditService.WorldEditBatch batch, Vector3 center, float radius, float strength, TerrainType type, bool forceReplace)
    {
        batch.Sphere(center, radius, strength, type, breakingProgress: 0, forceSameBlock: false, previewOnly: false, forceReplace: forceReplace);
        return 1;
    }

    int StampCapsuleReplaceOnlyBatched(EditService.WorldEditBatch batch, Vector3 a, Vector3 b, float radius, float step, float strength, TerrainType type)
    {
        int count = 0;
        float len = Vector3.Distance(a, b);
        int steps = Mathf.Max(1, Mathf.CeilToInt(len / step));
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 0f : (float)i / steps;
            Vector3 p = Vector3.Lerp(a, b, t);
            count += StampSphereReplaceOnlyBatched(batch, p, radius, strength, type);
        }
        return count;
    }

    int StampSphereReplaceOnlyBatched(EditService.WorldEditBatch batch, Vector3 center, float radius, float strength, TerrainType type)
    {
        TerrainType t;
        try { t = edits.GetTerrainTypeAtWorld(center); }
        catch (Exception ex)
        {
            Debug.LogWarning($"StructureStage.StampSphereReplaceOnlyBatched: sample failed at {center}: {ex.Message}");
            return 0;
        }

        // skip if it's air or already tree material
        if (t == default || t == TerrainType.Leaf || t == TerrainType.Wood)
            return 0;

        batch.Sphere(center, radius, strength, type, breakingProgress: 0, forceSameBlock: false, previewOnly: false, forceReplace: true);
        return 1;
    }

    // ------------------------- Utils -------------------------

    static int Hash(Vector3 origin, int seed)
    {
        unchecked
        {
            int x = Mathf.FloorToInt(origin.x * 73856093);
            int y = Mathf.FloorToInt(origin.y * 19349663);
            int z = Mathf.FloorToInt(origin.z * 83492791);
            return x ^ y ^ z ^ seed;
        }
    }

    static Vector3 RandomInsideSphere(System.Random r, float scale)
    {
        // rejection sampling
        for (int i = 0; i < 32; i++)
        {
            float x = (float)r.NextDouble() * 2f - 1f;
            float y = (float)r.NextDouble() * 2f - 1f;
            float z = (float)r.NextDouble() * 2f - 1f;
            var v = new Vector3(x, y, z);
            if (v.sqrMagnitude <= 1f) return v * scale;
        }
        // fallback (rare)
        return UnityEngine.Random.insideUnitSphere * scale;
    }
}
