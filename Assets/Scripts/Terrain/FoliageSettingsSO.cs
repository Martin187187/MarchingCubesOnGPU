using UnityEngine;

[CreateAssetMenu(menuName = "Terrain/FoliageSettings", fileName = "FoliageSettings")]
public class FoliageSettingsSO : ScriptableObject
{
    public GameObject[] prefabs;
    public float maxSlopeDeg = 25f;
    public float targetsPerArea = 10f;
    [Range(0f, 360f)] public float yawJitterDeg = 360f;
    [Range(0f, 20f)] public float tiltJitterDeg = 4f;
    public float positionJitter = 0.05f;
    public Vector2 uniformScaleRange = new(0.9f, 1.1f);

    public ChunkCell.FoliageSettings ToChunkFoliage() => new()
    {
        prefabs = prefabs,
        maxSlopeDeg = maxSlopeDeg,
        targetsPerArea = targetsPerArea,
        yawJitterDeg = yawJitterDeg,
        tiltJitterDeg = tiltJitterDeg,
        positionJitter = positionJitter,
        uniformScaleRange = uniformScaleRange
    };
}
