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

    void Awake()
    {
        if (Instance && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        // Optional: DontDestroyOnLoad(gameObject);
    }

    public void SpawnPebbles(Vector3 position)
    {
        Debug.Log("Spawning Pebbles");
        if (!pebblePrefab) { Debug.LogWarning("PebbleSpawner: prefab missing"); return; }

        for (int i = 0; i < pebbleCount; i++)
        {
            Vector3 spawnPos = position + Random.insideUnitSphere * spawnRadius;
            GameObject pebble = Instantiate(pebblePrefab, spawnPos, Random.rotation);

            float s = Random.Range(scaleRange.x, scaleRange.y);
            pebble.transform.localScale = Vector3.one * s;

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