using System.Collections.Generic;
using UnityEngine;

public sealed class ColliderPromotionStage : IPipelineStage
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly int colliderRadiusChunks;
    readonly int verticalRadiusChunks;

    public ColliderPromotionStage(Dictionary<Vector3Int, ChunkRuntime> loaded, int colliderRadiusChunks, int verticalRadiusChunks)
    {
        this.loaded = loaded;
        this.colliderRadiusChunks = colliderRadiusChunks;
        this.verticalRadiusChunks = verticalRadiusChunks;
    }

    public string Name => "Colliders";

    // We keep this simple; promotions are cheap to scan.
    public bool HasWork => true;

    public void Run(int budget, in StageContext ctx)
    {
        if (budget <= 0 || !ctx.PlayerChunk.HasValue) return;

        int done = 0;
        foreach (var kv in loaded)
        {
            if (done >= budget) break;

            var rt = kv.Value;
            if (rt.stage != Stage.Ready || rt.colliderCooked) continue;

            var d = rt.coord - ctx.PlayerChunk.Value;
            int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            int distY = Mathf.Abs(d.y);
            bool shouldHave = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);
            if (!shouldHave) continue;

            if (!rt.cell.HasRenderableMesh())
            {
                rt.cell.RebuildColliderOnly();
                continue;
            }

            rt.cell.RebuildColliderOnly();
            rt.colliderCooked = true;
            done++;
        }
    }
}
