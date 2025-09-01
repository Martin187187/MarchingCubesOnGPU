using UnityEngine;

[CreateAssetMenu(menuName = "Terrain/Structures/Tree Archetype", fileName = "TreeArchetype")]
public class TreeArchetypeSO : ScriptableObject
{
    public enum RootPlacementMode { ReplaceOnly, Normal, ForceReplace }

    [Header("Trunk")]
    public MinMaxf trunkHeight = new() { min = 6, max = 12 };
    public MinMaxf trunkRadius = new() { min = 0.35f, max = 0.6f };
    [Range(0.1f, 2f)] public float voxelStampStep = 0.5f; // sphere-to-sphere step along capsules

    [Header("Branches")]
    public MinMaxi branches = new() { min = 2, max = 5 };
    public MinMaxf branchLen = new() { min = 2.5f, max = 5.5f };
    public MinMaxf branchRadius = new() { min = 0.18f, max = 0.32f };
    public MinMaxf branchPitchDeg = new() { min = 20, max = 45 }; // up tilt
    public MinMaxf branchYawJitterDeg = new() { min = 0, max = 360 };

    [Header("Leaves")]
    public MinMaxi leafBlobsPerBranch = new() { min = 2, max = 4 };
    public MinMaxf leafBlobRadius = new() { min = 0.7f, max = 1.3f };

    [Header("Distribution shape")]
    [Range(0f, 1f)] public float branchStartHeight01 = 0.35f;

    [Header("Strength/Replace")]
    public float strengthWorld = 1f;
    public bool forceReplace = false; // trunk/branches/leaves only

    [Header("Ground constraints")]
    public TerrainType[] allowedGroundTypes;
    public float requiredSoilDepth = 0f;

    [Header("Roots")]
    public MinMaxi roots = new() { min = 2, max = 5 };
    public MinMaxf rootLen = new() { min = 2.5f, max = 6.5f };
    public MinMaxf rootRadius = new() { min = 0.12f, max = 0.28f };
    [Range(0.1f, 1f)] public float rootTaper01 = 0.5f;
    [Range(0f, 1f)] public float rootStartDepth = 0.35f;
    public MinMaxf rootPitchDeg = new() { min = 10f, max = 25f };
    public MinMaxf rootYawJitterDeg = new() { min = 0f, max = 360f };
    public MinMaxf rootTurnJitterDeg = new() { min = 3f, max = 12f };
    public MinMaxf rootWobble = new() { min = 0.1f, max = 0.35f };
    public MinMaxi rootSegments = new() { min = 3, max = 6 };
    public float rootStrengthWorld = 1f;
    [Range(0f, 0.6f)] public float rootDownBiasPerSegment = 0.1f;
    public float rootDepthCap = 0f;

    [Tooltip("How roots write into the world (independent of trunk/branches).")]
    public RootPlacementMode rootPlacement = RootPlacementMode.ReplaceOnly; // default keeps current behavior
}
