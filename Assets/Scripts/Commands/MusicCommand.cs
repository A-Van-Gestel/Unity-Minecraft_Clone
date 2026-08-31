using System;
using System.Collections.Generic;
using Audio;
using Data;
using Data.WorldTypes;
using UnityEngine;

namespace Commands
{
    /// <summary>
    /// <c>/music</c> — reports what the music layer will pick from and why, and forces a pick so a change can
    /// be heard without waiting out a gap.
    /// </summary>
    /// <remarks>
    /// Gaps run to eight minutes and the opening one to a minute, so confirming a weight, a share or a trim
    /// by ear otherwise costs a silence per attempt — long enough that the obvious workaround is editing the
    /// gap sliders, which then get left changed in the scene. The readout lists both pools with their
    /// resolved weights, because "the desert track never plays" has three separate causes (its weight, the
    /// biome share, and whether the listener's biome is the one that authored it) and hearing the result
    /// distinguishes none of them.
    /// </remarks>
    public sealed class MusicCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "music";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/music [next|stop|play <name>]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            MusicScheduler scheduler = MusicScheduler.Instance;
            if (scheduler == null)
                return CommandResult.Error("No MusicScheduler in the scene.");

            SoundManager manager = SoundManager.Instance;
            if (manager == null)
                return CommandResult.Error("No SoundManager in the scene.");

            if (args.Count == 0) return Report(scheduler, manager);

            string verb = args[0].Text.ToLowerInvariant();
            switch (verb)
            {
                case "next":
                    AudioClip picked = scheduler.ForcePick();
                    return picked == null
                        ? CommandResult.Error("Neither pool offered a track — nothing is authored.")
                        : CommandResult.Info($"Now playing '{picked.name}'.");

                case "stop":
                    scheduler.StopTrack();
                    return CommandResult.Info("Stopped; the gap has been re-armed.");

                case "play":
                    if (args.Count < 2)
                        return CommandResult.Error($"'/music play' needs a track name. Usage: {Usage}");

                    return PlayNamed(scheduler, manager, args[1].Text);

                default:
                    return CommandResult.Error($"Unknown sub-command '{verb}'. Usage: {Usage}");
            }
        }

        /// <summary>Prints both pools, the share between them, and what is playing now.</summary>
        /// <param name="scheduler">The live scheduler.</param>
        /// <param name="manager">The audio owner holding the database and context.</param>
        /// <returns>The readout.</returns>
        private static CommandResult Report(MusicScheduler scheduler, SoundManager manager)
        {
            List<ConsoleLine> lines = new List<ConsoleLine>();

            AmbienceDatabase database = manager.Ambience;
            if (database == null)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    "No AmbienceDatabase assigned — the music layer has no content to draw from."));
                return new CommandResult(lines.ToArray());
            }

            BiomeBase biome = manager.HasContext ? manager.Context.Biome : null;
            MusicTrack[] biomeTracks = biome != null ? biome.musicTracks : null;

            AudioClip current = scheduler.DiagCurrentTrack;
            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                current != null
                    ? $"Playing: {current.name} | track trim {scheduler.DiagTrackVolume:0.00} | " +
                      $"source volume {scheduler.DiagSourceVolume:0.000}"
                    : $"Silent | next pick in {scheduler.DiagGapRemaining:0.0}s"));

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Share: {database.BiomeMusicShare:0.00} toward the biome pool | " +
                $"music trim {database.MusicVolume:0.00} | " +
                $"category {AudioVolumes.GetLinear(AudioCategory.Music):0.00}"));

            // The seed, because the counters are randomized per session: without it "it opened with the
            // wrong track" is not reproducible from the world seed alone.
            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Session seed: {scheduler.DiagSessionSeed}"));

            AppendPool(lines, "Global", database.GlobalMusicTracks);
            AppendPool(lines, biome != null ? $"Biome ({biome.biomeName})" : "Biome (none resolved)",
                biomeTracks);

            if (!MusicResolution.HasPlayable(database.GlobalMusicTracks) &&
                !MusicResolution.HasPlayable(biomeTracks))
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    "Neither pool holds a playable track — the layer will stay silent."));

            return new CommandResult(lines.ToArray());
        }

        /// <summary>Lists one pool's tracks with the share each weight resolves to.</summary>
        /// <param name="lines">Receives the output.</param>
        /// <param name="label">The pool's name.</param>
        /// <param name="tracks">Its tracks. Null or empty prints a placeholder.</param>
        /// <remarks>
        /// The resolved share is printed, not the raw weight: a weight only means something against the sum
        /// of its pool, and "0.5" beside three other 0.5s is a quarter of the picks, not half.
        /// </remarks>
        private static void AppendPool(List<ConsoleLine> lines, string label, MusicTrack[] tracks)
        {
            if (tracks == null || tracks.Length == 0)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, $"{label}: no tracks authored."));
                return;
            }

            float total = 0f;
            int playable = 0;
            foreach (MusicTrack track in tracks)
            {
                if (!track.IsPlayable) continue;

                playable++;
                total += track.EffectiveWeight;
            }

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"{label}: {playable} playable of {tracks.Length}"));

            foreach (MusicTrack track in tracks)
            {
                if (!track.IsPlayable)
                {
                    lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning, "  (empty slot)"));
                    continue;
                }

                // An all-zero pool is an even pick, so report it that way rather than as a row of zeroes.
                float share = total > 0f ? track.EffectiveWeight / total : 1f / Mathf.Max(1, playable);

                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  {track.clip.name} | weight {track.EffectiveWeight:0.00} -> {share:P0} of this pool " +
                    $"| trim {track.EffectiveVolume:0.00}"));
            }
        }

        /// <summary>Starts a track by name from either pool.</summary>
        /// <param name="scheduler">The live scheduler.</param>
        /// <param name="manager">The audio owner holding the database and context.</param>
        /// <param name="name">The clip name to match, case-insensitively.</param>
        /// <returns>What was started, or why nothing was.</returns>
        private static CommandResult PlayNamed(MusicScheduler scheduler, SoundManager manager, string name)
        {
            AmbienceDatabase database = manager.Ambience;
            BiomeBase biome = manager.HasContext ? manager.Context.Biome : null;

            if (TryFind(database != null ? database.GlobalMusicTracks : null, name, out MusicTrack found) ||
                TryFind(biome != null ? biome.musicTracks : null, name, out found))
            {
                scheduler.ForceTrack(found.clip, found.EffectiveVolume);
                return CommandResult.Info($"Now playing '{found.clip.name}' at trim {found.EffectiveVolume:0.00}.");
            }

            return CommandResult.Error($"No authored track matches '{name}'. Run '/music' to list both pools.");
        }

        /// <summary>Finds a track in a pool by clip name.</summary>
        /// <param name="tracks">The pool to search. Null matches nothing.</param>
        /// <param name="name">The name to match, case-insensitively.</param>
        /// <param name="found">Receives the match.</param>
        /// <returns>True when a playable track matched.</returns>
        private static bool TryFind(MusicTrack[] tracks, string name, out MusicTrack found)
        {
            found = default;
            if (tracks == null) return false;

            foreach (MusicTrack track in tracks)
            {
                if (!track.IsPlayable) continue;
                if (!string.Equals(track.clip.name, name, StringComparison.OrdinalIgnoreCase)) continue;

                found = track;
                return true;
            }

            return false;
        }
    }
}
