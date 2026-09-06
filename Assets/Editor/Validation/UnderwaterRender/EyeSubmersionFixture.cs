using System;
using Editor.Validation.PhysicsSolver.Framework;
using Helpers;
using UnityEngine;
using Id = Editor.Validation.PhysicsSolver.Framework.TestPhysicsBlockPalette.Id;

namespace Editor.Validation.UnderwaterRender
{
    /// <summary>
    /// A world holding a still fluid pool, for driving <c>World.GatherEyeSubmersion</c> from edit mode.
    /// </summary>
    /// <remarks>
    /// Built on <see cref="PhysicsTestWorld"/> rather than on a second stub world. That fixture already wires
    /// the two things the eye query fails soft without — the job-side block palette and the fluid height
    /// templates — and it is the fixture the physics fluid baselines proved that wiring on. A private copy
    /// here would be one more thing to keep in step with <c>World</c>'s construction.
    /// </remarks>
    public sealed class EyeSubmersionFixture : IDisposable
    {
        /// <summary>Cell Y of the ground plane under the pool.</summary>
        /// <remarks>
        /// Low enough to leave a <b>four</b>-cell column above it, so a scenario can sink an eye across
        /// several cell boundaries rather than just one. Every scenario positions itself relative to
        /// <see cref="FluidTopY"/>, so deepening the pool costs them nothing.
        /// </remarks>
        private const int GROUND_Y = 2;

        /// <summary>Cell Y of the pool's topmost fluid layer.</summary>
        public const int FluidTopY = 6;

        /// <summary>Chunk-local X the pool is centered on.</summary>
        private const int POOL_X = 8;

        /// <summary>Chunk-local Z the pool is centered on.</summary>
        private const int POOL_Z = 8;

        /// <summary>Half-width of the pool in cells, so the sampled column is well inside it.</summary>
        private const int POOL_RADIUS = 2;

        /// <summary>A source fluid level: full strength, no falling flag.</summary>
        private const byte SOURCE_LEVEL = 0;

        private PhysicsTestWorld _world;

        /// <summary>The Unity-space XZ the scenarios sample, at the middle of a pool cell.</summary>
        public Vector2 EyeXz => new Vector2(POOL_X + 0.5f, POOL_Z + 0.5f);

        /// <summary>Seeds the ground, a two-layer pool, and pins the world as <c>World.Instance</c>.</summary>
        public EyeSubmersionFixture()
        {
            _world = new PhysicsTestWorld(TestPhysicsBlockPalette.Create());
            _world.FillLayer(GROUND_Y, Id.Ground);

            // Two full layers: the eye can then sit inside the body with fluid above it, which is the case
            // the surface override covers, as well as just under the drawn top.
            for (int y = GROUND_Y + 1; y <= FluidTopY; y++)
            for (int dx = -POOL_RADIUS; dx <= POOL_RADIUS; dx++)
            for (int dz = -POOL_RADIUS; dz <= POOL_RADIUS; dz++)
                _world.SetBlock(POOL_X + dx, y, POOL_Z + dz, Id.Fluid, SOURCE_LEVEL);
        }

        /// <summary>Runs the eye query at a Unity-space point.</summary>
        /// <param name="x">Unity-space X.</param>
        /// <param name="y">Unity-space Y.</param>
        /// <param name="z">Unity-space Z.</param>
        /// <returns>What the query reports there.</returns>
        public EyeSubmersion Sample(float x, float y, float z)
        {
            World.Instance.GatherEyeSubmersion(new Vector3(x, y, z), out EyeSubmersion submersion);
            return submersion;
        }

        /// <summary>
        /// Detaches the job-side palette, standing in for a world unloaded out from under a frame that is
        /// still publishing shader globals.
        /// </summary>
        /// <remarks>
        /// Detached rather than disposed: the fixture still owns that manager and disposes it in
        /// <see cref="Dispose"/>, so freeing it here would double-free. This exercises the query's null
        /// clause; the sibling <c>IsDisposed</c> clause guards the same call site and cannot be reached
        /// without tearing down the fixture's own teardown path.
        /// </remarks>
        public void DisposeJobData()
        {
            World.Instance.JobDataManager = null;
            World.Instance.FluidVertexTemplates = null;
        }

        /// <summary>Tears the world down and restores the previous <c>World.Instance</c>.</summary>
        public void Dispose()
        {
            _world?.Dispose();
            _world = null;
        }
    }
}
