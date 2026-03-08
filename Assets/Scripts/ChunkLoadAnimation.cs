using UnityEngine;

public class ChunkLoadAnimation : MonoBehaviour
{
    private readonly float _speed = 3f;
    private Vector3 _targetPos;

    /// Random delay for each chunk before playing animation.
    private bool _useRandomDelay = false;

    private float _waitTimer;
    private float _timer;

    /// <summary>
    /// Temporarily disables the animation component and snaps the chunk's active position to an underground offset.
    /// Used by the pool to prevent a 1-frame visual flash before the animation officially starts.
    /// </summary>
    /// <param name="targetPosition">The final resting world-position the chunk is meant to occupy.</param>
    public void ResetToUnderground(Vector3 targetPosition)
    {
        _targetPos = targetPosition;
        enabled = false;
        transform.position = new Vector3(_targetPos.x, _targetPos.y - VoxelData.ChunkHeight, _targetPos.z);
    }

    /// <summary>
    /// Resets the internal timer and enables the component, triggering the chunk to rise smoothly
    /// from its underground offset towards its final resting position.
    /// </summary>
    public void StartAnimation()
    {
        _timer = 0f;

        if (_useRandomDelay)
            _waitTimer = Random.Range(0f, 3f);

        enabled = true;
    }

    private void Update()
    {
        // Random delay for each chunk before playing animation.
        if (_useRandomDelay && _timer < _waitTimer)
        {
            _timer += Time.deltaTime;
            return;
        }

        transform.position = Vector3.Lerp(transform.position, _targetPos, Time.deltaTime * _speed);
        if (_targetPos.y - transform.position.y < 0.05f)
        {
            transform.position = _targetPos;
            enabled = false; // Disable instead of Destroy to reuse with component caching
        }
    }
}
