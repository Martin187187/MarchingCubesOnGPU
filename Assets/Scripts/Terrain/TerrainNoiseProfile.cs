using UnityEngine;

[CreateAssetMenu(fileName = "NewTerrainNoiseProfile", menuName = "Terrain/Terrain Noise Profile")]
public class TerrainNoiseProfile : ScriptableObject
{
    public TerrainType type;
    [Header("Noise Settings")]
    public Vector3 offset = Vector3.zero;
    [Range(1, 12)]
    public int octaves = 4;
    
    public float noiseScale = 0.5f;
    [Range(0.1f, 5f)]
    public float lacunarity = 2f;
    [Range(0f, 5f)]
    public float persistence = 0.5f;

    [Header("Weight Settings")]
    public float noiseWeight = 1f;
    public float weightMultiplier = 1f;

    [Header("Floor Settings")]
    public float floorOffset = 0f;
    public float hardFloor = 0f;
    public float hardFloorWeight = 0f;
}