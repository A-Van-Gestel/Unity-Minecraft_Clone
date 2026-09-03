using System;
using System.Collections.Generic;
using Serialization.Migration;
using UnityEngine;

namespace Serialization
{
    /// <summary>
    /// Version-tolerant <b>read</b> layer for <c>level.dat</c>: normalizes a document of any past save version to
    /// the current shape <i>in memory</i> before it is parsed into the live <see cref="WorldSaveData"/>.
    /// <para>
    /// Parsing an old document with the live type directly is only safe while every schema change is additive;
    /// the v13 re-type of <c>player.position</c> broke that (JsonUtility silently blanks the field — see
    /// <c>LegacyLevelDat</c>'s header). Rather than duplicating per-era shape knowledge here, this codec folds the
    /// registered migration steps' <see cref="WorldMigrationStep.MigrateLevelDat"/> transforms over the JSON — the
    /// frozen DTOs inside those steps ARE the codec tables, so a future format change extends this automatically.
    /// </para>
    /// <para>
    /// <b>Read-only by design:</b> nothing here may ever write the result back to disk. On-disk migration is
    /// exclusively <see cref="MigrationManager"/>'s job (backup, rollback, chunk data) when the world is played.
    /// </para>
    /// </summary>
    public static class LevelDatCodec
    {
        /// <summary>Minimal probe for the version field, present since v1.</summary>
        [Serializable]
        private class VersionProbe
        {
            public int version = 1;
        }

        /// <summary>
        /// Parses a <c>level.dat</c> JSON document of any supported version into the live
        /// <see cref="WorldSaveData"/>, upgrading old documents in memory first.
        /// <para>
        /// The returned object's <see cref="WorldSaveData.version"/> is deliberately kept at the <b>on-disk</b>
        /// value: callers (the world-select menu's migration UI, <c>RequiresMigration</c>) key off it to decide
        /// whether the world still needs a real disk migration; only the <i>contents</i> are normalized.
        /// </para>
        /// </summary>
        /// <param name="json">The raw <c>level.dat</c> JSON text.</param>
        /// <returns>The parsed save data, contents normalized to the current shape.</returns>
        public static WorldSaveData ReadNormalized(string json)
        {
            int diskVersion = JsonUtility.FromJson<VersionProbe>(json).version;

            // Current or future documents pass straight through — there is nothing to normalize (a future
            // version is surfaced to the user by the menu's version check, not here).
            if (diskVersion >= SaveSystem.CURRENT_VERSION)
                return JsonUtility.FromJson<WorldSaveData>(json);

            try
            {
                List<WorldMigrationStep> steps = new MigrationManager().GetRequiredMigrations(diskVersion);
                foreach (WorldMigrationStep step in steps)
                    json = step.MigrateLevelDat(json);

                WorldSaveData normalized = JsonUtility.FromJson<WorldSaveData>(json);
                normalized.version = diskVersion; // Contents are current-shaped; the version stays the disk's.
                return normalized;
            }
            catch (Exception e)
            {
                // Fail open to the raw parse (the pre-codec behavior): a broken step chain should degrade the
                // menu display, never block reading the world list.
                Debug.LogError($"[LevelDatCodec] In-memory level.dat normalization from v{diskVersion} failed — " +
                               $"falling back to a raw parse (old fields may read as defaults): {e.Message}");
                return JsonUtility.FromJson<WorldSaveData>(json);
            }
        }
    }
}
