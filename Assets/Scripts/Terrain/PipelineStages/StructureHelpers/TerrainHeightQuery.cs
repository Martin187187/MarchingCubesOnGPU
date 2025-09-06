using UnityEngine;

/// <summary>
/// Singleton service to query ground height at a world position using noise layers.
/// </summary>
public class TerrainHeightQuery : MonoBehaviour
{

    public struct TerrainProbe
    {
        public float height;
        public TerrainType terrainType;
        
    }
    public static TerrainHeightQuery Instance { get; private set; }

    [Tooltip("ComputeShader that contains a kernel named 'QueryHeight'.")]
    public ComputeShader groundHeightShader;

    [HideInInspector] public TerrainController controller;

    ComputeBuffer _result;
    int _kernelIndex = -1;
    readonly float[] _data = new float[1];
    bool _ready;

    void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        DontDestroyOnLoad(gameObject); // optional, keep across scenes
        Init();
    }

    void OnDisable() => Release();

    public void Init()
    {
        Release();

        if (controller == null || controller.config == null || groundHeightShader == null)
        {
            _ready = false;
            return;
        }

        if (controller.terrainLayers == null || controller.terrainLayers.Count == 0)
        {
            _ready = false;
            return;
        }

        _kernelIndex = groundHeightShader.FindKernel("QueryHeight");
        _result = new ComputeBuffer(1, sizeof(float));

        var s = controller.config.ChunkSettings;
        groundHeightShader.SetFloat("isoLevel", s.isoLevel);
        groundHeightShader.SetInt("numPointsPerAxis", s.gridSize);
        groundHeightShader.SetFloat("chunkSize", s.chunkSize);

        groundHeightShader.SetBuffer(_kernelIndex, "retrievedPositionBuffer", _result);

        _ready = true;
    }

    void Release()
    {
        if (_result != null)
        {
            _result.Dispose();
            _result = null;
        }
        _ready = false;
        _kernelIndex = -1;
    }

    public TerrainProbe TryQuery(Vector3 worldPos)
    {
        TerrainProbe result = new TerrainProbe();

        groundHeightShader.SetVector("position", new Vector4(worldPos.x, worldPos.y, worldPos.z, 0f));

        float bestH = float.NegativeInfinity;
        int bestTypeInt = 0;

        var layers = controller.terrainLayers;
        for (int i = 0; i < layers.Count; i++)
        {
            var p = layers[i];
            if (p == null) continue;

            SetProfileParams(p);
            groundHeightShader.Dispatch(_kernelIndex, 1, 1, 1);
            _result.GetData(_data);
            float h = _data[0];

            if (h > bestH)
            {
                bestH = h;
                bestTypeInt = (int)p.type;
            }
        }

        result.height = bestH;
        result.terrainType = (TerrainType)bestTypeInt;
        return result;
    }

    void SetProfileParams(TerrainNoiseProfile p)
    {
        groundHeightShader.SetVector("offset", new Vector4(p.offset.x, p.offset.y, p.offset.z, 0));
        groundHeightShader.SetInt("octaves", p.octaves);
        groundHeightShader.SetFloat("lacunarity", p.lacunarity);
        groundHeightShader.SetFloat("persistence", p.persistence);
        groundHeightShader.SetFloat("noiseScale", p.noiseScale);
        groundHeightShader.SetFloat("noiseWeight", p.noiseWeight);
        groundHeightShader.SetFloat("floorOffset", p.floorOffset);
        groundHeightShader.SetFloat("weightMultiplier", p.weightMultiplier);
        groundHeightShader.SetFloat("hardFloor", p.hardFloor);
        groundHeightShader.SetFloat("hardFloorWeight", p.hardFloorWeight);
        groundHeightShader.SetInt("val", (int)p.type);
    }
}
