using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class TerrainController : MonoBehaviour
{
    public Material[] materialInstances;
    public ComputeShader marchingCubesShader;
    public ComputeShader noiseShader;

    public Vector3Int numberOfChunks = new(4, 4, 4);

    public int gridSize = 10;
    public int chunkSize = 16;
    public float isoLevel = 0.5f;

    public List<TerrainNoiseProfile> terrainLayers;

    private ChunkCell[,,] chunkMatrix;

    public Dictionary<TerrainType, int> inventory = new Dictionary<TerrainType, int>
    {
        { TerrainType.Grass, 0 },
        { TerrainType.Dirt, 0 },
        { TerrainType.Rock, 0 },
        { TerrainType.CrackedRock, 0 },
        { TerrainType.IronOre, 0 },
        { TerrainType.Beton, 0 },
        { TerrainType.Coal, 0 }
    };
    

    private Queue<ChunkCell> refreshQueue = new();
    private HashSet<ChunkCell> queuedChunks = new();
    public float refreshInterval = 0.01f;
    private float nextRefreshTime = 0f;
    public int maxParallelJobs = 8;
    private bool isRefreshing = false;
    
    
    // ---------------- FOLIAGE CONFIG ----------------
    [Header("Foliage")]
    public GameObject[] foliagePrefabs;          // Assign one or more prefabs
    public float foliageMaxSlopeDeg = 25f;       // Max slope for spawning (0 = only perfectly up; 90 = any)
    public float foliageTargetsPerArea = 10f;    // Expected spawns per (gridSize * gridSize) per chunk
    [Range(0f, 360f)] public float yawJitterDeg = 360f;   // full spin randomness (set smaller if you want less)
    [Range(0f, 20f)] public float tiltJitterDeg = 4f;    // small pitch/roll wobble
    public float positionJitter = 0.05f;                 // world-space XY jitter (meters)
    public Vector2 uniformScaleRange = new Vector2(0.9f, 1.1f); // min..max
    
    void Start()
    {
        Vector3Int halfChunks = new Vector3Int(
            numberOfChunks.x / 2,
            numberOfChunks.y / 2,
            numberOfChunks.z / 2
        );

        chunkMatrix = new ChunkCell[numberOfChunks.x, numberOfChunks.y, numberOfChunks.z];

        for (int x = 0; x < numberOfChunks.x; x++)
        for (int y = 0; y < numberOfChunks.y; y++)
        for (int z = 0; z < numberOfChunks.z; z++)
        {
            Vector3Int worldIndex = new Vector3Int(
                x - halfChunks.x,
                y - halfChunks.y,
                z - halfChunks.z
            );

            ChunkCell chunk = InitChunk(worldIndex);
            chunkMatrix[x, y, z] = chunk;
        }
        Refresh();
    }
    

    void Update()
    {
        if (Input.GetMouseButton(0) || Input.GetMouseButton(1))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                Vector3 pos = SnapToGrid(hit.point, (float)chunkSize / (gridSize - 3));
                float strength = Input.GetMouseButton(0) ? -0.04f : 0.04f;
                EditChunks(pos, strength, 2);
            }
        }

        if (!isRefreshing && refreshQueue.Count > 0)
        {
            StartCoroutine(ProcessChunksInParallel());
        }
    }

    private IEnumerator ProcessChunksInParallel()
    {
        isRefreshing = true;

        List<Coroutine> runningJobs = new List<Coroutine>();

        for (int i = 0; i < maxParallelJobs && refreshQueue.Count > 0; i++)
        {
            ChunkCell chunk = refreshQueue.Dequeue();
            if (chunk != null)
            {
                Coroutine job = StartCoroutine(RefreshChunk(chunk));
                runningJobs.Add(job);
            }
        }

        // Wait until all jobs complete
        foreach (var job in runningJobs)
        {
            yield return job;
        }

        isRefreshing = false;
        nextRefreshTime = Time.time + refreshInterval;
    }

    private IEnumerator RefreshChunk(ChunkCell chunk)
    {
        chunk.ReadVerticesFromComputeShader(); // Assume this is fast or async-safe
        queuedChunks.Remove(chunk);
        yield return null; // Simulate 1-frame delay; adjust if needed
    }

    private Vector3 SnapToGrid(Vector3 position, float gridSize)
    {
        float x = Mathf.Round(position.x / gridSize) * gridSize;
        float y = Mathf.Round(position.y / gridSize) * gridSize;
        float z = Mathf.Round(position.z / gridSize) * gridSize;
        return new Vector3(x, y, z);
    }

    private ChunkCell InitChunk(Vector3Int index)
    {
        GameObject chunkObj = new GameObject();
        chunkObj.name = $"Chunk ({index.x}, {index.y}, {index.z})";
        chunkObj.transform.SetParent(transform);
        chunkObj.transform.localPosition = index * chunkSize;
        ChunkCell cell = chunkObj.AddComponent<ChunkCell>();
        cell.materialInstances = materialInstances;
        cell.marchingCubesShader = marchingCubesShader;
        cell.noiseShader = noiseShader;
        cell.gridSize = gridSize;
        cell.chunkSize = chunkSize;
        cell.isoLevel = isoLevel;
        cell.terrainLayers = terrainLayers;
        
        cell.foliagePrefabs = foliagePrefabs;
        cell.foliageMaxSlopeDeg = foliageMaxSlopeDeg;
        cell.foliageTargetsPerArea = foliageTargetsPerArea;
        cell.yawJitterDeg = yawJitterDeg;
        cell.tiltJitterDeg = tiltJitterDeg;
        cell.positionJitter = positionJitter;
        cell.uniformScaleRange = uniformScaleRange;
        return cell;
    }

    public void EditChunks(Vector3 point, float strength, float rad)
    {
        Vector3 hitPosition = point * (float)(gridSize - 3) / chunkSize;
        int n = gridSize - 3;
        float radius = rad * (float)(gridSize - 3) / chunkSize;

        HashSet<Vector3Int> affectedIndices = new();

        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            Vector3 offsetPosition = new Vector3(
                hitPosition.x + dx * (radius + 1),
                hitPosition.y + dy * (radius + 1),
                hitPosition.z + dz * (radius + 1)
            );

            Vector3Int index = new Vector3Int(
                Mathf.FloorToInt(offsetPosition.x / n),
                Mathf.FloorToInt(offsetPosition.y / n),
                Mathf.FloorToInt(offsetPosition.z / n)
            ) + Vector3Int.one * numberOfChunks / 2;

            if (index.x >= 0 && index.x < numberOfChunks.x &&
                index.y >= 0 && index.y < numberOfChunks.y &&
                index.z >= 0 && index.z < numberOfChunks.z)
            {
                affectedIndices.Add(index);
            }
        }

        foreach (var affectedIndex in affectedIndices)
        {
            Vector3 offset = affectedIndex - Vector3Int.one * numberOfChunks / 2;
            ChunkCell chunk = chunkMatrix[affectedIndex.x, affectedIndex.y, affectedIndex.z];
            Vector3 alignedPosition = new Vector3(
                hitPosition.x - offset.x * n,
                hitPosition.y - offset.y * n,
                hitPosition.z - offset.z * n
            );

            chunk.UpdateVoxelGridWithSphere(alignedPosition, radius, strength * (gridSize - 3) / chunkSize, TerrainType.Dirt, inventory);
            EnqueueChunkForRefresh(chunk);
        }
    }

    private void EnqueueChunkForRefresh(ChunkCell chunk)
    {
        if (!queuedChunks.Contains(chunk))
        {
            refreshQueue.Enqueue(chunk);
            queuedChunks.Add(chunk);
        }
    }

    [ContextMenu("Rebuild All Chunks")]
    public void RebuildTerrain()
    {
        for (int x = 0; x < numberOfChunks.x; x++)
        for (int y = 0; y < numberOfChunks.y; y++)
        for (int z = 0; z < numberOfChunks.z; z++)
        {
            chunkMatrix[x, y, z].InitChunk();
            EnqueueChunkForRefresh(chunkMatrix[x, y, z]);
        }
    }

    public void Refresh()
    {
        for (int x = 0; x < numberOfChunks.x; x++)
        for (int y = 0; y < numberOfChunks.y; y++)
        for (int z = 0; z < numberOfChunks.z; z++)
        {
            EnqueueChunkForRefresh(chunkMatrix[x, y, z]);
        }
    }
}
