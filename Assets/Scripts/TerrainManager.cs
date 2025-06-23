using System;
using System.Collections.Generic;
using UnityEngine;

public class TerrainManager : MonoBehaviour
{
    public ComputeShader computeShader;
    public Material materialInstance;
    
    public Vector3Int gridSize;
    public int numVoxels = 10;
    public int chunkSize = 8;

    List<ComputeShaderMeshData2> meshDataList = new List<ComputeShaderMeshData2>();
    private ComputeShaderMeshData2[,,] meshDataMatrix; // 3D matrix to store mesh data
    
    public Queue<ComputeShaderMeshData2> meshDataToUpdate = new Queue<ComputeShaderMeshData2>();

    private float timeSinceLastUpdate = 0f;  // Timer to track time between updates
    public bool updateCollisionMesh = false;
    public float updateInterval = 0.1f;
    
    private GameObject cube;
    public bool use_cube = false;
    
    public Dictionary<TerrainType, float> terrainDictionary = new Dictionary<TerrainType, float>
    {
        { TerrainType.Grass, 1 },
        { TerrainType.Dirt, 1 },
        { TerrainType.Rock, 1.5f },
        { TerrainType.IronOre, 0.2f },
        { TerrainType.Marble, 1f }
    };
    
    
    public Dictionary<TerrainType, float> inventory = new Dictionary<TerrainType, float>
    {
        { TerrainType.Grass, 0 },
        { TerrainType.Dirt, 0 },
        { TerrainType.Rock, 0 },
        { TerrainType.IronOre, 0 },
        { TerrainType.Marble, 0 }
    };
    public void GenerateMesh(Vector3Int index, Vector3Int index2)
    {
        Vector3Int full = index + index2;
        GameObject obj = new GameObject($"ComputeShaderMeshObject {full}");
        obj.transform.position = full * chunkSize;
        obj.transform.parent = transform;
        obj.transform.localScale = Vector3.one * chunkSize/(numVoxels-3);
        
        
        ComputeShaderMeshData2 meshData = obj.AddComponent<ComputeShaderMeshData2>();

        // Assign ComputeShader and Material
        meshData.computeShader = computeShader;
        meshData.materialInstance = materialInstance;
        meshData.gridSize = numVoxels;
        meshData.chunkSize = chunkSize;
        meshData.index = full;
        meshData.terrainManager = this;
        
        meshDataList.Add(meshData);
        meshDataMatrix[index.x, index.y, index.z] = meshData; // Store in matrix
    }
    Vector3 SnapToGrid(Vector3 position, float gridSize)
    {
        float x = Mathf.Round(position.x / gridSize) * gridSize;
        float y = Mathf.Round(position.y / gridSize) * gridSize;
        float z = Mathf.Round(position.z / gridSize) * gridSize;
        return new Vector3(x, y, z);
    }
    void Start()
    {
        //Cursor.visible = false;
        //Cursor.lockState = CursorLockMode.Locked;

        meshDataMatrix = new ComputeShaderMeshData2[gridSize.x, gridSize.y, gridSize.z]; // Initialize the matrix
        
        int n = numVoxels - 3;
        Vector3Int start = new Vector3Int(-gridSize.x / 2, -gridSize.y / 2, -gridSize.z / 2);
        for (int i = 0; i < gridSize.x; i++)
        {
            for (int j = 0; j < gridSize.y; j++)
            {
                for (int k = 0; k < gridSize.z; k++)
                {
                    GenerateMesh(new Vector3Int(i, j, k), start);
                }
            }
        }

        if (use_cube)
        {
            float cubeSize = (float)chunkSize / (numVoxels - 3);
            cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.transform.localScale = new Vector3(cubeSize, cubeSize, cubeSize);

            Destroy(cube.GetComponent<Collider>());
        }
    }

