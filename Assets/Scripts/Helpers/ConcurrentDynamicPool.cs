using System;
using System.Collections.Generic;
using UnityEngine;

namespace Helpers
{
    /// <summary>
    /// A generic object pool that handles storage, lazy creation, and amortized cleanup (drip-feed pruning).
    /// Tread safe version of <see cref="DynamicPool"/>
    /// Uses locking to ensure safety during async serialization/deserialization.
    /// </summary>
    /// <typeparam name="T">The type of object to pool.</typeparam>
    public class ConcurrentDynamicPool<T> where T : class
    {
        // Internal Storage
        private readonly Stack<T> _pool = new Stack<T>();

        // Delegates for type-specific behavior
        private readonly Func<T> _createFunc;
        private readonly Action<T> _onReturnAction; // Optional: Reset state on return
        private readonly Action<T> _destroyAction;

        // The synchronization object
        private readonly object _lock = new object();

        // Statistics
        public int PooledCount
        {
            get
            {
                lock (_lock) return _pool.Count;
            }
        }

        public int ActiveCount { get; private set; } // Atomic update recommended if strict accuracy needed, but volatile int is okay for debug stats

        // Pruning State
        private float _cleanupTimer = 0f;
        private const float CLEANUP_INTERVAL = 0.05f; // 20 checks/sec

        /// <summary>
        /// Creates a new pool.
        /// </summary>
        /// <param name="createFunc">Function to create a new instance when pool is empty.</param>
        /// <param name="destroyAction">Action to permanently destroy an instance (cleanup/pruning).</param>
        /// <param name="onReturnAction">Optional action to perform when item is returned (e.g. disable GameObject).</param>
        public ConcurrentDynamicPool(Func<T> createFunc, Action<T> destroyAction, Action<T> onReturnAction = null)
        {
            _createFunc = createFunc ?? throw new ArgumentNullException(nameof(createFunc));
            _destroyAction = destroyAction ?? throw new ArgumentNullException(nameof(destroyAction));
            _onReturnAction = onReturnAction;
        }

        public T Get()
        {
            lock (_lock)
            {
                ActiveCount++;
                if (_pool.Count > 0)
                {
                    return _pool.Pop();
                }
            }

            // Create OUTSIDE the lock to minimize contention/blocking time
            return _createFunc();
        }

        public void Return(T item)
        {
            if (item == null) return;

            // Optional: Execute Reset outside lock if it touches only local state?
            // Safer to do it before pushing to ensure no one pops a dirty object.
            _onReturnAction?.Invoke(item);

            lock (_lock)
            {
                ActiveCount--;
                _pool.Push(item);
            }
        }

        public void Clear()
        {
            lock (_lock)
            {
                while (_pool.Count > 0)
                {
                    _destroyAction(_pool.Pop());
                }

                ActiveCount = 0;
            }
        }

        /// <summary>
        /// Runs the amortized cleanup logic. Destroys 1 item per call if pool exceeds capacity.
        /// </summary>
        /// <param name="maxCapacity">The target maximum number of items to keep in the pool.</param>
        public void UpdatePruning(int maxCapacity)
        {
            // Pruning is typically called from Main Thread Update.
            // Check count inside lock or loosely outside? Loosely is fine for optimization.
            if (PooledCount <= maxCapacity) return;

            _cleanupTimer += Time.deltaTime;
            if (_cleanupTimer >= CLEANUP_INTERVAL)
            {
                _cleanupTimer = 0;
                T item = null;

                lock (_lock)
                {
                    if (_pool.Count > maxCapacity)
                    {
                        item = _pool.Pop();
                    }
                }

                // Destroy OUTSIDE lock
                if (item != null) _destroyAction(item);
            }
        }
    }
}
