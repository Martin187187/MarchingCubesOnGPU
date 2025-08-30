using UnityEngine;

[CreateAssetMenu(menuName="Terrain/Structures/Tree Spawner", fileName="TreeSpawner")]
public class TreeSpawnerSO : ScriptableObject
{
    [Header("Archetypes (weighted)")]
    public TreeArchetypeSO[] archetypes;
    public float[] weights; // same length as archetypes; if empty -> uniform

    [Header("Per-chunk density")]
    public MinMaxi treesPerChunk = new(){min=1, max=4};
    public float minDistanceBetweenTrees = 3f; // coarse Poisson

    [Header("Placement constraints")]
    public MinMaxf altitudeWorld = new(){min=-50, max=9999};
    [Range(0,60)] public float maxSlopeDeg = 30f;

    [Tooltip("If not empty, trees can spawn only on these ground types (applies to all archetypes).")]
    public TerrainType[] allowedGroundTypes;
    [Header("Randomness")]
    public int worldSeed = 12345;

    public TreeArchetypeSO PickArchetype(System.Random rng)
    {
        if (archetypes == null || archetypes.Length == 0) return null;
        if (weights == null || weights.Length != archetypes.Length)
            return archetypes[rng.Next(0, archetypes.Length)];

        float sum = 0f; for (int i=0;i<weights.Length;i++) sum += Mathf.Max(0f, weights[i]);
        float t = (float)rng.NextDouble() * Mathf.Max(sum, 1e-5f);
        for (int i=0;i<weights.Length;i++){
            t -= Mathf.Max(0f, weights[i]);
            if (t <= 0) return archetypes[i];
        }
        return archetypes[archetypes.Length-1];
    }
}
