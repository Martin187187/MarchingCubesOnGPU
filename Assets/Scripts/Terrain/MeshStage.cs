using System.Collections.Generic;
using UnityEngine;

public sealed class MeshStage : IPipelineStage
{
    readonly Dictionary<Vector3Int, ChunkRuntime> loaded;
    readonly int colliderRadiusChunks;
    readonly int verticalRadiusChunks;

    // Queues implemented as linked lists so we can move nodes in O(1)
    readonly LinkedList<ChunkRuntime> high = new();
    readonly LinkedList<ChunkRuntime> normal = new();

    // Track where each coord currently lives and its node
    enum Q { High, Normal }
    readonly Dictionary<Vector3Int, (Q q, LinkedListNode<ChunkRuntime> node)> index
        = new();

    public MeshStage(Dictionary<Vector3Int, ChunkRuntime> loaded, int colliderRadiusChunks, int verticalRadiusChunks)
    {
        this.loaded = loaded;
        this.colliderRadiusChunks = colliderRadiusChunks;
        this.verticalRadiusChunks = verticalRadiusChunks;
    }

    public string Name => "Mesh";
    public bool HasWork => high.Count > 0 || normal.Count > 0;

    // ---------- Public enqueue API ----------

    public void EnqueueHigh(ChunkRuntime rt) => Enqueue(rt, Q.High);
    public void EnqueueNormal(ChunkRuntime rt) => Enqueue(rt, Q.Normal);

    public void Enqueue(ChunkRuntime rt, bool highPriority) =>
        Enqueue(rt, highPriority ? Q.High : Q.Normal);

    void Enqueue(ChunkRuntime rt, Q targetQ)
    {
        if (rt == null) return;

        // If it's already in a queue…
        if (index.TryGetValue(rt.coord, out var entry))
        {
            // If it's already in the same queue: move to back
            if (entry.q == targetQ)
            {
                var list = entry.q == Q.High ? high : normal;
                // Remove and re-add at tail to "move to back"
                entry.node = list.AddLast(rt);
                index[rt.coord] = (entry.q, entry.node);
                return;
            }

            // Priority changed: move between queues
            var fromList = entry.q == Q.High ? high : normal;

            var toList = targetQ == Q.High ? high : normal;
            var newNode = toList.AddLast(rt);
            index[rt.coord] = (targetQ, newNode);
            return;
        }

        // New entry: add at back of target queue
        var listToAdd = targetQ == Q.High ? high : normal;
        var node = listToAdd.AddLast(rt);
        index.Add(rt.coord, (targetQ, node));
    }

    // ---------- Dequeue helper ----------

    ChunkRuntime DequeueAny()
    {
        LinkedList<ChunkRuntime> list = null;
        if (high.Count > 0) list = high;
        else if (normal.Count > 0) list = normal;
        else return null;

        var node = list.First;
        var rt = node.Value;
        list.RemoveFirst();
        index.Remove(rt.coord); // allow re-queueing later
        return rt;
    }

    // ---------- Run ----------

    public void Run(int budget, in StageContext ctx)
    {
        int count = 0;
        while (count < budget)
        {
            var rt = DequeueAny();
            if (rt == null) break;

            // Skip if chunk unloaded since enqueue
            if (!loaded.ContainsKey(rt.coord)) continue;

            bool buildCollider = false;
            if (ctx.PlayerChunk.HasValue)
            {
                var d = rt.coord - ctx.PlayerChunk.Value;
                int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
                int distY = Mathf.Abs(d.y);
                buildCollider = (distXZ <= colliderRadiusChunks) && (distY <= verticalRadiusChunks);
            }

            rt.cell.BuildMesh(buildCollider);
            rt.stage = Stage.Ready;
            rt.colliderCooked = buildCollider;
            count++;
        }
    }
}
