using System.Collections;
using System.Collections.Generic;
using UnityEngine;


/// <summary>
/// Owns chunks, voxel data upload/download, and edit operations.
/// No input or brush logic here — call EditSphere/EditCube from your tool.
/// </summary>
public class TerrainController : MonoBehaviour
{
    [Header("Rendering / Shaders")]
    public Material[] materialInstances;
    public ComputeShader marchingCubesShader;
    public ComputeShader noiseShader;

    [Header("World Layout")]
    public Vector3Int numberOfChunks = new(4, 4, 4);
    public int gridSize = 10;    // voxels per chunk edge (including padding)
    public int chunkSize = 16;   // world units per chunk edge
    public float isoLevel = 0.5f;

    [Header("Terrain Generation")]
    public List<TerrainNoiseProfile> terrainLayers;

    // Foliage config passed through to each chunk
    [Header("Foliage")]
    public GameObject[] foliagePrefabs;
    public float foliageMaxSlopeDeg = 25f;
    public float foliageTargetsPerArea = 10f;
    [Range(0f, 360f)] public float yawJitterDeg = 360f;
    [Range(0f, 20f)]  public float tiltJitterDeg = 4f;
    public float positionJitter = 0.05f;
    public Vector2 uniformScaleRange = new(0.9f, 1.1f);

    // Inventory bookkeeping (touched by chunks while crossing iso threshold)
    public Dictionary<TerrainType, int> inventory = new()
    {
        { TerrainType.Grass, 0 },
        { TerrainType.Dirt, 0 },
        { TerrainType.Rock, 0 },
        { TerrainType.CrackedRock, 0 },
        { TerrainType.IronOre, 0 },
        { TerrainType.Beton, 0 },
        { TerrainType.Coal, 0 },
    };

    // Internal
    private ChunkCell[,,] chunkMatrix;

    // Refresh queue controls how many chunks pull mesh data back per frame
    private readonly Queue<ChunkCell> refreshQueue = new();
    private readonly HashSet<ChunkCell> queuedChunks = new();
    [Header("Mesh Refresh")]
    public float refreshInterval = 0.01f;
    public int maxParallelJobs = 8;
    private float nextRefreshTime = 0f;
    private bool isRefreshing = false;
    
    public TerrainType terrainType = default;
    // ----------------------------------------------------------------------

    void Start()
    {
        BuildChunkGrid();
        Refresh(); // kick first meshing pass
    }

