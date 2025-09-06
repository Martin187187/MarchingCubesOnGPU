using System.Collections.Generic;
using UnityEngine;

public interface IChunkCell
{
    // Identity / scene
    GameObject GameObject { get; }
    Transform Transform { get; }

    // Lifecycle
    void Initialize(Material[] materials,
                    ComputeShader marchingCubes,
                    ComputeShader noise,
                    List<TerrainNoiseProfile> terrainLayers,
                    ChunkCell.FoliageSettings foliageDefaults);

    void ResetFor(Vector3Int index,
                  Vector3 worldPosition,
                  ChunkCell.ChunkSettings settings,
                  ChunkCell.FoliageSettings foliageOverride);

    void DisposeAll();

    // Density / Mesh / Collider
    void GenerateVoxelsAllLayers();
    void BuildMesh(bool withCollider);
    void RebuildColliderOnly();
    bool HasRenderableMesh();

    // Queries
    TerrainType GetTerrainTypeAtLocal(Vector3 localWorld); // local world-units within this chunk

    // --------- Batching (per-chunk) ---------
    void BeginBatch();
    /// <summary>Ends the batch, uploads voxels once, runs pending foliage sweeps.
    /// Returns aggregated inventory delta for this chunk during the batch.</summary>
    Dictionary<TerrainType,int> EndBatch();

    // --------- Edits (grid-space) ----------
    Dictionary<TerrainType,int> UpdateVoxelGridWithSphere(
        Vector3 centerGrid,
        float radiusGrid,
        float strength,
        TerrainType terrainType,
        Dictionary<TerrainType,int> inventory = null,
        float breakingProgress = 0,
        bool doFallOff = true,
        bool oneBlockOnly = false,
        bool previewOnly = false,
        bool forceReplace = false
    );

    Dictionary<TerrainType,int> UpdateVoxelGridWithCube(
        Vector3 centerGrid,
        Vector3 halfExtentsGrid,
        Quaternion rotation,
        float strength,
        TerrainType terrainType,
        Dictionary<TerrainType,int> inventory = null,
        float breakingProgress = 0,
        bool doFallOff = true,
        bool oneBlockOnly = false,
        bool previewOnly = false,
        bool forceReplace = false
    );

    // Optional smoothing utility (grid-space)
    void SmoothSphere(Vector3 centerGrid, float radiusGrid, float intensity, bool doFallOff = true);
}
