using System;
using System.Globalization;
using Data;
using Helpers;
using StartRefusal = Helpers.PregenerationPlan.StartRefusal;

namespace Commands
{
    /// <summary>
    /// <c>/chunk</c> — chunk-level tools.
    /// <para>
    /// <c>/chunk info</c> dumps the pipeline state of the player's current chunk (CMD-3 §8.1): the
    /// lifecycle/lighting flags plus the visual's mesh state. Pairs with chunk-lifecycle debugging; queryable
    /// anywhere, including over not-yet-loaded chunks.
    /// </para>
    /// <para>
    /// <c>/chunk generate</c> makes every chunk of a rectangle exist on disk through the CMD-6 station walk
    /// (<see cref="World.TryStartPregeneration"/>): a radius around the player or two voxel corners. Size is never
    /// refused — the walk's memory is bounded by one station — but a run over
    /// <see cref="PregenerationPlan.ConfirmChunkCount"/> asks for confirmation with an estimate of its disk use. The
    /// run outlives <c>Execute</c>; its progress and outcome lines come from <see cref="ProgressLine"/> and
    /// <see cref="EndedLine"/>, which <c>WorldUIManager</c> posts.
    /// See Documentation/Design/CHUNK_PREGENERATION.md §3.3.
    /// </para>
    /// </summary>
    public sealed class ChunkCommand : IConsoleCommand
    {
        /// <summary>
        /// The refusal teleporting commands give while a run is active: the walk owns the player and the arrival hold
        /// for its duration, the mirror of a run refusing to start during a teleport hold.
        /// </summary>
        public const string TeleportRefusedWhilePregenerating =
            "A pre-generation run is moving the player — /chunk generate cancel stops it first.";

        private static readonly string[] s_noAliases = Array.Empty<string>();

        // "generate <radius>" and "generate <x1> <z1> <x2> <z2>", counting the subcommand word.
        private const int RADIUS_FORM_ARGS = 2;
        private const int CORNER_FORM_ARGS = 5;

        private const int PROGRESS_STEPS = 10;
        private const int PERCENT = 100;

        private const string CANCEL_REASON = "canceled by command";

        // On-disk size of a generated chunk, region-file overhead included: 4.7 MB for 729–1 024 chunks in a seed-4242
        // region at vd 10 (2026-10-17). An estimate band for the warning, not a quota.
        private const long DISK_BYTES_PER_CHUNK_LOW = 4_600;
        private const long DISK_BYTES_PER_CHUNK_HIGH = 6_400;

        private const double BYTES_PER_MB = 1e6;
        private const double BYTES_PER_GB = 1e9;
        private const int SECONDS_PER_MINUTE = 60;
        private const int SECONDS_PER_HOUR = 3600;

        // The chunk index range a voxel coordinate inside the addressable world maps to.
        private const long ADDRESSABLE_LIMIT_CHUNKS = CommandArgUtility.AddressableLimitVoxels / ChunkMath.CHUNK_WIDTH;

        /// <inheritdoc/>
        public string Name => "chunk";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage =>
            "/chunk info | /chunk generate <radius> | /chunk generate <x1> <z1> <x2> <z2> | /chunk generate cancel";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count == 1 && IsWord(args[0], "info"))
                return ExecuteInfo(ctx);

            if (args.Count >= RADIUS_FORM_ARGS && IsWord(args[0], "generate"))
                return ExecuteGenerate(ctx, args);