    /// <summary>
    /// Only handles queued chunk refreshes (no input/brush logic here).
    /// </summary>
    void Update()
    {
        if (!isRefreshing && refreshQueue.Count > 0 && Time.time >= nextRefreshTime)
        {
            StartCoroutine(ProcessChunksInParallel());
        }
        
        // --- Replace your T-key block in Update() with this (uses mesh normal for offset) ---


        var cam = Camera.main;
        if (cam != null)
        {
            Ray ray = cam.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out var hit, Mathf.Infinity))
            {
                float voxelSizeWorld = chunkSize / (gridSize - 3f);


                // Offset ALONG the mesh normal (outward) by half a voxel:
                var debugSamplePos = hit.point - hit.normal.normalized * voxelSizeWorld*0.7f;

                terrainType = GetTerrainTypeAtWorld(debugSamplePos);

            }
        }
        
        


    }

    // ----------------------------------------------------------------------
    #region Public editing API (called by your tool)
    
    public TerrainType GetTerrainTypeAtWorld(Vector3 worldPos)
    {
        if (chunkMatrix == null) return default;

        // Convert world -> controller-local
        Vector3 local = worldPos;

        // Map to chunk indices
        Vector3Int half = new Vector3Int(numberOfChunks.x / 2, numberOfChunks.y / 2, numberOfChunks.z / 2);
        int cx = Mathf.FloorToInt(local.x / chunkSize) + half.x;
        int cy = Mathf.FloorToInt(local.y / chunkSize) + half.y;
        int cz = Mathf.FloorToInt(local.z / chunkSize) + half.z;

        // Bounds check
        if (cx < 0 || cy < 0 || cz < 0 ||
            cx >= numberOfChunks.x || cy >= numberOfChunks.y || cz >= numberOfChunks.z)
            return default;
        Debug.Log("passed bound check");

        ChunkCell chunk = chunkMatrix[cx, cy, cz];
        if (chunk == null) return default;

        Debug.Log("passed chunk check");
        // Local position inside that chunk (0..chunkSize)
        Vector3 chunkOriginLocal = new Vector3(
            (cx - half.x) * chunkSize,
            (cy - half.y) * chunkSize,
            (cz - half.z) * chunkSize
        );
        Vector3 localInChunk = local - chunkOriginLocal;

        return chunk.GetTerrainTypeAtLocal(localInChunk);
    }

    /// <summary>
    /// Sphere operation. Positive strength builds, negative breaks.
    /// radiusWorld is in world units.
    /// </summary>
    public void EditSphere(Vector3 centerWorld, float radiusWorld, float strengthWorld, TerrainType fillType, bool forceSameBlock = false)
    {
        Vector3 centerGrid = centerWorld * (gridSize - 3f) / chunkSize;
        float radiusGrid = radiusWorld * (gridSize - 3f) / chunkSize;
        ApplyToAffectedChunks(centerGrid, radiusGrid, (chunk, localCenter) =>
        {
            chunk.UpdateVoxelGridWithSphere(localCenter, radiusGrid / 2f, strengthWorld * (gridSize - 3f) / chunkSize, fillType, inventory, true, forceSameBlock);
        });
    }

    /// <summary>
    /// Box operation. Positive strength builds, negative breaks.
    /// sizeWorld is full extents (scale) in world units. rotationWorld is world rotation for the box.
    /// </summary>
    public void EditCube(Vector3 centerWorld, Vector3 sizeWorld, Quaternion rotationWorld, float strengthWorld, TerrainType fillType)
    {
        // Convert world center to grid for chunk-local addressing.
        Vector3 centerGrid = centerWorld * (gridSize - 3f) / chunkSize;

        // NOTE: UpdateVoxelGridWithCube expects: position in chunk-local *grid* coords,
        // size in *world*? In your current code you pass effectorTransform.localScale directly.
        // We keep that behavior: we pass sizeWorld & rotationWorld through.
        float radiusForAABB = Mathf.Max(sizeWorld.x, Mathf.Max(sizeWorld.y, sizeWorld.z)) ;
        float radiusGrid = radiusForAABB * (gridSize - 3f) / chunkSize;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (chunk, localCenter) =>
        {
            chunk.UpdateVoxelGridWithCube(
                localCenter,
                sizeWorld,              // keep as world units to match your existing chunk API
                rotationWorld,
                strengthWorld * (gridSize - 3f) / chunkSize,
                fillType,
                inventory);
        });
    }

    /// <summary>
    /// Smooths voxels inside a sphere. 
    /// centerWorld/radiusWorld are in world units. 
    /// intensity = [0..1], how strongly to blend toward local average.
    /// </summary>
    public void SmoothSphere(Vector3 centerWorld, float radiusWorld, float intensity)
    {
        // Convert to grid space (same as EditSphere)
        Vector3 centerGrid = centerWorld * (gridSize - 3f) / chunkSize;
        float radiusGrid   = radiusWorld * (gridSize - 3f) / chunkSize;

        ApplyToAffectedChunks(centerGrid, radiusGrid, (chunk, localCenter) =>
        {
            // Call the real smoothing function on each chunk
            chunk.SmoothSphere(localCenter, radiusGrid, intensity, true);
        });
    }
    #endregion
    // ----------------------------------------------------------------------

    #region Chunk grid lifecycle

    private void BuildChunkGrid()
    {
        Vector3Int halfChunks = new(
            numberOfChunks.x / 2,
            numberOfChunks.y / 2,
            numberOfChunks.z / 2
        );

        chunkMatrix = new ChunkCell[numberOfChunks.x, numberOfChunks.y, numberOfChunks.z];

        for (int x = 0; x < numberOfChunks.x; x++)
        for (int y = 0; y < numberOfChunks.y; y++)
        for (int z = 0; z < numberOfChunks.z; z++)
        {
            Vector3Int worldIndex = new(
                x - halfChunks.x,
                y - halfChunks.y,
                z - halfChunks.z
            );

            ChunkCell chunk = InitChunk(worldIndex);
            chunkMatrix[x, y, z] = chunk;
        }
    }

    private ChunkCell InitChunk(Vector3Int index)
    {
        GameObject chunkObj = new GameObject { name = $"Chunk ({index.x}, {index.y}, {index.z})" };
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

        // pass foliage settings
        cell.foliagePrefabs = foliagePrefabs;
        cell.foliageMaxSlopeDeg = foliageMaxSlopeDeg;
        cell.foliageTargetsPerArea = foliageTargetsPerArea;
        cell.yawJitterDeg = yawJitterDeg;
        cell.tiltJitterDeg = tiltJitterDeg;
        cell.positionJitter = positionJitter;
        cell.uniformScaleRange = uniformScaleRange;

        return cell;
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

    /// <summary>Queue all chunks to rebuild mesh buffers from current voxel data.</summary>
    public void Refresh()
    {
        for (int x = 0; x < numberOfChunks.x; x++)
        for (int y = 0; y < numberOfChunks.y; y++)
        for (int z = 0; z < numberOfChunks.z; z++)
        {
            EnqueueChunkForRefresh(chunkMatrix[x, y, z]);
        }
    }

    #endregion
    // ----------------------------------------------------------------------

    #region Refresh queue

    private void EnqueueChunkForRefresh(ChunkCell chunk)
    {
        if (chunk == null) return;
        if (queuedChunks.Add(chunk))
            refreshQueue.Enqueue(chunk);
    }

    private IEnumerator ProcessChunksInParallel()
    {
        isRefreshing = true;

        List<Coroutine> runningJobs = new List<Coroutine>();

        int jobs = Mathf.Min(maxParallelJobs, refreshQueue.Count);
        for (int i = 0; i < jobs; i++)
        {
            ChunkCell chunk = refreshQueue.Dequeue();
            if (chunk != null)
            {
                Coroutine job = StartCoroutine(RefreshChunk(chunk));
                runningJobs.Add(job);
            }
        }

        foreach (var job in runningJobs)
            yield return job;

        isRefreshing = false;
        nextRefreshTime = Time.time + refreshInterval;
    }

    private IEnumerator RefreshChunk(ChunkCell chunk)
    {
        // Pull vertices/indices back and update the mesh.
        chunk.ReadVerticesFromComputeShader();
        queuedChunks.Remove(chunk);
        yield return null; // spread work over frames
    }

    #endregion
    // ----------------------------------------------------------------------

    #region Helpers

    /// <summary>
    /// Finds all chunks that might be affected by a brush centered at centerGrid with radiusGrid,
    /// converts the center into each chunk’s local grid space, and invokes the action.
    /// </summary>
    private void ApplyToAffectedChunks(Vector3 centerGrid, float radiusGrid, System.Action<ChunkCell, Vector3> perChunk)
    {
        int n = gridSize - 3;

        HashSet<Vector3Int> affected = new();

        // coarse search: consider 3×3×3 neighbor chunks around the hit
        for (int dx = -1; dx <= 1; dx++)
        for (int dy = -1; dy <= 1; dy++)
        for (int dz = -1; dz <= 1; dz++)
        {
            Vector3 offsetPosition = new(
                centerGrid.x + dx * (radiusGrid + 1f),
                centerGrid.y + dy * (radiusGrid + 1f),
                centerGrid.z + dz * (radiusGrid + 1f)
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
                affected.Add(index);
            }
        }

        foreach (var idx in affected)
        {
            Vector3 offset = idx - Vector3Int.one * numberOfChunks / 2;
            ChunkCell chunk = chunkMatrix[idx.x, idx.y, idx.z];

            // Convert world-grid center to this chunk’s local grid coords
            Vector3 localCenter = new(
                centerGrid.x - offset.x * (gridSize - 3),
                centerGrid.y - offset.y * (gridSize - 3),
                centerGrid.z - offset.z * (gridSize - 3)
            );

            perChunk?.Invoke(chunk, localCenter);
            EnqueueChunkForRefresh(chunk);
        }
    }

    public Vector3 SnapToGrid(Vector3 position)
    {
        float step = chunkSize / (gridSize - 3f);
        float x = Mathf.Round(position.x / step) * step; 
        float y = Mathf.Round(position.y / step) * step; 
        float z = Mathf.Round(position.z / step) * step; 
        return new Vector3(x, y, z);
    }

    #endregion



}
