using UnityEngine;

public class ComputeShaderMeshData : MonoBehaviour
{
    public ComputeShader computeShader;
    public Material materialPrefab; // Use this as a template for creating new materials

    private ComputeBuffer verticesBuffer;
    private ComputeBuffer counterBuffer;

    [Header("Grid Settings")]
    public int gridSize = 2; // Exposed in the Inspector, can be set dynamically

    private int vertexCount;
    private Material materialInstance;

    struct VertexData
    {
        public Vector3 position;
        public Vector3 normal;
    }
    void Start()
    {
        // Calculate number of vertices required for generating triangles (6 vertices per grid cell)
        vertexCount = (gridSize - 1) * (gridSize - 1) * 6;

        // Create buffers for positions and normals
        verticesBuffer = new ComputeBuffer(vertexCount, sizeof(float) * 6 * 3 , ComputeBufferType.Append);
        counterBuffer = new ComputeBuffer(4, sizeof(int), ComputeBufferType.IndirectArguments);

        // Create a new material instance for this object (this is key to having unique materials)
        materialInstance = Instantiate(materialPrefab);

        // Pass gridSize to the compute shader
        computeShader.SetInt("gridSize", gridSize);

        // Assign buffers to compute shader
        computeShader.SetBuffer(0, "vertices", verticesBuffer);
        computeShader.SetBuffer(0, "counter", counterBuffer);
        verticesBuffer.SetCounterValue(0);
        
        int[] args = { 0, 1, 0, 0 };
        counterBuffer.SetData(args);
        
        // Dispatch compute shader
        computeShader.Dispatch(0, gridSize, gridSize, 1);
        
        // Assign buffers to material
        materialInstance.SetBuffer("vertices", verticesBuffer);
        materialInstance.SetMatrix("_ObjectToWorld", transform.localToWorldMatrix);

    }

    void Update()
    {
        // Draw the Mesh using DrawProcedural
        Graphics.DrawProceduralIndirect(materialInstance, new Bounds(Vector3.zero, Vector3.one * 10), MeshTopology.Triangles, counterBuffer);
    }

    void OnDestroy()
    {
        // Release buffers
        verticesBuffer.Release();
        counterBuffer.Release();
    }
}
