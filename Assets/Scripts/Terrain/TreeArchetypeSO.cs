using UnityEngine;
[CreateAssetMenu(menuName = "Terrain/Structures/Tree Archetype", fileName = "TreeArchetype")]
public class TreeArchetypeSO : ScriptableObject
{
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
    public bool forceReplace = false; // allow placing into air only if you prefer

    [Header("Ground constraints")]
    [Tooltip("If empty, any solid ground is allowed. Otherwise the ground voxel must be one of these.")]
    public TerrainType[] allowedGroundTypes;

    [Tooltip("How deep the allowed ground must be directly below base (in world units). 0 = just the surface voxel.")]
    public float requiredSoilDepth = 0f;

    [Header("Roots")]
    public MinMaxi roots = new() { min = 2, max = 5 };
    public MinMaxf rootLen = new() { min = 2.5f, max = 6.5f };
    public MinMaxf rootRadius = new() { min = 0.12f, max = 0.28f };

    [Tooltip("End radius = start radius * rootTaper01")]
    [Range(0.1f, 1f)] public float rootTaper01 = 0.5f;

    [Tooltip("How far below the surface the root starts (world units).")]
    [Range(0f, 1f)] public float rootStartDepth = 0.35f;

    [Tooltip("Initial downward pitch in degrees (from horizontal).")]
    public MinMaxf rootPitchDeg = new() { min = 10f, max = 25f };

    [Tooltip("Initial yaw around the trunk in degrees.")]
    public MinMaxf rootYawJitterDeg = new() { min = 0f, max = 360f };

    [Tooltip("Per-segment small turn jitter in degrees (applies to yaw and a bit to pitch).")]
    public MinMaxf rootTurnJitterDeg = new() { min = 3f, max = 12f };

    [Tooltip("Per-segment lateral wobble (world units).")]
    public MinMaxf rootWobble = new() { min = 0.1f, max = 0.35f };

    [Tooltip("How many segments to form each root polyline.")]
    public MinMaxi rootSegments = new() { min = 3, max = 6 };

    [Tooltip("Strength applied when carving roots.")]
    public float rootStrengthWorld = 1f;
    [Tooltip("Extra downward push per segment as a fraction of segment length.")]
    [Range(0f, 0.6f)] public float rootDownBiasPerSegment = 0.1f;

    [Tooltip("Max depth below base the root is allowed to reach (0 = no cap).")]
    public float rootDepthCap = 0f;
}
