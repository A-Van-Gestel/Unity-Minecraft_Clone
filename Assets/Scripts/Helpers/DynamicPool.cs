using System;
using System.Collections.Generic;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// A generic object pool that handles storage, lazy creation, and amortized cleanup (drip-feed pruning).
    /// </summary>
    /// <typeparam name="T">The type of object to pool.</typeparam>
    public class DynamicPool<T> where T : class
    {
        // Internal Storage
        private readonly Stack<T> _pool = new Stack<T>();

        // Delegates for type-specific behavior
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onReturnAction; // Optional: Reset state on return
        private readonly Action<T> _destroyAction;

        // Statistics
        public int PooledCount => _pool.Count;
        public int ActiveCount { get; private set; }

        // Pruning State
        private float _cleanupTimer = 0f;
        private const float CLEANUP_INTERVAL = 0.05f; // 20 checks/sec

        /// <summary>
        /// Creates a new pool.
        /// </summary>
        /// <param name="createFunc">Function to create a new instance when pool is empty.</param>
        /// <param name="destroyAction">Action to permanently destroy an instance (cleanup/pruning).</param>
        /// <param name="onReturnAction">Optional action to perform when item is returned (e.g. disable GameObject).</param>
        public DynamicPool(Func<T> createFunc, Action<T> destroyAction, Action<T> onReturnAction = null)
        {
            _createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
            _destroyAction = destroyAction ?? throw new ArgumentNullException(nameof(destroyAction));
            _onReturnAction = onReturnAction;
        }

        public T Get()
        {
            ActiveCount++;
            if (_pool.Count > 0)
            {
                return _pool.Pop();
            }

            return _createFunc();
        }

        public void Return(T item)
        {
            if (item == null) return;

            ActiveCount--;
            _onReturnAction?.Invoke(item);
            _pool.Push(item);
        }

        public void Clear()
        {
            while (_pool.Count > 0)
            {
                _destroyAction(_pool.Pop());
            }

            ActiveCount = 0;
        }

        /// <summary>
        /// Runs the amortized cleanup logic. Destroys 1 item per call if pool exceeds capacity.
        /// </summary>
        /// <param name="maxCapacity">The target maximum number of items to keep in the pool.</param>
        public void UpdatePruning(int maxCapacity)
        {
            if (_pool.Count <= maxCapacity) return;

            _cleanupTimer += Time.deltaTime;
            if (_cleanupTimer >= CLEANUP_INTERVAL)
            {
                _cleanupTimer = 0;
                // Pop and destroy ONE item to spread GC cost
                T item = _pool.Pop();
                _destroyAction(item);
            }
        }
    }
}
