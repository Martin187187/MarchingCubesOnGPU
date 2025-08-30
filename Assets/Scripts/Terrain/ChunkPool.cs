using UnityEngine;
using System.Collections.Generic;

public sealed class ChunkPool
{
    readonly Transform parent;
    readonly Material[] materials;
    readonly ComputeShader mcShader, noiseShader;
    readonly List<TerrainNoiseProfile> terrainLayers;
    readonly ChunkCell.FoliageSettings foliageSettings;
    readonly int maxChunks;

    readonly Queue<IChunkCell> pool = new();
    int total = 0;

    public ChunkPool(Transform parent, Material[] mats, ComputeShader mc, ComputeShader noise,
        List<TerrainNoiseProfile> layers, ChunkCell.FoliageSettings foliage, int maxChunks)
    {
        this.parent = parent;
        materials = mats;
        mcShader = mc;
        noiseShader = noise;
        terrainLayers = layers;
        foliageSettings = foliage;
        this.maxChunks = maxChunks;
    }

    public void Prewarm(int count)
    {
        for (int i = 0; i < count; i++) Release(Create());
    }

    public IChunkCell Acquire() => pool.Count > 0 ? pool.Dequeue() : (total < maxChunks ? Create() : null);

    public void Release(IChunkCell cell)
    {
        cell.GameObject.SetActive(false);
        cell.Transform.SetParent(parent, false);
        pool.Enqueue(cell);
    }

    IChunkCell Create()
    {
        var go = new GameObject("ChunkCell");
        go.transform.SetParent(parent, false);
        var adapter = go.AddComponent<ChunkCellAdapter>();
        adapter.Initialize(materials, mcShader, noiseShader, terrainLayers, foliageSettings);
        total++;
        return adapter;
    }
}
