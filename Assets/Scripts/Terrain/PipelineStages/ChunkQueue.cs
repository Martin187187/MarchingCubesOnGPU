using System.Collections.Generic;

public interface IChunkQueue<T>
{
    int Count { get; }
    void Enqueue(T item);
    bool TryDequeue(out T item);
}

public sealed class ChunkQueue<T> : IChunkQueue<T>
{
    private readonly Queue<T> q = new();
    public int Count => q.Count;
    public void Enqueue(T item) => q.Enqueue(item);
    public bool TryDequeue(out T item)
    {
        if (q.Count == 0) { item = default!; return false; }
        item = q.Dequeue(); return true;
    }
}