using UnityEngine;

public sealed class ChunkWorld
{
    readonly Vector3 origin;
    readonly int chunkSize;
    readonly int gridSize;

    public ChunkWorld(Vector3 origin, int chunkSize, int gridSize)
    {
        this.origin = origin;
        this.chunkSize = chunkSize;
        this.gridSize = gridSize;
    }

    public Vector3Int WorldToChunkCoord(Vector3 world)
    {
        Vector3 w = world - origin;
        return new Vector3Int(
            Mathf.FloorToInt(w.x / chunkSize),
            Mathf.FloorToInt(w.y / chunkSize),
            Mathf.FloorToInt(w.z / chunkSize)
        );
    }

    public Vector3 ChunkOriginWorld(Vector3Int coord)
        => origin + new Vector3(coord.x * chunkSize, coord.y * chunkSize, coord.z * chunkSize);

    public Vector3 SnapToGrid(Vector3 position, float snapFactor)
    {
        float step = chunkSize / (gridSize - 3f) * Mathf.Max(0.0001f, snapFactor);
        Vector3 p = position - origin;
        float x = Mathf.Round(p.x / step) * step;
        float y = Mathf.Round(p.y / step) * step;
        float z = Mathf.Round(p.z / step) * step;
        return origin + new Vector3(x, y, z);
    }

    public static int Chebyshev2D(Vector3Int v) => Mathf.Max(Mathf.Abs(v.x), Mathf.Abs(v.z));
    public int LocalGridSpan => gridSize - 3;
    public Vector3 Origin => origin;
}
