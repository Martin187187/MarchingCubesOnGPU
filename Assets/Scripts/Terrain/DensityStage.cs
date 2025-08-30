using System.Collections.Generic;
using UnityEngine;

public sealed class DensityStage : IPipelineStage
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly MeshStage meshStage;
    readonly Queue<ChunkRuntime> queue = new();

    public DensityStage(Dictionary<Vector3Int, ChunkRuntime> loaded, MeshStage meshStage)
    {
        this.loaded = loaded;
        this.meshStage = meshStage;
    }

    public string Name => "Density";
    public bool HasWork => queue.Count > 0;

    public void Enqueue(ChunkRuntime rt) => queue.Enqueue(rt);

    public void Run(int budget, in StageContext ctx)
    {
        int n = 0;
        while (n < budget && queue.Count > 0)
        {
            var rt = queue.Dequeue();
            if (!loaded.ContainsKey(rt.coord)) continue;

            rt.cell.GenerateVoxelsAllLayers();
            rt.stage = Stage.DensityReady;

            meshStage.EnqueueNormal(rt);
            n++;
        }
    }
}
