using System.Collections.Generic;
using UnityEngine;

public sealed class DensityStage : IPipelineStage
{
    private readonly IDictionary<Vector3Int, ChunkRuntime> loaded;
    private readonly IChunkQueue<ChunkRuntime> input;
    private readonly IChunkQueue<ChunkRuntime> output;

    public DensityStage(
        IDictionary<Vector3Int, ChunkRuntime> loaded,
        IChunkQueue<ChunkRuntime> input,
        IChunkQueue<ChunkRuntime> output)
    {
        this.loaded = loaded;
        this.input = input;
        this.output = output;
    }


    public string Name => "Density";
    public bool HasWork => input.Count > 0;
    public void Enqueue(ChunkRuntime rt) => input.Enqueue(rt);

    public void Run(int budget, in StageContext ctx)
    {
        int n = 0;
        while (n < budget && input.TryDequeue(out var rt))
        {
            // chunk might have been unloaded since enqueued
            if (!loaded.ContainsKey(rt.coord)) continue;

            // do the work
            rt.cell.GenerateVoxelsAllLayers();  // same call you had before
            rt.stage = Stage.DensityCompleted;

            // strict hand-off to next stage
            output.Enqueue(rt);
            n++;
        }
    }
}
