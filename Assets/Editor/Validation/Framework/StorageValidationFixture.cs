using System;
using System.IO;
using Serialization;
using UnityEngine;

namespace Editor.Validation.Framework
{
    /// <summary>
    /// Shared isolated save-system fixture for storage-boundary suites (Save Durability,
    /// Deserialization Robustness): stubs <c>World.Instance</c> (settings + concurrent chunk pool —
    /// everything <see cref="ChunkStorageManager"/> and <see cref="ChunkSerializer"/> reach for) and
    /// stands up a real <see cref="ChunkStorageManager"/> on a unique volatile-path world. Disposal
    /// disarms EVERY dev-only injection seam (the single seam-disarm list — new seams get their disarm
    /// here, once), restores the previous <c>World.Instance</c>, and deletes the temp save. Suites
    /// subclass with their own world-name prefix.
    /// </summary>
    public class StorageValidationFixture : IDisposable
    {
        /// <summary>The fixture's real storage manager on the volatile-path world.</summary>
        public readonly ChunkStorageManager Storage;

        private readonly GameObject _worldGo;
        private readonly World _previousInstance;

        /// <summary>The fixture's unique volatile world name (for scenarios that stand up a second
        /// <see cref="ChunkStorageManager"/> on the same save, e.g. manager-swap / on-disk corruption).</summary>
        public string WorldName { get; }

        /// <summary>Creates the stub world + storage manager (subclass with your suite's prefix).</summary>
        /// <param name="worldNamePrefix">Suite-specific prefix for the unique volatile world name.</param>
        protected StorageValidationFixture(string worldNamePrefix)
        {
            _previousInstance = World.Instance;
            WorldName = $"{worldNamePrefix}_{Guid.NewGuid():N}";
            try
            {
                _worldGo = new GameObject($"{worldNamePrefix}_StubWorld");
                // AddComponent on a MonoBehaviour runs no Awake in edit mode — the component is only
                // the typed Instance target; we wire the two members the load/save path reads.
                World world = _worldGo.AddComponent<World>();
                world.settings = new Settings();
                world.worldData = new Data.WorldData(WorldName, 0);
                ValidationReflection.SetInstanceProperty(world, nameof(World.ChunkPool),
                    new ChunkPoolManager(_worldGo.transform));
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), world);

                Storage = new ChunkStorageManager(WorldName, useVolatilePath: true, SaveSystem.CURRENT_VERSION);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Disarms all injection seams, tears down the stub world, and deletes the temp save.</summary>
        public void Dispose()
        {
            ChunkStorageManager.InjectSaveFaults(0);
            ChunkStorageManager.InjectZeroLengthSerializes(0);
            ChunkStorageManager.InjectLoadFaults(0);
            ChunkStorageManager.InjectTooLargeSaves(0);
            Storage?.Dispose();
            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);
            if (_worldGo != null) UnityEngine.Object.DestroyImmediate(_worldGo);

            string savePath = SaveSystem.GetSavePath(WorldName, useVolatilePath: true);
            if (Directory.Exists(savePath)) Directory.Delete(savePath, recursive: true);
        }
    }
}