    public void Update()
    {
        
        Ray ray2 = Camera.main.ScreenPointToRay(Input.mousePosition);
        if (use_cube & Physics.Raycast(ray2, out RaycastHit hit2))
        {
            Vector3 snappedPosition = SnapToGrid(hit2.point, (float)chunkSize / (numVoxels - 3));
            cube.transform.position = snappedPosition;
        }

        if(updateCollisionMesh)
        {

            timeSinceLastUpdate += Time.deltaTime;
            if (timeSinceLastUpdate >= updateInterval && meshDataToUpdate.Count > 0)
            {   
                var data = meshDataToUpdate.Dequeue();
                data.ReadVerticesFromComputeShader(); // Call once per second
                timeSinceLastUpdate = 0f; // Reset the timer after calling the method
                Debug.Log("updated");
            }
        }
        
        
        
        
        if (Input.GetKeyDown(KeyCode.K))
        {
            foreach (ComputeShaderMeshData2 meshData in meshDataList)
            {
                meshData.UpdateVoxelGridWithCube();
                meshData.ReadVerticesFromComputeShader();
            }
        }
        
        if (Input.GetMouseButton(0))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                EditChunks(hit.point, 0.05f, 4);
            }
        }
        
        if (Input.GetMouseButton(1))
        {
            Ray ray = Camera.main.ScreenPointToRay(Input.mousePosition);
            if (Physics.Raycast(ray, out RaycastHit hit))
            {
                
                Vector3 snappedPosition = SnapToGrid(hit.point, (float)chunkSize / (numVoxels - 3));
               EditChunks(snappedPosition, -0.01f, 4);
            }
        }
        

    }
    
    public void EditChunks(Vector3 point, float strength, float rad)
    {
        
        Vector3 hitPosition = point *  (float)(numVoxels-3)/chunkSize;
        int n = numVoxels - 3;
        float radius = rad *  (float)(numVoxels-3)/chunkSize; // The radius for the affected region (4 as in your example)
        
        
        // find out type from center chunk
        Vector3Int centerIndexC = new Vector3Int(
            Mathf.FloorToInt(hitPosition.x / n),
            Mathf.FloorToInt(hitPosition.y / n),
            Mathf.FloorToInt(hitPosition.z / n)
        );
        
        Vector3Int centerIndex = centerIndexC+Vector3Int.one * gridSize / 2;
        ComputeShaderMeshData2 centerMesh = meshDataMatrix[centerIndex.x, centerIndex.y, centerIndex.z];
        Vector3 centerAlignedPosition = new Vector3(
            hitPosition.x - centerIndexC.x*n,
            hitPosition.y - centerIndexC.y*n,
            hitPosition.z - centerIndexC.z*n
        );
        TerrainType terrainType = centerMesh.GetType(centerAlignedPosition);
        
        
        // Create a HashSet to store unique chunk indices to update
        HashSet<Vector3Int> affectedIndices = new HashSet<Vector3Int>();
        // Iterate over the range of hit positions by subtracting and adding the radius
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    
                    // Calculate the new position based on the hit position and the offsets
                    Vector3 offsetPosition = new Vector3(hitPosition.x + dx*radius, hitPosition.y + dy*radius, hitPosition.z + dz*radius);

                    // Calculate the chunk index based on the offset position
                    Vector3Int index = new Vector3Int(
                        Mathf.FloorToInt(offsetPosition.x / n),
                        Mathf.FloorToInt(offsetPosition.y / n),
                        Mathf.FloorToInt(offsetPosition.z / n)
                    )+Vector3Int.one * gridSize / 2;
                    
                    // Add the chunk index to the set of affected indices
                    // Ensure the index is within the valid grid range
                    if (index.x >= 0 && index.x < gridSize.x &&
                        index.y >= 0 && index.y < gridSize.y &&
                        index.z >= 0 && index.z < gridSize.z)
                    {
                        affectedIndices.Add(index);
                    }
                }
            }
        }

        // Iterate over the affected indices and update the chunks
        foreach (var affectedIndex in affectedIndices)
        {
            Vector3 yesir = affectedIndex - Vector3Int.one * gridSize / 2;
            ComputeShaderMeshData2 hitMeshData = meshDataMatrix[affectedIndex.x, affectedIndex.y, affectedIndex.z];
            Vector3 alignedPosition = new Vector3(
                hitPosition.x - yesir.x*n,
                hitPosition.y - yesir.y*n,
                hitPosition.z - yesir.z*n
            );
            if(strength>0)
                hitMeshData.UpdateVoxelGridWithSphere(alignedPosition, radius, strength*  (numVoxels-3)/chunkSize, terrainType);
            else
                hitMeshData.UpdateVoxelGridWithSphere(alignedPosition, radius, strength*  (numVoxels-3)/chunkSize, terrainType);
                

            // Add the mesh data to the update queue if it's not already there
            if (!meshDataToUpdate.Contains(hitMeshData))
            {
                meshDataToUpdate.Enqueue(hitMeshData);
            }
        }
    }

}
