using System.Collections.Generic;
using UnityEngine;

public class PebbleSpawner : MonoBehaviour
{
    public static PebbleSpawner Instance { get; private set; }

    [Header("Pebble Settings")]
    public GameObject pebblePrefab;
    public int pebbleCount = 10;
    public Vector2 scaleRange = new Vector2(0.1f, 0.4f);
    public float spawnRadius = 0.5f;
    public float launchForce = 3f;
    public float upBias = 0.5f;

    [Header("Lifetime")]
    public float lifetime = 10f;

    [Header("Textures")]
    [Tooltip("Texture2DArray with one slice per terrain type.")]
    public Texture2DArray terrainTextures;

    [Tooltip("Material that uses the Custom/PebbleArray_AlbedoFromSlice shader below.")]
    public Material pebbleBaseMaterial;

    // Shader property IDs
    static readonly int _AlbedoArrayID = Shader.PropertyToID("_AlbedoArray");
    static readonly int _TextureIndexID = Shader.PropertyToID("_TextureIndex");

    void Awake()
    {
        if (Instance && Instance != this) { Destroy(gameObject); return; }
        Instance = this;

        if (!pebblePrefab) Debug.LogWarning("PebbleSpawner: prefab missing");
        if (!pebbleBaseMaterial) Debug.LogWarning("PebbleSpawner: base material missing");
        if (!terrainTextures) Debug.LogWarning("PebbleSpawner: Texture2DArray missing");

        // Sanity: ensure the shader has the properties we expect
        if (pebbleBaseMaterial && !pebbleBaseMaterial.HasProperty(_AlbedoArrayID))
            Debug.LogError("PebbleSpawner: Material's shader does not expose _AlbedoArray (2DArray). Assign the shader provided below.");

        // Bind the Texture2DArray to the material once
        if (pebbleBaseMaterial && terrainTextures)
            pebbleBaseMaterial.SetTexture(_AlbedoArrayID, terrainTextures);
    }

    /// <summary>
    /// Spawns pebbles proportionally based on terrain type changes.
    /// terrainChangeMap values > 0 indicate removed terrain (generate pebbles).
    /// </summary>
    public void SpawnPebbles(Vector3 position, Dictionary<TerrainType, int> terrainChangeMap)
    {
        if (!pebblePrefab || !pebbleBaseMaterial || !terrainTextures) return;

        // Filter: only positive counts
        Dictionary<TerrainType, int> positiveCounts = new();
        int totalPositive = 0;
        foreach (var kvp in terrainChangeMap)
        {
            if (kvp.Value > 0)
            {
                positiveCounts[kvp.Key] = kvp.Value;
                totalPositive += kvp.Value;
            }
        }
        if (totalPositive == 0) return;

        int depth = terrainTextures.depth;

        foreach (var kvp in positiveCounts)
        {
            // Proportional spawn count
            int count = Mathf.RoundToInt((kvp.Value / (float)totalPositive) * pebbleCount);

            // Map TerrainType -> slice index
            int textureIndex = Mathf.Clamp((int)kvp.Key, 0, depth - 1);

            for (int i = 0; i < count; i++)
            {
                Vector3 spawnPos = position + Random.insideUnitSphere * spawnRadius;
                GameObject pebble = Instantiate(pebblePrefab, spawnPos, Random.rotation);

                float s = Random.Range(scaleRange.x, scaleRange.y);
                pebble.transform.localScale = Vector3.one * s;

                if (pebble.TryGetComponent<Renderer>(out var renderer))
                {
                    // Ensure the renderer uses our shared base material (instancing-friendly)
                    renderer.sharedMaterial = pebbleBaseMaterial;

                    // Per-renderer slice selection via MPB
                    var mpb = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(mpb);
                    mpb.SetFloat(_TextureIndexID, textureIndex); // float is fine here
                    renderer.SetPropertyBlock(mpb, 0);            // submesh 0
                }

                if (pebble.TryGetComponent<Rigidbody>(out var rb))
                {
                    Vector3 dir = (Random.onUnitSphere + Vector3.up * upBias).normalized;
                    rb.AddForce(dir * launchForce, ForceMode.Impulse);
                    rb.AddTorque(Random.onUnitSphere * launchForce, ForceMode.Impulse);
                }

                Destroy(pebble, lifetime);
            }
        }
    }
}

