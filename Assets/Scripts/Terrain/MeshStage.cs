using System.Collections.Generic;
using UnityEngine;

public sealed class MeshStage : IPipelineStage
{
    private readonly IDictionary<Vector3Int, ChunkRuntime> loaded;
    private readonly int colliderRadiusChunks;
    private readonly int verticalRadiusChunks;

    // Input queues (priority)
    private readonly IChunkQueue<ChunkRuntime> inputHigh;
    private readonly IChunkQueue<ChunkRuntime> inputNormal;

    // Output queue (to collider stage or next pipeline hop)
    private readonly IChunkQueue<ChunkRuntime> output;

    // Prevent duplicate enqueues per coord
    private readonly HashSet<Vector3Int> inQueue = new();

    public MeshStage(
        IDictionary<Vector3Int, ChunkRuntime> loaded,
        int colliderRadiusChunks,
        int verticalRadiusChunks,
        IChunkQueue<ChunkRuntime> inputHigh,
        IChunkQueue<ChunkRuntime> inputNormal,
        IChunkQueue<ChunkRuntime> output)
    {
        this.loaded = loaded;
        this.colliderRadiusChunks = colliderRadiusChunks;
        this.verticalRadiusChunks = verticalRadiusChunks;
        this.inputHigh = inputHigh;
        this.inputNormal = inputNormal;
        this.output = output;
    }

    public string Name => "Mesh";
    public bool HasWork => inputHigh.Count > 0 || inputNormal.Count > 0;

    // ---------- Public enqueue API ----------
    public void EnqueueHigh(ChunkRuntime rt) => Enqueue(rt, highPriority: true);
    public void EnqueueNormal(ChunkRuntime rt) => Enqueue(rt, highPriority: false);
    public void Enqueue(ChunkRuntime rt, bool highPriority)
    {
        if (rt == null) return;
        if (!inQueue.Add(rt.coord)) return; // already queued somewhere

        if (highPriority) inputHigh.Enqueue(rt);
        else inputNormal.Enqueue(rt);
    }

    // ---------- Run ----------
    public void Run(int budget, in StageContext ctx)
    {
        int processed = 0;
        while (processed < budget && TryDequeuePriority(out var rt))
        {
            // Drop if chunk got unloaded meanwhile
            if (!loaded.ContainsKey(rt.coord))
            {
                inQueue.Remove(rt.coord);
                continue;
            }

            // Decide collider now vs later (streaming-friendly)
            bool buildColliderNow = false;
            if (ctx.PlayerChunk.HasValue)
            {
                var d = rt.coord - ctx.PlayerChunk.Value;
                int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
                int distY = Mathf.Abs(d.y);
                buildColliderNow = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);
            }

            // Build mesh (+ optional collider)
            rt.cell.BuildMesh(buildColliderNow);

            // State & flags
            if (buildColliderNow)
            {
                rt.colliderCooked = true;
                rt.stage = Stage.Finished;     // or ColliderReady, per your enum
            }
            else
            {
                rt.colliderCooked = false;
                rt.stage = Stage.MeshCompleted;         // or MeshReady
            }

            // Hand off to next stage (e.g., collider promotion)
            output?.Enqueue(rt);

            inQueue.Remove(rt.coord);
            processed++;
        }
    }

    // ---------- Helpers ----------
    private bool TryDequeuePriority(out ChunkRuntime rt)
    {
        // Always favor high-priority; fall back to normal
        if (inputHigh.TryDequeue(out rt)) return true;
        if (inputNormal.TryDequeue(out rt)) return true;

        rt = null;
        return false;
    }
}
