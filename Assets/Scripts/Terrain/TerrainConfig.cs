using UnityEngine;

[CreateAssetMenu(menuName = "Terrain/TerrainConfig", fileName = "TerrainConfig")]
public class TerrainConfig : ScriptableObject
{
    [Header("Grid / World")]
    public int gridSize = 32;
    public int chunkSize = 16;
    [Range(0f, 1f)] public float isoLevel = 0.5f;

    [Header("Streaming (in chunks)")]
    public int viewRadiusChunks = 6;
    public int verticalRadiusChunks = 2;
    public int unloadHysteresis = 1;
    public int colliderRadiusChunks = 3;
    public float wantedUpdateInterval = 0.25f;

    [Header("Budgets (per frame)")]
    public int budgetDensityPerFrame = 2;
    public int budgetMeshPerFrame = 1;
    public int budgetColliderPromotionsPerFrame = 2;

    [Header("Pool")]
    public int prewarmChunks = 64;
    public int maxChunks = 512;

    [Header("Foliage (optional)")]
    public FoliageSettingsSO foliageSettings;

    [Header("Structures")]
    public int budgetStructureStampsPerFrame = 24; // you already had this
    public int budgetStructureChunksPerFrame = 2;  // new: how many chunks structure-stage processes
    public TreeSpawnerSO treeSpawner;  

    public ChunkCell.ChunkSettings ChunkSettings => new()
    {
        gridSize = gridSize,
        chunkSize = chunkSize,
        isoLevel = isoLevel
    };
}
