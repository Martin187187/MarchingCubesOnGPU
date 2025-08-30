using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(ChunkCell))]
public sealed class ChunkCellAdapter : MonoBehaviour, IChunkCell
{
    private ChunkCell impl;

    void Awake() => impl = GetComponent<ChunkCell>();

    public void Initialize(Material[] materials, ComputeShader mc, ComputeShader noise, List<TerrainNoiseProfile> layers, ChunkCell.FoliageSettings foliage)
        => impl.Initialize(materials, mc, noise, layers, foliage);

    public void ResetFor(Vector3Int coord, Vector3 worldPos, ChunkCell.ChunkSettings chunkSettings, ChunkCell.FoliageSettings foliageSettings)
        => impl.ResetFor(coord, worldPos, chunkSettings, foliageSettings);

    public void GenerateVoxelsAllLayers() => impl.GenerateVoxelsGPU_AllLayers();
    public void BuildMesh(bool withCollider) => impl.BuildMesh(withCollider);
    public void RebuildColliderOnly() => impl.RebuildColliderOnly();
    public bool HasRenderableMesh() => impl.HasRenderableMesh();
    public TerrainType GetTerrainTypeAtLocal(Vector3 local) => impl.GetTerrainTypeAtLocal(local);
    public void DisposeAll() => impl.DisposeAll();

    public GameObject GameObject => gameObject;
    public Transform Transform => transform;
}
