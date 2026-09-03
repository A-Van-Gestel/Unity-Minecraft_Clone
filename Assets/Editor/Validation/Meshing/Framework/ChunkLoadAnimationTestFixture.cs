using System;
using Data;
using Editor.Validation.Framework;
using Helpers;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// An edit-mode fixture for the chunk <b>load-animation</b> path — a real <see cref="Chunk"/> whose
    /// <c>enableChunkLoadAnimations</c> setting can be toggled between construction and
    /// <see cref="Chunk.TriggerLoadAnimation"/>, which is exactly the sequence the 2026-04-09 regression
    /// broke (see Documentation/Bugs/_FIXED_BUGS.md).
    /// <para>
    /// <b>Why a real <see cref="Chunk"/> is affordable here.</b> Its constructor needs only
    /// <c>World.Instance.transform</c> and <c>World.Instance.settings</c>: the 16
    /// <see cref="SectionRenderer"/>s it builds resolve materials lazily inside <c>UpdateMeshNative</c>,
    /// which this fixture never calls. So unlike <see cref="SectionRendererTestFixture"/> it needs no
    /// <see cref="BlockDatabase"/> stub and no materials.
    /// </para>
    /// <para>
    /// <b>Ordering differs from the MH-6 fixture</b>, deliberately: that one builds its renderer *before*
    /// claiming <see cref="World.Instance"/>, because the renderer constructor never reads it. A
    /// <see cref="Chunk"/> constructor does (parent transform + settings), so the singleton must be claimed
    /// first — the constructor's <c>catch</c> tears everything down so a failure can never strand a stub
    /// world in the global singleton.
    /// </para>
    /// </summary>
    public sealed class ChunkLoadAnimationTestFixture : IDisposable
    {
        /// <summary>The real chunk under test.</summary>
        public readonly Chunk Chunk;

        private readonly GameObject _worldGo;
        private readonly World _world;
        private readonly World _previousInstance;
        private bool _disposed;

        /// <summary>
        /// Stubs <see cref="World.Instance"/> with a settings object and constructs a real chunk under it.
        /// </summary>
        /// <param name="animationsEnabled">The value of <c>enableChunkLoadAnimations</c> <i>at construction</i>
        /// — the discriminator for this fixture, since the constructor is where the component is pre-added.</param>
        /// <param name="coord">The chunk coordinate to construct at.</param>
        public ChunkLoadAnimationTestFixture(bool animationsEnabled, ChunkCoord coord = default)
        {
            // Captured up front so the failure path can always restore it.
            _previousInstance = World.Instance;

            try
            {
                _worldGo = new GameObject("ChunkAnim_StubWorld");

                // World is a plain MonoBehaviour (no [ExecuteAlways]/OnValidate), so AddComponent runs no
                // lifecycle in edit mode — Awake never fires and no world initialization happens.
                _world = _worldGo.AddComponent<World>();
                _world.settings = new Settings { enableChunkLoadAnimations = animationsEnabled };

                // Claim the singleton BEFORE constructing the chunk — see the ordering note on the class.
                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _world);

                Chunk = new Chunk(coord);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>The live <c>enableChunkLoadAnimations</c> setting — settable, so a scenario can toggle it
        /// mid-lifecycle exactly as the pause menu does.</summary>
        public bool AnimationsEnabled
        {
            get => _world.settings.enableChunkLoadAnimations;
            set => _world.settings.enableChunkLoadAnimations = value;
        }

        /// <summary>The chunk's animation component, or <c>null</c> when none has been added. Read through the
        /// public GameObject rather than the chunk's private cached field.</summary>
        public ChunkLoadAnimation Animation => Chunk.ChunkGameObject.GetComponent<ChunkLoadAnimation>();

        /// <summary>Whether the chunk's GameObject currently carries an animation component.</summary>
        public bool HasAnimationComponent => Animation != null;

        /// <summary>Whether an animation component exists AND is enabled (i.e. actually driving the transform).</summary>
        public bool AnimationEnabled
        {
            get
            {
                ChunkLoadAnimation animation = Animation;
                return animation != null && animation.enabled;
            }
        }

        /// <summary>The chunk GameObject's current world position.</summary>
        public Vector3 Position => Chunk.ChunkGameObject.transform.position;

        /// <summary>
        /// The position a correctly-seeded animation parks a chunk at before it rises — the chunk's resting
        /// position dropped by one chunk height. Derived the same way <c>ChunkLoadAnimation.ResetToUnderground</c>
        /// derives it, so a scenario can distinguish "parked underground" from "left at the world origin".
        /// </summary>
        /// <param name="restingPosition">The chunk's intended resting position (its <c>UnityPosition</c>).</param>
        /// <returns>The expected underground parking position.</returns>
        public static Vector3 UndergroundOf(Vector3 restingPosition) =>
            new Vector3(restingPosition.x, restingPosition.y - ChunkMath.CHUNK_HEIGHT, restingPosition.z);

        /// <summary>Restores the previous <see cref="World.Instance"/> and destroys everything the fixture created.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);

            // Chunk.Destroy() cannot be used here: it (and SectionRenderer.Destroy) call Object.Destroy, which
            // is an error in edit mode. Tear down by hand instead — and destroy each section's Mesh first,
            // since a Mesh is a standalone asset that destroying its GameObject does NOT reclaim (16 per chunk).
            if (Chunk?.ChunkGameObject != null)
            {
                Transform root = Chunk.ChunkGameObject.transform;
                for (int i = 0; i < root.childCount; i++)
                {
                    MeshFilter filter = root.GetChild(i).GetComponent<MeshFilter>();
                    if (filter != null && filter.sharedMesh != null) Object.DestroyImmediate(filter.sharedMesh);
                }

                Object.DestroyImmediate(Chunk.ChunkGameObject);
            }

            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
        }
    }
}
