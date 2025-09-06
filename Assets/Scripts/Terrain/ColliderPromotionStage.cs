using System.Collections.Generic;
using UnityEngine;

public sealed class ColliderPromotionStage : IPipelineStage
{
    private readonly IDictionary<Vector3Int, ChunkRuntime> loaded;
    private readonly int colliderRadiusChunks;
    private readonly int verticalRadiusChunks;

    // Input queue from MeshStage (chunks with meshes that may need colliders)
    private readonly IChunkQueue<ChunkRuntime> input;

    // Optional: output queue if you want a downstream hop (often null)
    private readonly IChunkQueue<ChunkRuntime> output;

    // Avoid duplicate enqueues
    private readonly HashSet<Vector3Int> inQueue = new();

    public ColliderPromotionStage(
        IDictionary<Vector3Int, ChunkRuntime> loaded,
        int colliderRadiusChunks,
        int verticalRadiusChunks,
        IChunkQueue<ChunkRuntime> input,
        IChunkQueue<ChunkRuntime> output = null)
    {
        this.loaded = loaded;
        this.colliderRadiusChunks = colliderRadiusChunks;
        this.verticalRadiusChunks = verticalRadiusChunks;
        this.input = input;
        this.output = output;
    }

    public string Name => "Colliders";
    public bool HasWork => input.Count > 0;

    // Public enqueue (from MeshStage or elsewhere)
    public void Enqueue(ChunkRuntime rt)
    {
        if (rt == null) return;
        if (!inQueue.Add(rt.coord)) return; // already queued
        input.Enqueue(rt);
    }

    public void Run(int budget, in StageContext ctx)
    {
        if (budget <= 0) return;

        int processed = 0;
        while (processed < budget && input.TryDequeue(out var rt))
        {
            // Allow re-queue later
            inQueue.Remove(rt.coord);

            // Skip if unloaded
            if (rt == null || !loaded.ContainsKey(rt.coord))
                continue;

            // Only promote colliders for chunks that have a mesh and aren't done yet
            // Adjust these to your final enum names:
            if (rt.stage != Stage.MeshCompleted && rt.stage != Stage.MeshCompleted)
                continue;
            if (rt.colliderCooked)
                continue;

            // If no player info, we can skip or promote immediately.
            // Here we require proximity info to avoid cooking far-away colliders.
            if (!ctx.PlayerChunk.HasValue)
                continue;

            var d = rt.coord - ctx.PlayerChunk.Value;
            int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            int distY  = Mathf.Abs(d.y);
            bool shouldHave = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);

            if (!shouldHave)
            {
                // Not close enough yet — push back to the end so it can be reconsidered later.
                // This keeps the pipeline local and avoids a full-world scan.
                processed++;
                Requeue(rt);
                continue;
            }

            // If there is no renderable mesh yet, we can still try to ensure collider is cleared/built.
            // Your ChunkCell.RebuildColliderOnly handles both cases safely.
            rt.cell.RebuildColliderOnly();

            // Mark state and hand off (if any downstream)
            rt.colliderCooked = true;
            rt.stage = Stage.Finished; // or ColliderReady per your naming
            output?.Enqueue(rt);

            processed++;
        }
    }

    private void Requeue(ChunkRuntime rt)
    {
        // Put it back into the queue once; inQueue ensures no dupes
        if (inQueue.Add(rt.coord))
            input.Enqueue(rt);
    }
}
