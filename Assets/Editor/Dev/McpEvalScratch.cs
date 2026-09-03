namespace Editor.Dev
{
    /// <summary>
    /// SCRATCH body for <see cref="McpEval"/>. Overwrite <see cref="Run"/> with the code to execute,
    /// then invoke <c>Minecraft Clone/Dev/MCP Eval</c> (via <c>Unity_ManageMenuItem</c>) after an asset
    /// refresh. This is an ordinary editor script, so it has full namespace access (System.Reflection,
    /// System.Net, Newtonsoft if referenced, ...) — unlike <c>Unity_RunCommand</c>, whose analyzer
    /// blocks those. Use <see cref="McpEval.Log"/> for tagged output. Reset to this default body when
    /// done so the committed file stays idle and always compiles.
    /// </summary>
    internal static class McpEvalScratch
    {
        /// <summary>Replace this body with the code to run. Keep it parameterless and static.</summary>
        public static void Run()
        {
            McpEval.Log("scratch is empty — replace McpEvalScratch.Run() body, refresh, then run the menu item");
        }
    }
}
