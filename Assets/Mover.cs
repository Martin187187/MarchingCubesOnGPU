using UnityEngine;

public class Mover : MonoBehaviour
{
    public TerrainManager terrain;
    public float moveSpeed = 5f;

    public float positionThreshold = 0.1f;
    private Vector3 lastEditPosition = Vector3.positiveInfinity;

    void Start()
    {
        if (terrain == null)
        {
            terrain = FindObjectOfType<TerrainManager>();
        }
    }

void Update()
{
    Vector3 currentPosition = transform.position;

    if (terrain != null && Vector3.Distance(currentPosition, lastEditPosition) > positionThreshold)
    {
        terrain.EditChunks(currentPosition, 0.02f, 0.8f);
        lastEditPosition = currentPosition;
    }

    // Movement
    float moveX = Input.GetAxis("Horizontal");
    float moveZ = Input.GetAxis("Vertical");
    Vector3 move = new Vector3(moveX, 0, moveZ) * moveSpeed * Time.deltaTime;
    transform.position += move;
}
}
