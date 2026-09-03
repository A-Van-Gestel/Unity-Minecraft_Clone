using System;
using Data;

namespace Commands
{
    /// <summary>
    /// <c>/give &lt;block&gt; [count]</c> — puts a stack of the named block in the selected hotbar
    /// slot (CMD-3 §8.1). Names resolve via the block database (case-insensitive; quote multi-word
    /// names); the count clamps to the block's stack size.
    /// </summary>
    public sealed class GiveCommand : IConsoleCommand, IArgumentCompleter
    {
        private static readonly string[] s_noAliases = Array.Empty<string>();

        /// <inheritdoc/>
        public string Name => "give";

        /// <inheritdoc/>
        public string[] Aliases => s_noAliases;

        /// <inheritdoc/>
        public string Usage => "/give <block> [count]";

        /// <inheritdoc/>
        public CommandResult Execute(CommandContext ctx, CommandArgs args)
        {
            if (args.Count < 1 || args.Count > 2 || args[0].Type != CommandTokenType.Word)
                return CommandResult.Error($"Usage: {Usage}");

            int count = 1;
            if (args.Count == 2)
            {
                if (args[1].Type != CommandTokenType.Number || !args[1].IsInteger || args[1].Integer <= 0)
                    return CommandResult.Error($"Count must be a positive integer. Usage: {Usage}");
                count = args[1].Integer;
            }

            if (ctx.World == null || ctx.Player == null)
                return CommandResult.Error("No world is loaded.");

            if (!CommandArgUtility.TryResolveBlockId(ctx.World, args[0].Text, out ushort id, out string nameError))
                return CommandResult.Error(nameError);

            if (id == BlockIDs.Air)
                return CommandResult.Error("Cannot give Air.");

            // The inventory stores IDs as a byte (ItemStack.ID) — a wider database ID cannot be held.
            if (id > byte.MaxValue)
                return CommandResult.Error($"'{ctx.World.BlockTypes[id].blockName}' cannot be held (ID {id} exceeds the inventory's byte range).");

            var toolbar = ctx.Player.GetComponent<PlayerInteraction>()?.toolbar;
            if (toolbar == null)
                return CommandResult.Error("No toolbar available.");

            int stackSize = ctx.World.BlockTypes[id].stackSize;
            int given = Math.Min(count, stackSize);

            toolbar.slots[toolbar.slotIndex].ItemSlot.InsertStack(new ItemStack((byte)id, given));

            string clampNote = given < count ? $" (clamped to the stack size of {stackSize})" : "";
            return CommandResult.Info($"Gave {given} × {ctx.World.BlockTypes[id].blockName}{clampNote}.");
        }

        /// <inheritdoc/>
        public string[] CompleteArgument(int argIndex, string partial, CommandContext ctx) =>
            argIndex == 0 ? CommandArgUtility.MatchBlockNames(ctx.World, partial) : Array.Empty<string>();
    }
}
