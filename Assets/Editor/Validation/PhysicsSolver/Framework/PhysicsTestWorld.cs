using System;
using System.Reflection;
using Data;
using Data.JobData;
using Data.NativeData;
using Editor.Validation.Framework;
using Helpers;
using Jobs.BurstData;
using Physics;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.PhysicsSolver.Framework
{
    /// <summary>
    /// Single-chunk, edit-mode harness that drives the <b>real</b> collision solver — a live
    /// <see cref="VoxelRigidbody"/> resolving against the real <c>World.CheckPhysicsCollision</c> over a synthetic
    /// <see cref="ChunkData"/>. Nothing here reimplements the solver: the scenarios feed displacements in and read
    /// the solver's own resolved displacement, position and <see cref="VoxelRigidbody.IsGrounded"/> back out.
    /// <para>
    /// <b>World seam</b> reuses the <see cref="ValidationReflection"/> recipe proven by <c>PlacementTestWorld</c>
    /// (which VQ-3 already drove <c>CheckPhysicsCollision</c> through unmodified, for 1950 probes): a plain
    /// <see cref="World"/> component (no <c>Awake</c> in edit mode), a stub <see cref="BlockDatabase"/> over the
    /// supplied palette, a quiet <see cref="Settings"/>, a <see cref="WorldData"/>, a real
    /// <see cref="ChunkPoolManager"/>, and one all-air <see cref="IsPopulated"/> chunk at the origin chunk.
    /// </para>
    /// <para>
    /// <b>⚠️ The <see cref="WorldOrigin"/> trap.</b> <c>CheckPhysicsCollision</c> offsets its voxel lookup by the
    /// <see cref="WorldOrigin"/> <i>static</i>, which survives play sessions (it is re-anchored only on play-mode
    /// entry). A fixture that inherits a stale origin looks up cells far from the seeded blocks, every sweep
    /// returns zero hits, and every scenario passes <b>vacuously</b>. This constructor therefore pins the origin
    /// explicitly and <see cref="Dispose"/> restores the previous anchor; the suite's first baseline asserts both
    /// the pinned origin and a non-zero hit before any other scenario is trusted.
    /// </para>
    /// </summary>
    public sealed class PhysicsTestWorld : IDisposable
    {
        /// <summary>The center chunk the solver reads and the scenario seeds.</summary>
        public readonly ChunkData ChunkData;

        #region Pinned entity dimensions

        // Pinned rather than inherited from the player prefab: the scenarios compute exact expected rest heights and
        // contact faces from these, so an inspector retune of the live player must not silently move every baseline.

        /// <summary>Total collider height of the harness entity.</summary>
        public const float EntityHeight = 1.8f;

        /// <summary>Full collider width (X) of the harness entity.</summary>
        public const float EntityWidthX = 0.8f;

        /// <summary>Full collider depth (Z) of the harness entity.</summary>
        public const float EntityDepthZ = 0.8f;

        /// <summary>Internal collider inset that keeps sweeps off flush faces.</summary>
        public const float EntityPadding = 0.001f;

        /// <summary>Step height the solver may lift the entity by in its step-up pre-pass.</summary>
        public const float EntityStepHeight = 0.5f;

        /// <summary>Downward acceleration per second applied when not flying.</summary>
        public const float EntityGravity = -13f;

        /// <summary>Horizontal walk speed, in m/s.</summary>
        public const float EntityWalkSpeed = 3f;

        /// <summary>Half the collider width (X) — the entity AABB's X extent before <see cref="EntityPadding"/>.</summary>
        public const float EntityHalfWidthX = EntityWidthX * 0.5f;

        /// <summary>Half the collider depth (Z) — the entity AABB's Z extent before <see cref="EntityPadding"/>.</summary>
        public const float EntityHalfDepthZ = EntityDepthZ * 0.5f;

        #endregion

        #region Pinned fluid template shape

        // Pinned for the same reason the entity dimensions are: the fluid scenarios compute exact expected
        // waterlines from these, so a retune of the shipping FluidData assets must not silently move them.

        /// <summary>Horizontal flow levels of the fixture's water-like fluid (levels 0-7).</summary>
        public const int WaterFlowLevels = 8;

        /// <summary>Height lost per horizontal flow level for the fixture's water-like fluid.</summary>
        public const float WaterDecayStep = 1f / 8f;

        /// <summary>Horizontal flow levels of the fixture's lava-like fluid.</summary>
        public const int LavaFlowLevels = 4;

        /// <summary>Height lost per horizontal flow level for the fixture's lava-like fluid.</summary>
        public const float LavaDecayStep = 1f / 4f;

        #endregion

        private readonly BlockType[] _palette;
        private readonly GameObject _worldGo;
        private readonly GameObject _entityGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly World _world;
        private readonly World _previousInstance;
        private readonly ChunkCoord _previousOriginChunk;
        private readonly VoxelRigidbody _body;
        private readonly MethodInfo _resolveMovement;
        private readonly MethodInfo _calculateVelocity;
        private readonly MethodInfo _resolveFluidContact;
        private readonly MethodInfo _collectStepSmoothing;
        private readonly MethodInfo _applyPendingJump;
        private readonly JobDataManager _jobDataManager;
        private readonly FluidVertexTemplatesNativeData _fluidTemplates;
        private bool _disposed;

        /// <summary>The palette backing <c>World.Instance.BlockTypes</c> — exposed so scenarios can read block data by id.</summary>
        public BlockType[] Palette => _palette;

        /// <summary>The live solver under test, for scenarios that need to read or tune it directly.</summary>
        public VoxelRigidbody Body => _body;

        /// <summary>The entity's current feet-center position, in Unity space.</summary>
        public Vector3 Position => _entityGo.transform.position;

        /// <summary>The solver's grounded verdict after the most recent resolve.</summary>
        public bool IsGrounded => _body.IsGrounded;

        /// <summary>
        /// The displacement the solver resolved on the most recent <see cref="Tick"/>. <b>Only</b> <c>Tick</c> writes
        /// it — <see cref="Resolve"/> and <see cref="Step"/> leave it stale (zero, if no tick has run), so read their
        /// return value instead of this property.
        /// </summary>
        public Vector3 Velocity => _body.Velocity;

        /// <summary>The solver's accumulated vertical momentum (m/s) — the fall speed a scenario pins.</summary>
        public float VerticalMomentum => (float)ValidationReflection.GetInstanceField(_body, "_verticalMomentum");

        /// <summary>
        /// Whether the solver latched the last <c>VoxelRigidbody.RequestJump</c> call. That method latches only
        /// when the body is grounded and is not still holding the button that carried it out of a fluid
        /// (<see cref="VoxelRigidbody.requireJumpReleaseAfterFluid"/>), so this is how a scenario observes a jump
        /// being <i>refused</i> rather than merely being ineffective — the distinction <c>PLAYER_BUGS</c> §04
        /// turns on.
        /// </summary>
        public bool JumpRequested => (bool)ValidationReflection.GetInstanceField(_body, "_jumpRequest");

        /// <summary>The fixed timestep the solver integrates with, read from the project rather than assumed.</summary>
        public static float FixedDeltaTime => Time.fixedDeltaTime;

        /// <summary>
        /// Stands up the stub world, an all-air center chunk, and the entity, backed by the supplied palette.
        /// </summary>
        /// <param name="palette">The block palette assigned to the stub <see cref="BlockDatabase.blockTypes"/>;
        /// indices are the ids the scenario seeds and the solver resolves against.</param>
        /// <param name="originChunk">The WS-4 floating-origin anchor to pin for this fixture. Defaults to the
        /// identity (0, 0), where Unity space and voxel space coincide. A non-zero value moves the world's voxel
        /// coordinates far out while the harness keeps addressing the same small Unity-space cells — which is what
        /// proves the solver's scan actually converts rather than reading raw Unity coordinates as voxels.</param>
        public PhysicsTestWorld(BlockType[] palette, ChunkCoord originChunk = default)
        {
            _previousInstance = World.Instance;
            _previousOriginChunk = WorldOrigin.OriginChunk;
            try
            {
                _palette = palette;

                // Pin the origin BEFORE the chunk is created: the seeding below addresses Unity-space cells, which
                // only land on this chunk while the anchor matches.
                WorldOrigin.SetOrigin(originChunk);

                _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
                _stubDatabase.blockTypes = _palette;

                _worldGo = new GameObject("PhysicsSolver_StubWorld");
                _world = _worldGo.AddComponent<World>();
                ValidationReflection.SetInstanceField(_world, "_blockDatabase", _stubDatabase);
                _world.settings = new Settings { enableLighting = false, enableWaterDiagnosticLogs = false };
                _world.worldData = new WorldData("PhysicsTestWorld", 0);

                ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _world);
                ValidationReflection.SetInstanceProperty(_world, nameof(World.ChunkPool),
                    new ChunkPoolManager(_worldGo.transform));

                Vector2Int chunkVoxelPos = originChunk.ToVoxelOrigin();
                ChunkData = new ChunkData(chunkVoxelPos);
                // The harness models a loaded, generated chunk — WorldData.TryGetVoxel (the solver's VQ-1 lookup)
                // resolves populated chunks only (Fluid Bug 18).
                ChunkData.IsPopulated = true;
                _world.worldData.SetChunk(chunkVoxelPos, ChunkData);

                // The fluid query needs the job-side palette and height templates, not just the managed
                // palette. Without them it returns "no fluid" for every body rather than failing, so every
                // fluid scenario would pass vacuously.
                _jobDataManager = BuildJobDataManager(_palette);
                _fluidTemplates = BuildFluidTemplates();
                _world.JobDataManager = _jobDataManager;
                _world.FluidVertexTemplates = _fluidTemplates;

                _entityGo = new GameObject("PhysicsSolver_Entity");
                _body = _entityGo.AddComponent<VoxelRigidbody>();
                PinEntityDimensions(_body);
                // Start() never runs in edit mode, so the world reference is injected instead.
                ValidationReflection.SetInstanceField(_body, "_world", _world);

                _resolveMovement = ResolveSolverMethod("ResolveMovement");
                _calculateVelocity = ResolveSolverMethod("CalculateVelocity");
                _resolveFluidContact = ResolveSolverMethod("ResolveFluidContact");
                _collectStepSmoothing = ResolveSolverMethod("CollectStepSmoothing");
                _applyPendingJump = ResolveSolverMethod("ApplyPendingJump");
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>
        /// Applies the pinned collider/movement dimensions the scenarios compute their expectations from.
        /// <para>
        /// <b>Includes <c>_lastMoveSpeed</c>, and that is not incidental.</b> <c>CalculateVelocity</c> reads
        /// <c>MoveSpeed = _lastMoveSpeed</c> for any body that is airborne and not flying, and the field starts at 0.
        /// A <see cref="Tick"/>-driven scenario with horizontal intent would therefore travel <b>exactly zero</b>
        /// distance and pass a "did not pass through the wall" assertion without testing anything. Pinning it to the
        /// walk speed models a player who has been on the ground at some point — which every real airborne player has.
        /// </para>
        /// </summary>
        /// <param name="body">The harness entity's solver.</param>
        private static void PinEntityDimensions(VoxelRigidbody body)
        {
            ValidationReflection.SetInstanceField(body, "_lastMoveSpeed", EntityWalkSpeed);

            body.collisionHeight = EntityHeight;
            body.collisionWidthX = EntityWidthX;
            body.collisionDepthZ = EntityDepthZ;
            body.collisionPadding = EntityPadding;
            body.stepHeight = EntityStepHeight;
            body.gravity = EntityGravity;
            body.walkSpeed = EntityWalkSpeed;
            body.isFlying = false;
            body.isNoclipping = false;
            body.isSprinting = false;
            body.showBoundingBox = false;
        }

        /// <summary>
        /// Builds the job-side palette the fluid contact query reads, mirroring the managed one.
        /// </summary>
        /// <param name="palette">The managed palette this fixture is built on.</param>
        /// <returns>A manager owning the native palette; disposed with the fixture.</returns>
        private static JobDataManager BuildJobDataManager(BlockType[] palette)
        {
            NativeArray<BlockTypeJobData> blockTypes =
                new NativeArray<BlockTypeJobData>(palette.Length, Allocator.Persistent);
            for (int id = 0; id < palette.Length; id++)
                blockTypes[id] = new BlockTypeJobData(palette[id]);

            // The solver's fluid path reads only the block palette; the custom-mesh arrays exist to satisfy
            // the manager's contract and are deliberately empty.
            return new JobDataManager(
                blockTypes,
                new NativeArray<CustomMeshData>(0, Allocator.Persistent),
                new NativeArray<CustomFaceData>(0, Allocator.Persistent),
                new NativeArray<CustomVertData>(0, Allocator.Persistent),
                new NativeArray<int>(0, Allocator.Persistent));
        }

        /// <summary>
        /// Builds the fluid vertex-height templates from <see cref="FluidMeshData.BuildVertexHeightTemplate"/>.
        /// </summary>
        /// <returns>Native templates owned by this fixture.</returns>
        /// <remarks>
        /// Generated from the shared curve builder rather than loaded from the shipping
        /// <c>FluidData_*.asset</c>, for the same reason the block palette is synthetic: a scenario's expected
        /// waterline should follow the engine's height <i>rule</i>, not whatever the shipping assets were last
        /// baked to. It is also the single source of truth the <c>FluidDataGenerator</c> bakes those assets
        /// from, so the fixture cannot drift from production while agreeing with itself.
        /// </remarks>
        private static FluidVertexTemplatesNativeData BuildFluidTemplates()
        {
            float[] water = new float[16];
            float[] lava = new float[16];
            FluidMeshData.BuildVertexHeightTemplate(water, WaterFlowLevels, WaterDecayStep);
            FluidMeshData.BuildVertexHeightTemplate(lava, LavaFlowLevels, LavaDecayStep);

            return new FluidVertexTemplatesNativeData(new FluidTemplates
            {
                WaterVertexTemplates = water,
                LavaVertexTemplates = lava,
            });
        }

        /// <summary>Locates a private solver method, failing loudly if it was renamed.</summary>
        /// <param name="name">The method name.</param>
        /// <returns>The resolved <see cref="MethodInfo"/>.</returns>
        private static MethodInfo ResolveSolverMethod(string name)
        {
            MethodInfo method = typeof(VoxelRigidbody).GetMethod(name,
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null)
                throw new InvalidOperationException(
                    $"Could not locate VoxelRigidbody.{name} via reflection — the solver was renamed and this suite " +
                    "no longer drives it.");
            return method;
        }

        #region Seeding

        /// <summary>Writes a block at a Unity-space cell (which is this chunk's local cell, for 0-15).</summary>
        /// <param name="x">Cell X (0-15).</param>
        /// <param name="y">Cell Y (0-127).</param>
        /// <param name="z">Cell Z (0-15).</param>
        /// <param name="id">Block id present in the palette.</param>
        /// <param name="meta">Raw metadata byte; defaults to 0.</param>
        public void SetBlock(int x, int y, int z, ushort id, byte meta = 0)
        {
            ChunkData.SetVoxel(x, y, z, BurstVoxelDataBitMapping.PackVoxelData(id, meta));
        }

        /// <summary>Fills one whole cell layer of the chunk — the ground plane most scenarios stand on.</summary>
        /// <param name="y">Cell Y of the layer.</param>
        /// <param name="id">Block id to fill with.</param>
        /// <param name="meta">Raw metadata byte; defaults to 0.</param>
        public void FillLayer(int y, ushort id, byte meta = 0)
        {
            for (int x = 0; x < ChunkMath.CHUNK_WIDTH; x++)
            for (int z = 0; z < ChunkMath.CHUNK_WIDTH; z++)
                SetBlock(x, y, z, id, meta);
        }

        #endregion

        #region Driving the solver

        /// <summary>Teleports the entity's feet-center to a Unity-space position without resolving collision.</summary>
        /// <param name="position">The feet-center position to place the entity at.</param>
        public void PlaceEntity(Vector3 position) => _entityGo.transform.position = position;

        /// <summary>Forces the solver's grounded state — the precondition the step-up pre-pass and jumping gate on.</summary>
        /// <param name="grounded">The grounded state to write.</param>
        public void SetGrounded(bool grounded) =>
            ValidationReflection.SetInstanceProperty(_body, nameof(VoxelRigidbody.IsGrounded), grounded);

        /// <summary>
        /// Pins the solver's vertical momentum (m/s, negative = falling). Gravity only accelerates a momentum that
        /// is still above <see cref="EntityGravity"/>, so a value at or past it survives the next
        /// <see cref="Tick"/> unchanged — which is how a scenario expresses an exact fall speed.
        /// </summary>
        /// <param name="momentum">Vertical momentum in m/s.</param>
        public void SetVerticalMomentum(float momentum) =>
            ValidationReflection.SetInstanceField(_body, "_verticalMomentum", momentum);

        /// <summary>Sets the normalized horizontal movement intent, as the input layer does.</summary>
        /// <param name="intent">Horizontal intent; magnitudes above 1 are normalized by the solver.</param>
        public void SetMovementIntent(Vector3 intent) => _body.SetMovementIntent(intent);

        /// <summary>
        /// Runs one collision resolve for an explicit displacement — the solver's <c>ResolveMovement</c>, which
        /// owns the step-up pre-pass, the per-axis horizontal resolve, the vertical/ground snap and
        /// <see cref="IsGrounded"/>. The entity is <b>not</b> moved; use <see cref="Step"/> for that.
        /// <para>
        /// Since <c>PH-2</c> the solver takes the position to resolve from as a second argument instead of reading
        /// the transform itself, so this passes the entity's current position — exactly what the solver used to
        /// read, which is what keeps every scenario's meaning unchanged across that refactor.
        /// </para>
        /// </summary>
        /// <param name="displacement">The intended displacement for this resolve, in Unity space.</param>
        /// <returns>The displacement the solver resolved to.</returns>
        public Vector3 Resolve(Vector3 displacement)
        {
            object[] args = { displacement, _entityGo.transform.position };
            _resolveMovement.Invoke(_body, args);
            return (Vector3)args[0];
        }

        /// <summary>
        /// <see cref="Resolve"/> plus the move — the entity ends at its resolved position, so a scenario can chain
        /// steps and assert a settled end state.
        /// </summary>
        /// <param name="displacement">The intended displacement for this resolve, in Unity space.</param>
        /// <returns>The displacement the solver resolved to.</returns>
        public Vector3 Step(Vector3 displacement)
        {
            Vector3 resolved = Resolve(displacement);
            _entityGo.transform.position += resolved;
            return resolved;
        }

        /// <summary>
        /// Runs one full physics tick the way <c>FixedUpdate</c> does: <c>CalculateVelocity</c> (which integrates
        /// gravity, derives this tick's displacement, and <b>substeps</b> it when it exceeds the tunneling
        /// threshold) followed by the translate. This is the only entry point that exercises the substep chain.
        /// </summary>
        /// <returns>The total displacement applied this tick.</returns>
        public Vector3 Tick()
        {
            Vector3 velocity = CalculateVelocityOnly();
            _applyPendingJump.Invoke(_body, null);
            _entityGo.transform.Translate(velocity, Space.World);
            CollectStepSmoothing();
            return velocity;
        }

        /// <summary>
        /// Runs the solver's post-move step-smoothing handoff, which <c>FixedUpdate</c> performs after its
        /// translate. <see cref="Tick"/> already calls it; scenarios driving <see cref="Step"/> or
        /// <see cref="Resolve"/> directly must call it themselves, since those bypass the tick.
        /// </summary>
        public void CollectStepSmoothing() => _collectStepSmoothing.Invoke(_body, null);

        /// <summary>
        /// Runs <c>CalculateVelocity</c> <b>without</b> the translate that follows it in <c>FixedUpdate</c> — so a
        /// scenario can observe what the solver did to the transform on its own, before the legitimate move lands.
        /// </summary>
        /// <returns>The displacement the solver resolved for this tick.</returns>
        public Vector3 CalculateVelocityOnly()
        {
            _calculateVelocity.Invoke(_body, null);
            return _body.Velocity;
        }

        /// <summary>
        /// Clears the entity transform's dirty flag, so a following <see cref="TransformChanged"/> read reports only
        /// writes made after this point.
        /// </summary>
        public void ClearTransformChanged() => _entityGo.transform.hasChanged = false;

        /// <summary>
        /// Whether anything wrote the entity's transform since <see cref="ClearTransformChanged"/>. Unity latches
        /// this on any write and a later revert does <b>not</b> clear it, which is what makes it a sound guard
        /// against a write-then-undo (<c>B26</c>) where comparing positions would not be.
        /// </summary>
        public bool TransformChanged => _entityGo.transform.hasChanged;

        /// <summary>
        /// Issues a raw collision query against the seeded world — the seam under the solver. Used by the
        /// fixture-integrity baseline to prove the world is actually reachable before any sweep result is trusted.
        /// </summary>
        /// <param name="bounds">The Unity-space AABB to test.</param>
        /// <param name="axis">Movement axis (0=X, 1=Y, 2=Z).</param>
        /// <param name="directionSign">+1 for positive movement, -1 for negative.</param>
        /// <param name="contact">The resolved contact, when the query hits.</param>
        /// <returns>True if the AABB overlaps solid collision geometry on that axis.</returns>
        public bool Probe(Bounds bounds, int axis, int directionSign, out CollisionContact contact) =>
            _world.CheckPhysicsCollision(bounds, axis, directionSign, out contact);

        /// <summary>
        /// The fluid contact the solver resolved on the most recent <see cref="Tick"/> /
        /// <see cref="CalculateVelocityOnly"/>. Only those drive the resolve, so
        /// <see cref="Resolve"/> and <see cref="Step"/> leave it stale.
        /// </summary>
        public FluidContact FluidContact => _body.FluidContact;

        /// <summary>
        /// Runs the solver's private fluid-contact resolve for the entity's current position, without
        /// integrating a tick — so a scenario can assert what the query measured, separately from what the
        /// forces then did with it.
        /// </summary>
        /// <returns>The resolved contact.</returns>
        public FluidContact ResolveFluidContact()
        {
            _resolveFluidContact.Invoke(_body, null);
            return _body.FluidContact;
        }

        /// <summary>
        /// Disposes the fixture's job data and fluid templates early, reproducing the state a world leaves
        /// behind when it is torn down while a body is still ticking.
        /// </summary>
        /// <remarks>
        /// Both are idempotent, so the fixture's own <see cref="Dispose"/> still runs cleanly afterwards.
        /// That was not true before <c>IsDisposed</c> existed: the second call threw.
        /// </remarks>
        public void DisposeJobDataEarly()
        {
            _fluidTemplates.Dispose();
            _jobDataManager.Dispose();
        }

        /// <summary>Applies vertical swim intent, as the input layer does while jump or crouch is held.</summary>
        /// <param name="verticalInput">-1 (swim down) to 1 (swim up).</param>
        public void SetSwimVerticalIntent(float verticalInput) => _body.SetSwimVerticalIntent(verticalInput);

        /// <summary>
        /// The authored surface height of a fluid level, straight from the shared curve builder the fixture's
        /// templates are generated with.
        /// </summary>
        /// <param name="fluidLevel">The raw 4-bit fluid level, falling flag included.</param>
        /// <param name="flowLevels">Horizontal flow levels of the fluid (<see cref="WaterFlowLevels"/>).</param>
        /// <param name="decayStep">Height lost per level (<see cref="WaterDecayStep"/>).</param>
        /// <returns>The surface height in block-local units, 0-1.</returns>
        public static float SurfaceHeightOf(byte fluidLevel, int flowLevels, float decayStep)
        {
            float[] template = new float[16];
            FluidMeshData.BuildVertexHeightTemplate(template, flowLevels, decayStep);
            return template[fluidLevel];
        }

        /// <summary>
        /// The entity AABB the solver would build for a feet-center position — so a scenario can express an
        /// expectation about the body's faces (min/max) rather than only about its position.
        /// </summary>
        /// <param name="feetCenter">The feet-center position, in Unity space.</param>
        /// <returns>The unpadded entity AABB at that position.</returns>
        public static Bounds EntityBoundsAt(Vector3 feetCenter)
        {
            Bounds bounds = new Bounds();
            bounds.SetMinMax(
                new Vector3(feetCenter.x - EntityHalfWidthX, feetCenter.y, feetCenter.z - EntityHalfDepthZ),
                new Vector3(feetCenter.x + EntityHalfWidthX, feetCenter.y + EntityHeight,
                    feetCenter.z + EntityHalfDepthZ));
            return bounds;
        }

        #endregion

        /// <summary>
        /// Restores the previous <c>World.Instance</c> and floating-origin anchor, and destroys every object the
        /// harness created. Restoring the origin matters even though every scenario pins its own: a leaked anchor
        /// would silently offset the <i>next</i> suite in a <c>Validate All</c> run.
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            ValidationReflection.SetStaticProperty(typeof(World), nameof(World.Instance), _previousInstance);
            WorldOrigin.SetOrigin(_previousOriginChunk);

            ChunkData?.Dispose();
            _fluidTemplates?.Dispose();
            _jobDataManager?.Dispose();
            if (_entityGo != null) Object.DestroyImmediate(_entityGo);
            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
        }
    }
}
