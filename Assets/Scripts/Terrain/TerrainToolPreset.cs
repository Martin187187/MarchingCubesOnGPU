using UnityEngine;

public enum BrushShape { Sphere, Wall, Floor }
public enum ToolOperation { None, Break, Build, Smooth }

[CreateAssetMenu(fileName = "TerrainToolPreset", menuName = "Terrain/Terrain Tool Preset")]
public class TerrainToolPreset : ScriptableObject
{
    [Header("Identity")]
    public string displayName = "New Tool";

    [Header("Operation")]
    public ToolOperation operation = ToolOperation.Break;
    [Tooltip("Main strength: negative to remove, positive to add. Smooth uses smoothStrength.")]
    public float strength = -0.04f;
    public float smoothStrength = 0.02f;

    [Header("Shape + Base Size")]
    public BrushShape shape = BrushShape.Sphere;

    [Tooltip("Sphere radius (base size).")]
    public float sphereRadius = 2f;

    [Tooltip("Box size (base size) for Wall/Floor.")]
    public Vector3 boxSize = new(6f, 0.3f, 6f);

    [Header("Runtime Scale Limits")]
    [Tooltip("Sphere radius clamp (min/max). Wheel scaling uses these limits.")]
    public Vector2 sphereRadiusRange = new Vector2(0.1f, 12f);

    [Tooltip("Box size clamp (per axis). Wheel scaling is uniform but clamped per axis.")]
    public Vector3 boxSizeMin = new Vector3(0.1f, 0.1f, 0.1f);
    public Vector3 boxSizeMax = new Vector3(24f, 24f, 24f);

    [Header("Placement / Anchoring")]
    public bool anchored = true;
    public bool lockYawWhileActive = true;
    public bool hideBrush = false; // ignored if operation == None (indicator always hidden)

    [Header("Snapping permissions")]
    public bool allowPositionSnap = true;
    public bool allowYawSnap = true;

    [Header("Type anchor (Break only)")]
    public bool allowTypeAnchor = false;

    [Header("Build material (Build only)")]
    public TerrainType fillType = TerrainType.Beton;
}