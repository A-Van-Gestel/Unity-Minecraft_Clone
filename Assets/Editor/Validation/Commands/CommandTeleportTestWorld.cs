using System;
using Commands;
using Data;
using Editor.Validation.Framework;
using Helpers;
using Physics;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Commands
{
    /// <summary>
    /// Edit-mode stub world for the CMD-2 <c>/teleport</c> matrix (§4.3): a real <see cref="World"/>
    /// component (no <c>Awake</c> in edit mode — the <c>PlacementTestWorld</c> recipe) with a dummy
    /// player, driven through a <see cref="CommandEngine"/> whose context carries the world facade.
    /// <para>
    /// <b>Shared-static safety:</b> teleports re-anchor <see cref="WorldOrigin"/>, which is global
    /// static state — the construction snapshot is restored in <see cref="Dispose"/> so a teleport
    /// baseline can never leak a shifted origin into subsequent scenarios or suites.
    /// <see cref="World.Instance"/> is deliberately never touched (nothing on the teleport entry
    /// path reads it in edit mode).
    /// </para>
    /// </summary>
    internal sealed class CommandTeleportTestWorld : IDisposable
    {
        private readonly World _world;
        private readonly GameObject _worldGo;
        private readonly GameObject _playerGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly ChunkCoord _savedOriginChunk;
        private bool _disposed;

        /// <summary>The stub world commands act on.</summary>
        public World World => _world;

        /// <summary>The dummy player's transform (what <see cref="World.TeleportPlayer"/> places).</summary>
        public Transform PlayerTransform => _playerGo.transform;

        /// <summary>The dummy player's rigidbody (carries the arrival-hold flag).</summary>
        public VoxelRigidbody Rigidbody { get; }

        /// <summary>An engine wired to this world's facade, with <c>/teleport</c> registered.</summary>
        public CommandEngine Engine { get; }

        /// <summary>Stands up the stub world, dummy player, and a teleport-ready engine.</summary>
        public CommandTeleportTestWorld()
        {
            _savedOriginChunk = WorldOrigin.OriginChunk;

            _worldGo = new GameObject("Command_StubWorld");
            _world = _worldGo.AddComponent<World>();
            _world.settings = new Settings { enableLighting = false };
            _world.worldData = new WorldData("CommandTeleportTestWorld", 0);

            // Minimal named palette so /give and /setblock name→ID resolution is headless-testable
            // (index 0 must be Air — the engine-wide convention BlockIDs pins).
            _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
            _stubDatabase.blockTypes = new[]
            {
                new BlockType { blockName = "Air", isSolid = false },
                new BlockType { blockName = "Stone", isSolid = true },
            };
            ValidationReflection.SetInstanceField(_world, "_blockDatabase", _stubDatabase);

            _playerGo = new GameObject("Command_StubPlayer");
            Rigidbody = _playerGo.AddComponent<VoxelRigidbody>();
            Player player = _playerGo.GetComponent<Player>() != null
                ? _playerGo.GetComponent<Player>()
                : _playerGo.AddComponent<Player>();
            ValidationReflection.SetInstanceProperty(player, nameof(Player.VoxelRigidbody), Rigidbody);
            _world.player = player;
            ValidationReflection.SetInstanceField(_world, "_playerTransform", _playerGo.transform);

            // The full production pack via the shared installer (§8.1.1), so any pack command can be
            // driven against this fixture and a production/suite registration split cannot exist.
            Engine = new CommandEngine(new CommandContext(world: _world, player: player));
            ConsoleCommandInstaller.RegisterAll(Engine.Registry);
        }

        /// <summary>Enables the TF-14 border fence on the stub world (0 disables — the default).</summary>
        /// <param name="radius">Border half-extent in voxels.</param>
        public void SetBorderRadius(int radius) => _world.SetBorderRadius(radius);

        /// <summary>Restores the <see cref="WorldOrigin"/> snapshot and destroys every created object.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            WorldOrigin.SetOrigin(_savedOriginChunk);
            if (_playerGo != null) Object.DestroyImmediate(_playerGo);
            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
        }
    }
}
