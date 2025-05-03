using UnityEngine;

public class ChunkLoadAnimation : MonoBehaviour
{
    private readonly float speed = 3f;
    private Vector3 targetPos;

    /// Random delay for each chunk before playing animation.
    private bool useRandomDelay = false;
    private float waitTimer;
    private float timer;

    /// Ensure that the chunks are rendered before playing animation.
    private readonly bool waitUntilMeshCreated = true;
    private MeshFilter meshFilter;

    private void Start()
    {
        if (useRandomDelay)
            waitTimer = Random.Range(0f, 3f);
        
        targetPos = transform.position;
        transform.position = new Vector3(targetPos.x, -VoxelData.ChunkHeight, targetPos.z);

        if (waitUntilMeshCreated)
            meshFilter = gameObject.GetComponent<MeshFilter>();
        
    }

    private void Update()
    {
        // Random delay for each chunk before playing animation.
        if (useRandomDelay && timer < waitTimer)
        {
            timer += Time.deltaTime;
            return;
        }

        // Ensure that the chunks are rendered before playing animation.
        if (waitUntilMeshCreated && meshFilter.mesh.vertices.Length == 0)
            return;
        
        transform.position = Vector3.Lerp(transform.position, targetPos, Time.deltaTime * speed);
        if ((targetPos.y - transform.position.y) < 0.05f)
        {
            transform.position = targetPos;
            Destroy(this);
        }
    }
}