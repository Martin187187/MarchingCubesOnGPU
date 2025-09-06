using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(ChunkCell))]
public sealed class ChunkCellAdapter : MonoBehaviour, IChunkCell
{
    private ChunkCell impl;

    void Awake() => impl = GetComponent<ChunkCell>();

    // Identity
    public GameObject GameObject => gameObject;
    public Transform Transform => transform;

    // Lifecycle
    public void Initialize(Material[] materials, ComputeShader mc, ComputeShader noise,
                           List<TerrainNoiseProfile> layers, ChunkCell.FoliageSettings foliage)
        => impl.Initialize(materials, mc, noise, layers, foliage);

    public void ResetFor(Vector3Int coord, Vector3 worldPos,
                         ChunkCell.ChunkSettings chunkSettings,
                         ChunkCell.FoliageSettings foliageSettings)
        => impl.ResetFor(coord, worldPos, chunkSettings, foliageSettings);

    public void DisposeAll() => impl.DisposeAll();

    // Density / Mesh / Collider
    public void GenerateVoxelsAllLayers() => impl.GenerateVoxelsGPU_AllLayers();
    public void BuildMesh(bool withCollider) => impl.BuildMesh(withCollider);
    public void RebuildColliderOnly() => impl.RebuildColliderOnly();
    public bool HasRenderableMesh() => impl.HasRenderableMesh();

    // Queries
    public TerrainType GetTerrainTypeAtLocal(Vector3 local) => impl.GetTerrainTypeAtLocal(local);

    // --------- Batching ----------
    public void BeginBatch() => impl.BeginBatch();
    public Dictionary<TerrainType,int> EndBatch() => impl.EndBatch();

    // --------- Edits (grid-space) ----------
    public Dictionary<TerrainType,int> UpdateVoxelGridWithSphere(
        Vector3 centerGrid, float radiusGrid, float strength, TerrainType terrainType,
        Dictionary<TerrainType,int> inventory = null, float breakingProgress = 0,
        bool doFallOff = true, bool oneBlockOnly = false, bool previewOnly = false, bool forceReplace = false)
        => impl.UpdateVoxelGridWithSphere(centerGrid, radiusGrid, strength, terrainType,
                                          inventory, breakingProgress, doFallOff, oneBlockOnly, previewOnly, forceReplace);

    public Dictionary<TerrainType,int> UpdateVoxelGridWithCube(
        Vector3 centerGrid, Vector3 halfExtentsGrid, Quaternion rotation, float strength, TerrainType terrainType,
        Dictionary<TerrainType,int> inventory = null, float breakingProgress = 0,
        bool doFallOff = true, bool oneBlockOnly = false, bool previewOnly = false, bool forceReplace = false)
        => impl.UpdateVoxelGridWithCube(centerGrid, halfExtentsGrid, rotation, strength, terrainType,
                                        inventory, breakingProgress, doFallOff, oneBlockOnly, previewOnly, forceReplace);

    public void SmoothSphere(Vector3 centerGrid, float radiusGrid, float intensity, bool doFallOff = true)
        => impl.SmoothSphere(centerGrid, radiusGrid, intensity, doFallOff);
}
