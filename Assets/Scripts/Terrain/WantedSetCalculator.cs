using System.Collections.Generic;
using UnityEngine;

public sealed class WantedSetCalculator
{
    readonly int viewRadiusChunks;
    readonly int verticalRadiusChunks;
    readonly int unloadHysteresis;

    public WantedSetCalculator(int viewRadiusChunks, int verticalRadiusChunks, int unloadHysteresis)
    {
        this.viewRadiusChunks = viewRadiusChunks;
        this.verticalRadiusChunks = verticalRadiusChunks;
        this.unloadHysteresis = unloadHysteresis;
    }

    public void Compute(
        Vector3Int playerChunk,
        ICollection<Vector3Int> currentlyLoaded,
        out List<Vector3Int> sortedWanted,
        out List<Vector3Int> toUnload)
    {
        sortedWanted = new List<Vector3Int>(EstimateWantedCount());
        for (int dz = -viewRadiusChunks; dz <= viewRadiusChunks; dz++)
        for (int dx = -viewRadiusChunks; dx <= viewRadiusChunks; dx++)
        for (int dy = -verticalRadiusChunks; dy <= verticalRadiusChunks; dy++)
        {
            int distXZ = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz));
            if (distXZ <= viewRadiusChunks)
                sortedWanted.Add(new Vector3Int(playerChunk.x + dx, playerChunk.y + dy, playerChunk.z + dz));
        }

        sortedWanted.Sort((a, b) =>
        {
            int da = ChunkWorld.Chebyshev2D(a - playerChunk);
            int db = ChunkWorld.Chebyshev2D(b - playerChunk);
            return da.CompareTo(db);
        });

        int keepRadius = viewRadiusChunks + unloadHysteresis;
        toUnload = new List<Vector3Int>();

        foreach (var coord in currentlyLoaded)
        {
            Vector3Int d = coord - playerChunk;
            int distXZ = Mathf.Max(Mathf.Abs(d.x), Mathf.Abs(d.z));
            int distY = Mathf.Abs(d.y);
            if (distXZ > keepRadius || distY > verticalRadiusChunks + unloadHysteresis)
                toUnload.Add(coord);
        }
    }

    int EstimateWantedCount()
    {
        int r = viewRadiusChunks;
        int v = verticalRadiusChunks;
        return (2 * r + 1) * (2 * r + 1) * (2 * v + 1);
    }
}
