using UnityEngine;

public enum Stage { Raw, DensityCompleted, StructureCompleted, MeshCompleted, Finished }

public sealed class ChunkRuntime
{
    public Vector3Int coord;
    public IChunkCell cell;
    public Stage stage;
    public bool colliderCooked;
}

public readonly struct StageContext
{
    public readonly Vector3Int? PlayerChunk;
    public readonly float Now;

    public StageContext(Vector3Int? playerChunk, float now)
    {
        PlayerChunk = playerChunk;
        Now = now;
    }
}

public interface IPipelineStage
{
    string Name { get; }
    bool HasWork { get; }
    void Run(int budget, in StageContext ctx);
}