            return CommandResult.Error($"Usage: {Usage}");
        }

        /// <summary>
        /// The text for a refused pre-generation start, one distinct line per <see cref="StartRefusal"/>.
        /// </summary>
        /// <param name="refusal">Why the run did not start.</param>
        /// <returns>The refusal text.</returns>
        public static string DescribeRefusal(StartRefusal refusal)
        {
            switch (refusal)
            {
                case StartRefusal.OutsideBorder:
                    return "Part of the area lies outside the world border.";
                case StartRefusal.NoWorld:
                    return "No world is loaded.";
                case StartRefusal.PersistenceDisabled:
                    return "Chunk persistence is disabled, so generated chunks could not be saved.";
                case StartRefusal.AlreadyRunning:
                    return "A pre-generation run is already active — /chunk generate cancel stops it.";
                case StartRefusal.TeleportHoldActive:
                    return "A teleport is still arriving — try again once it lands.";
                default:
                    return $"Pre-generation refused: {refusal}.";
            }
        }

        /// <summary>
        /// The console line for a progress report when it reaches a new 10 % step of the station grid, or null. The
        /// remaining-time estimate extrapolates the run's own pace, so it adapts to the machine and view distance.
        /// 100 % is left to <see cref="EndedLine"/>. The benchmark's runs post nothing: no command started them.
        /// </summary>
        /// <param name="report">The progress report.</param>
        /// <param name="lastStep">The last step posted; 0 before the first. Advanced when a line is returned.</param>
        /// <returns>The line text, or null when no new step was reached or the run is not interactive.</returns>
        public static string ProgressLine(in PregenerationProgressReport report, ref int lastStep)
        {
            if (!report.Interactive || report.StationCount <= 0 || report.StationsChecked <= 0) return null;

            int step = (int)(report.StationsChecked * PROGRESS_STEPS / report.StationCount);
            if (step <= lastStep || step >= PROGRESS_STEPS) return null;

            lastStep = step;
            double remainingSeconds = report.ElapsedSeconds * (report.StationCount - report.StationsChecked) /
                                      report.StationsChecked;
            return $"Pre-generation {step * PERCENT / PROGRESS_STEPS} % — {FormatCount(report.StationsChecked)} of " +
                   $"{FormatCount(report.StationCount)} stations checked, {FormatCount(report.Generated)} chunks " +
                   $"generated, about {FormatDuration(remainingSeconds)} left.";
        }

        /// <summary>The console line for a finished pre-generation run.</summary>
        /// <param name="outcome">The run's outcome.</param>
        /// <returns>Info when completed, Error when chunks stalled, Warning when canceled.</returns>
        public static ConsoleLine EndedLine(PregenerationOutcome outcome)
        {
            string elapsed = FormatDuration(outcome.ElapsedSeconds);
            switch (outcome.Status)
            {
                case PregenerationStatus.CompletedWithStalls:
                    return new ConsoleLine(ConsoleLineSeverity.Error,
                        $"Pre-generation finished with {FormatCount(outcome.Stalled)} chunks that never generated " +
                        $"(first: {string.Join(", ", outcome.FirstStalled)}); {FormatCount(outcome.Generated)} " +
                        $"generated in {elapsed}.");
                case PregenerationStatus.Canceled:
                    return new ConsoleLine(ConsoleLineSeverity.Warning,
                        $"Pre-generation canceled ({outcome.CancelReason ?? "no reason given"}): " +
                        $"{FormatCount(outcome.Generated)} chunks generated in {elapsed}, " +
                        $"{FormatCount(outcome.StationsChecked)} of {FormatCount(outcome.StationGridCount)} stations " +
                        "checked. Running the same command again resumes where it stopped.");
                default:
                    return outcome.MissingFound == 0
                        ? new ConsoleLine(ConsoleLineSeverity.Info,
                            $"All {FormatCount(outcome.Rect.Count)} chunks already exist — nothing to generate.")
                        : new ConsoleLine(ConsoleLineSeverity.Info,
                            $"Pre-generation complete: {FormatCount(outcome.Generated)} chunks generated in " +
                            $"{elapsed} over {outcome.Stations} station{(outcome.Stations == 1 ? "" : "s")}.");
            }
        }

        /// <summary>
        /// The disk-use estimate for a run, flagged when it may not fit on the save drive. A warning only: the
        /// estimate is a band, and a run that fills the drive fails its saves loudly rather than silently.
        /// </summary>
        /// <param name="chunkCount">Chunks in the rectangle.</param>
        /// <param name="hasFreeBytes">Whether the save drive's free space is known.</param>
        /// <param name="freeBytes">The save drive's free space.</param>
        /// <returns>The estimate text, e.g. "about 18.4–25.6 GB on disk".</returns>
        public static string DescribeDiskEstimate(long chunkCount, bool hasFreeBytes, long freeBytes)
        {
            double low = (double)chunkCount * DISK_BYTES_PER_CHUNK_LOW;
            double high = (double)chunkCount * DISK_BYTES_PER_CHUNK_HIGH;
            string text = $"about {FormatByteBand(low, high)} on disk";
            if (hasFreeBytes && high > freeBytes)
                text += $" — more than the {FormatBytes(freeBytes)} free on the save drive";
            return text;
        }

        /// <summary>The <c>/chunk info</c> dump.</summary>
        /// <param name="ctx">The execution context.</param>
        /// <returns>The state lines, or the not-loaded / no-world line.</returns>
        private static CommandResult ExecuteInfo(CommandContext ctx)
        {
            if (ctx.World == null || ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            ChunkCoord chunkCoord = WorldOrigin.UnityToChunk(ctx.Player.transform.position);
            if (!ctx.World.worldData.TryGetChunk(chunkCoord.ToVoxelOrigin(), out var data) || data == null)
                return CommandResult.Info($"Chunk ({chunkCoord.X}, {chunkCoord.Z}): not loaded.");

            string visual = data.Chunk == null
                ? "none"
                : data.Chunk.HasMeshApplied
                    ? "mesh applied"
                    : "no mesh yet";

            return new CommandResult(new[]
            {
                new ConsoleLine(ConsoleLineSeverity.Info, $"Chunk ({chunkCoord.X}, {chunkCoord.Z}):"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  IsPopulated={data.IsPopulated}  IsLoading={data.IsLoading}  Visual={visual}"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  NeedsInitialLighting={data.NeedsInitialLighting}  HasLightChangesToProcess={data.HasLightChangesToProcess}"),
                new ConsoleLine(ConsoleLineSeverity.Info,
                    $"  NeedsEdgeCheck={data.NeedsEdgeCheck}"),
            });
        }

        /// <summary><c>/chunk generate …</c>: parse, then the confirmation tier, then the start.</summary>
        /// <param name="ctx">The execution context.</param>
        /// <param name="args">The arguments, the subcommand word first.</param>
        /// <returns>The result.</returns>
        private CommandResult ExecuteGenerate(CommandContext ctx, CommandArgs args)
        {
            bool cancel = args.Count == RADIUS_FORM_ARGS && IsWord(args[1], "cancel");
            if (!cancel && args.Count != RADIUS_FORM_ARGS && args.Count != CORNER_FORM_ARGS)
                return ErrorWithUsage("Expected a radius, two corners (X1 Z1 X2 Z2), or 'cancel'.");

            if (ctx.World == null)
                return CommandResult.Error("No world is loaded.");

            World world = ctx.World;
            if (cancel)
            {
                return world.CancelPregeneration(CANCEL_REASON)
                    ? CommandResult.Info("Canceling pre-generation…")
                    : CommandResult.Error("No pre-generation run is active.");
            }

            ChunkRect rect;
            string error;
            bool parsed = args.Count == RADIUS_FORM_ARGS
                ? TryParseRadiusForm(world, args[1], out rect, out error)
                : TryParseCornerForm(world, args, out rect, out error);
            if (!parsed)
                return ErrorWithUsage(error);

            if (rect.Count > PregenerationPlan.ConfirmChunkCount)
            {
                long stations = PregenerationPlan.StationGridCount(rect,
                    PregenerationPlan.CoverRadius(world.settings.LoadDistance));
                bool hasFree = world.TryGetSaveDriveFreeBytes(out long freeBytes);
                return CommandResult.Confirm(
                    $"Pre-generate {FormatCount(rect.Count)} chunks, {FormatRect(rect)} ({FormatCount(stations)} " +
                    $"stations)? That is {DescribeDiskEstimate(rect.Count, hasFree, freeBytes)}, and it can take " +
                    "hours. The player is walked through the area and returned; /chunk generate cancel stops it at " +
                    "any time, and running the same command again resumes where it stopped.",
                    () => StartRun(world, rect));
            }

            return StartRun(world, rect);
        }

        /// <summary>Starts the run, or reports why it was refused.</summary>
        /// <param name="world">The world (non-null).</param>
        /// <param name="rect">The rectangle.</param>
        /// <returns>The started line, or the refusal.</returns>
        private static CommandResult StartRun(World world, ChunkRect rect)
        {
            if (!world.TryStartPregeneration(rect, out StartRefusal refusal))
                return CommandResult.Error(DescribeRefusal(refusal));

            return CommandResult.Info($"Pre-generating {FormatCount(rect.Count)} chunks, {FormatRect(rect)} — " +
                                      "/chunk generate cancel stops it.");
        }

        /// <summary>The square of chunks within a radius of the player's chunk.</summary>
        /// <param name="world">The world (non-null).</param>
        /// <param name="token">The radius token.</param>
        /// <param name="rect">The square on success.</param>
        /// <param name="error">The error text on failure.</param>
        /// <returns>True when parsed.</returns>
        private static bool TryParseRadiusForm(World world, CommandToken token, out ChunkRect rect, out string error)
        {
            rect = default;
            if (token.Type != CommandTokenType.Number || !token.IsInteger)
            {
                error = "Radius must be a whole number of chunks.";
                return false;
            }

            int radius = token.Integer;
            if (radius < 0)
            {
                error = "Radius must be 0 or more.";
                return false;
            }

            if (!world.TryGetPlayerVoxelCell(out int voxelX, out _, out int voxelZ))
            {
                error = "No player is loaded.";
                return false;
            }

            // The square's corners must stay addressable — the one real bound, and what keeps the corner arithmetic
            // from overflowing for a radius near int.MaxValue.
            ChunkCoord center = new ChunkCoord(ChunkMath.VoxelToChunk(voxelX), ChunkMath.VoxelToChunk(voxelZ));
            if (Math.Abs((long)center.X) + radius > ADDRESSABLE_LIMIT_CHUNKS ||
                Math.Abs((long)center.Z) + radius > ADDRESSABLE_LIMIT_CHUNKS)
            {
                error = $"Radius {radius} reaches past the addressable world (±2^31 voxels).";
                return false;
            }

            rect = PregenerationPlan.Square(center, radius);
            error = null;
            return true;
        }

        /// <summary>The chunk rectangle covering two voxel corners, given in any order; <c>~</c> is player-relative.</summary>
        /// <param name="world">The world (non-null).</param>
        /// <param name="args">The arguments, the subcommand word first.</param>
        /// <param name="rect">The rectangle on success.</param>
        /// <param name="error">The error text on failure.</param>
        /// <returns>True when parsed.</returns>
        private static bool TryParseCornerForm(World world, CommandArgs args, out ChunkRect rect, out string error)
        {
            rect = default;
            bool relativeAllowed = world.TryGetPlayerVoxelCell(out int baseX, out _, out int baseZ);

            if (!CommandArgUtility.TryParseCoord(args[1], "X1", relativeAllowed, baseX, out int x1, out error) ||
                !CommandArgUtility.TryParseCoord(args[2], "Z1", relativeAllowed, baseZ, out int z1, out error) ||
                !CommandArgUtility.TryParseCoord(args[3], "X2", relativeAllowed, baseX, out int x2, out error) ||
                !CommandArgUtility.TryParseCoord(args[4], "Z2", relativeAllowed, baseZ, out int z2, out error))
                return false;

            rect = PregenerationPlan.FromVoxelCorners(x1, z1, x2, z2);
            return true;
        }

        /// <summary>Whether a token is the given word, case-insensitively.</summary>
        private static bool IsWord(CommandToken token, string word) =>
            token.Type == CommandTokenType.Word && string.Equals(token.Text, word, StringComparison.OrdinalIgnoreCase);

        /// <summary>A chunk count with invariant thousands separators (a Dutch host locale must never print `10.201`).</summary>
        private static string FormatCount(long count) => count.ToString("N0", CultureInfo.InvariantCulture);

        /// <summary>A byte count as MB or GB (decimal units), invariant-formatted.</summary>
        private static string FormatBytes(double bytes) =>
            bytes >= BYTES_PER_GB ? FormatInGb(bytes) + " GB" : FormatInMb(bytes) + " MB";

        /// <summary>A low–high byte band in the unit its upper end needs, e.g. "18.4–25.6 GB".</summary>
        private static string FormatByteBand(double low, double high) =>
            high >= BYTES_PER_GB
                ? $"{FormatInGb(low)}–{FormatInGb(high)} GB"
                : $"{FormatInMb(low)}–{FormatInMb(high)} MB";

        private static string FormatInGb(double bytes) =>
            (bytes / BYTES_PER_GB).ToString("0.#", CultureInfo.InvariantCulture);

        private static string FormatInMb(double bytes) =>
            Math.Max(1.0, Math.Round(bytes / BYTES_PER_MB)).ToString("0", CultureInfo.InvariantCulture);

        /// <summary>A duration as seconds under a minute, minutes and seconds under an hour, else hours and minutes.</summary>
        private static string FormatDuration(double seconds)
        {
            if (seconds < SECONDS_PER_MINUTE) return seconds.ToString("F1", CultureInfo.InvariantCulture) + " s";

            long whole = (long)seconds;
            if (seconds < SECONDS_PER_HOUR) return $"{whole / SECONDS_PER_MINUTE} min {whole % SECONDS_PER_MINUTE} s";
            return $"{whole / SECONDS_PER_HOUR} h {whole % SECONDS_PER_HOUR / SECONDS_PER_MINUTE} min";
        }

        /// <summary>A rectangle as its two corner chunks, for prompts and replies.</summary>
        private static string FormatRect(ChunkRect rect) =>
            $"chunks ({rect.MinX}, {rect.MinZ}) to ({rect.MaxX}, {rect.MaxZ})";

        /// <summary>Builds an error line followed by the usage string.</summary>
        /// <param name="text">The error text.</param>
        /// <returns>The two-line result.</returns>
        private CommandResult ErrorWithUsage(string text)
        {
            return new CommandResult(new[]
            {
                new ConsoleLine(ConsoleLineSeverity.Error, text),
                new ConsoleLine(ConsoleLineSeverity.Info, Usage),
            });
        }
    }
}
