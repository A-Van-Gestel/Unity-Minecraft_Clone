using System;
using System.Collections.Generic;
using Audio;
using Data.WorldTypes;
using Jobs.Helpers;

namespace Commands
{
    /// <summary>
    /// <c>/sound</c> — prints what the world-ambience layer currently believes: the sampled listener
    /// context, the biome weights driving the bed mix, every duck applied to the beds, and each bed
    /// source's live gain.
    /// </summary>
    /// <remarks>
    /// Exists because the bed mix is the product of five independent multipliers — mix weight, rest cycle,
    /// cave duck, depth gate and content trim — and when the result is "too loud" or "should be silent",
    /// hearing it tells you nothing about which one is wrong. Every number below is read from the live
    /// director, and the per-bed gain is the value actually written to the source rather than a
    /// recomputation, so the readout cannot agree with a mix the engine is not producing.
    /// </remarks>
    public sealed class SoundCommand : IConsoleCommand
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "sound";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/sound";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count != 0)
                return CommandResult.Error($"'/sound' takes no arguments. Usage: {Usage}");

            SoundManager manager = SoundManager.Instance;
            if (manager == null)
                return CommandResult.Error("No SoundManager in the scene.");

            List<ConsoleLine> lines = new List<ConsoleLine>();

            if (!manager.HasContext)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    "No listener context sampled yet — no world, or no camera tagged MainCamera."));
                return new CommandResult(lines.ToArray());
            }

            AudioContext context = manager.Context;

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Listener: skylight {context.SkylightAtHead}/15 | depth {context.DepthBelowSurface} below surface | " +
                (context.Submerged ? "submerged" : "dry")));

            AppendBiomes(lines, context, manager.Biomes);
            AppendDirector(lines);
            AppendVolumes(lines, manager);

            return new CommandResult(lines.ToArray());
        }

        /// <summary>Adds the biome-weight breakdown driving the bed mix.</summary>
        /// <param name="lines">Receives the output.</param>
        /// <param name="context">The sampled listener context.</param>
        /// <param name="biomes">The world type's biome assets, indexed by biome index.</param>
        private static void AppendBiomes(List<ConsoleLine> lines, AudioContext context, BiomeBase[] biomes)
        {
            if (!context.HasWeights || context.Weights.Count <= 0)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    "Biome weights: none — this generator does not answer weighted queries, so the beds " +
                    "fall back to the default loop."));
                return;
            }

            BiomeWeights weights = context.Weights;
            string breakdown = string.Empty;

            for (int i = 0; i < weights.Count && i < BiomeWeights.MaxBiomes; i++)
            {
                int index = weights.Indices[i];
                string name = biomes != null && (uint)index < (uint)biomes.Length && biomes[index] != null
                    ? biomes[index].biomeName
                    : $"#{index}";

                if (i > 0) breakdown += ", ";
                breakdown += $"{name} {weights.Weights[i] * 100f:0.#}%";
            }

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info, $"Biome weights: {breakdown}"));
        }

        /// <summary>Adds the director's ducks, rest-cycle state and per-bed gains.</summary>
        /// <param name="lines">Receives the output.</param>
        private static void AppendDirector(List<ConsoleLine> lines)
        {
            AmbienceDirector director = AmbienceDirector.Instance;
            if (director == null)
            {
                lines.Add(new ConsoleLine(ConsoleLineSeverity.Warning,
                    "No AmbienceDirector in the scene — no beds are being driven."));
                return;
            }

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Cave: fade {director.DiagCaveFade:0.00} | committed {director.DiagUndergroundCommitted} | " +
                $"volume {director.DiagCaveVolume:0.000}"));

            // The two ducks are reported separately, and so is the one that wins: "beds are quiet" has two
            // possible causes, and they are tuned by different knobs.
            float caveDuck = director.DiagCaveDuck;
            float depthDuck = director.DiagDepthDuck;
            string binding = depthDuck < caveDuck ? "depth" : "cave";

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Ducks: cave {caveDuck:0.00} | depth {depthDuck:0.00} " +
                $"(full at {director.DiagFullDuckDepth}, taper {director.DiagDuckTaperBlocks}) | " +
                $"binding: {binding}"));

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Rest cycle: {(director.DiagRestAudible ? "audible" : "resting")}, " +
                $"{director.DiagRestRemaining:0.0}s left"));

            for (int slot = 0; slot < director.DiagBedCount; slot++)
            {
                director.DiagBed(slot, out string clipName, out float fade, out float volume);
                if (clipName == "-" && volume <= 0f) continue;

                lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  Bed {slot}: {clipName} | fade {fade:0.00} | volume {volume:0.000}"));
            }
        }

        /// <summary>Adds the content trim and the category gains the beds and music ride on.</summary>
        /// <param name="lines">Receives the output.</param>
        /// <param name="manager">The audio owner holding the database.</param>
        private static void AppendVolumes(List<ConsoleLine> lines, SoundManager manager)
        {
            string trim = manager.Ambience != null
                ? manager.Ambience.BedVolume.ToString("0.00")
                : "no database";

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Gains: bed trim {trim} | ambient {AudioVolumes.GetLinear(AudioCategory.Ambient):0.00} | " +
                $"music {AudioVolumes.GetLinear(AudioCategory.Music):0.00} | " +
                $"master {AudioVolumes.GetLinear(AudioCategory.Master):0.00}"));

            lines.Add(new ConsoleLine(ConsoleLineSeverity.Info,
                $"Voices: {manager.PlayingVoiceCount}/{manager.VoiceCount} playing | " +
                $"low-pass {manager.LowPassCutoffHertz:0} Hz"));
        }
    }
}
