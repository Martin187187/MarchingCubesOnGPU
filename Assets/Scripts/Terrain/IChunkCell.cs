using UnityEngine;
using System.Collections.Generic;

public interface IChunkCell
{
    void Initialize(Material[] materials, ComputeShader mc, ComputeShader noise,
        List<TerrainNoiseProfile> layers, ChunkCell.FoliageSettings foliage);

    void ResetFor(Vector3Int coord, Vector3 worldPos, ChunkCell.ChunkSettings chunkSettings, ChunkCell.FoliageSettings foliageSettings);

    void GenerateVoxelsAllLayers();
    void BuildMesh(bool withCollider);
    void RebuildColliderOnly();
    bool HasRenderableMesh();
    TerrainType GetTerrainTypeAtLocal(Vector3 local);
    void DisposeAll();

    GameObject GameObject { get; }
    Transform Transform { get; }
}
