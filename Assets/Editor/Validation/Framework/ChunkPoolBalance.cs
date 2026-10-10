using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Snapshot of the concurrent data, section and light-queue pools' active counts on the stub
    /// <c>World.Instance</c>, for leak balance checks around a chunk operation. The single list of pooled resources a
    /// balance check covers, so a newly pooled one is checked everywhere at once.
    /// </summary>
    public readonly struct ChunkPoolBalance
    {
        private readonly int _activeData;
        private readonly int _activeSections;
        private readonly int _activeLightQueues;

        private ChunkPoolBalance(int activeData, int activeSections, int activeLightQueues)
        {
            _activeData = activeData;
            _activeSections = activeSections;
            _activeLightQueues = activeLightQueues;
        }

        /// <summary>Captures the current active counts.</summary>
        /// <returns>The snapshot.</returns>
        public static ChunkPoolBalance Capture() => new ChunkPoolBalance(World.Instance.ChunkPool.ActiveData,
            World.Instance.ChunkPool.ActiveSections, World.Instance.ChunkPool.ActiveLightQueues);

        /// <summary>Asserts the active counts match this snapshot (nothing leaked, nothing over-returned), logging PASS or FAIL.</summary>
        /// <param name="label">Assertion label for the log.</param>
        /// <returns>True when every pool is balanced.</returns>
        public bool AssertUnchanged(string label)
        {
            ChunkPoolBalance now = Capture();
            bool balanced = now._activeData == _activeData && now._activeSections == _activeSections &&
                            now._activeLightQueues == _activeLightQueues;
            string message =
                $"{label} (data {_activeData.ToString()}→{now._activeData.ToString()}, sections {_activeSections.ToString()}→{now._activeSections.ToString()}, light queues {_activeLightQueues.ToString()}→{now._activeLightQueues.ToString()})";
            if (balanced) Debug.Log($"  [PASS] {message}");
            else Debug.LogError($"  [FAIL] {message}");
            return balanced;
        }
    }
}
