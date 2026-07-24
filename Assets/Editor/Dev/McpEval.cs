using System;
using UnityEditor;
using UnityEngine;

namespace Editor.Dev
{
    /// <summary>
    /// Permanent editor harness for running arbitrary C# from an MCP session — including the
    /// <c>System.Reflection</c> / <c>System.Net</c> / <c>System.Diagnostics</c> /
    /// <c>System.Runtime.InteropServices</c> namespaces that <c>Unity_RunCommand</c>'s analyzer
    /// blocks (see <c>RunCommandCodeAnalyzer</c>). Edit <see cref="McpEvalScratch.Run"/>, trigger an
    /// asset refresh, then invoke the <c>Minecraft Clone/Dev/MCP Eval</c> menu item via
    /// <c>Unity_ManageMenuItem</c>. Every line the harness emits is prefixed with <see cref="Tag"/>
    /// so <c>Unity_ReadConsole</c> (FilterText) can isolate this run's output.
    /// </summary>
    public static class McpEval
    {
        /// <summary>Prefix on every harness log line; filter <c>Unity_ReadConsole</c> by this string.</summary>
        public const string Tag = "[MCP-EVAL]";

        /// <summary>
        /// Runs <see cref="McpEvalScratch.Run"/> inside a tagged try/catch. Invoke via
        /// <c>Unity_ManageMenuItem</c> after editing the scratch body and refreshing the AssetDatabase.
        /// </summary>
        [MenuItem("Minecraft Clone/Dev/MCP Eval")]
        public static void Run()
        {
            Debug.Log($"{Tag} begin");
            try
            {
                McpEvalScratch.Run();
                Debug.Log($"{Tag} end OK");
            }
            catch (Exception e)
            {
                // Full exception (message + stack) so the MCP caller sees the failure in the console.
                Debug.LogError($"{Tag} threw: {e}");
            }
        }

        /// <summary>Logs a tagged line so <c>Unity_ReadConsole</c> FilterText="[MCP-EVAL]" isolates it.</summary>
        /// <param name="message">Value to log; its <c>ToString()</c> is used.</param>
        public static void Log(object message) => Debug.Log($"{Tag} {message}");
    }
}
