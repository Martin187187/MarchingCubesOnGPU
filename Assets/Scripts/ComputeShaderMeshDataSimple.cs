using UnityEngine;

public class ComputeShaderMeshDataSimple : MonoBehaviour
{
    public Material materialPrefab; // Use this as a template for creating new materials

    [Header("Grid Settings")]
    public int gridSize = 2; // Exposed in the Inspector, can be set dynamically

    private int vertexCount;
    private Material materialInstance;

    void Start()
    {
        // Calculate number of vertices required for generating triangles (6 vertices per grid cell)
        vertexCount = (gridSize - 1) * (gridSize - 1) * 6;


        // Create a new material instance for this object (this is key to having unique materials)
        materialInstance = Instantiate(materialPrefab);


        // Assign buffers to material
        materialInstance.SetMatrix("_ObjectToWorld", transform.localToWorldMatrix);
        
    }

    void Update()
    {
        // Draw the Mesh using DrawProcedural
        Graphics.DrawProcedural(materialInstance, new Bounds(Vector3.zero, Vector3.one * 10), MeshTopology.Triangles, vertexCount);
    }

}
