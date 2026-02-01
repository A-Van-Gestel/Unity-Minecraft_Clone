using UnityEngine;

public class ChunkLoadAnimation : MonoBehaviour
{
    private readonly float _speed = 3f;
    private Vector3 _targetPos;

    /// Random delay for each chunk before playing animation.
    private bool _useRandomDelay = false;
    private float _waitTimer;
    private float _timer;

    /// Ensure that the chunks are rendered before playing animation.
    private bool _waitUntilMeshCreated = true;
    private MeshFilter _meshFilter;

    private void Start()
    {
        if (_useRandomDelay)
            _waitTimer = Random.Range(0f, 3f);
        
        _targetPos = transform.position;
        transform.position = new Vector3(_targetPos.x, -VoxelData.ChunkHeight, _targetPos.z);

        if (_waitUntilMeshCreated)
            _meshFilter = gameObject.GetComponent<MeshFilter>();
        
    }

    private void Update()
    {
        // Random delay for each chunk before playing animation.
        if (_useRandomDelay && _timer < _waitTimer)
        {
            _timer += Time.deltaTime;
            return;
        }

        // Ensure that the chunks are rendered before playing animation.
        if (_waitUntilMeshCreated && _meshFilter.mesh.vertices.Length == 0)
            return;
        
        transform.position = Vector3.Lerp(transform.position, _targetPos, Time.deltaTime * _speed);
        if (_targetPos.y - transform.position.y < 0.05f)
        {
            transform.position = _targetPos;
            Destroy(this);
        }
    }
}