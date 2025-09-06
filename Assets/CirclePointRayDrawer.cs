using System.Collections.Generic;
using UnityEngine;

public class CirclePointRayDrawer : MonoBehaviour
{
    [Header("Main Circle")]
    public Transform centerTransform;             // optional
    public Vector3 center = Vector3.zero;         // used if transform is null
    public float radius = 25f;

    [Header("Chunk Sampling")]
    public float chunkSize = 8f;
    public int seed = 12345;
    [Tooltip("Candidates per chunk square (chunkSize x chunkSize). Typical: 16.")]
    public int pointsPerChunk = 16;

    [Header("Ray Drawing")]
    public float rayLength = 200f;
    public Color rayColor = Color.cyan;
    public bool liveUpdate = true;

    private List<Vector3> _points;

    void Start()
    {
        Recompute();
    }

    void Update()
    {
        if (liveUpdate) Recompute();
        if (_points == null || _points.Count == 0) return;

        for (int i = 0; i < _points.Count; i++)
        {
            Debug.DrawRay(_points[i], Vector3.up * rayLength, rayColor, 0f, false);
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        if (Application.isPlaying) return;
        if (liveUpdate) Recompute();
        if (_points == null) return;

        Gizmos.color = rayColor;
        foreach (var p in _points)
            Gizmos.DrawLine(p, p + Vector3.up * Mathf.Max(1f, rayLength));

        // optional: draw circle boundary
        UnityEditor.Handles.color = new Color(rayColor.r, rayColor.g, rayColor.b, 0.5f);
        var c = EffectiveCenter();
        UnityEditor.Handles.DrawWireDisc(new Vector3(c.x, c.y, c.z), Vector3.up, radius);
    }
#endif

    void OnValidate()
    {
        radius = Mathf.Max(0f, radius);
        chunkSize = Mathf.Max(0.0001f, chunkSize);
        pointsPerChunk = Mathf.Max(0, pointsPerChunk);
        rayLength = Mathf.Max(0f, rayLength);

        if (!Application.isPlaying) Recompute();
    }

    void Recompute()
    {
        var c = EffectiveCenter();
        _points = DeterministicCircleSampler.GeneratePointsInCircleXZ(
            c, radius, chunkSize, seed, pointsPerChunk
        );
    }

    Vector3 EffectiveCenter()
    {
        var c = centerTransform ? centerTransform.position : center;
        return new Vector3(c.x, c.y, c.z);
    }
}
