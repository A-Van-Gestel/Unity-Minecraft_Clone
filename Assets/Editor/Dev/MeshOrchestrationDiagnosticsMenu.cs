using UnityEditor;
using UnityEngine;

namespace Editor.Dev
{
    /// <summary>
    /// Dev menu item that dumps the MP-1 mesh-orchestration diagnostic counters from the live
    /// <see cref="World"/> to the console. Play-mode only — the counters are per-session runtime
    /// state that accumulates while a world is loaded, and are compiled out of non-development
    /// builds (their increment sites are <c>[Conditional]</c>-gated).
    /// See Documentation/Design/MESHING_PIPELINE_ORCHESTRATION_REFACTOR.md §MP-1.
    /// </summary>
    public static class MeshOrchestrationDiagnosticsMenu
    {
        /// <summary>Logs <see cref="World.BuildMeshOrchestrationDiagnostics"/> for the active world.</summary>
        [MenuItem("Minecraft Clone/Dev/Dump Mesh Orchestration Diagnostics", priority = DevMenuPriority.Diagnostics)]
        private static void Dump()
        {
            if (!Application.isPlaying || World.Instance == null)
            {
                Debug.LogWarning("[MP-1] Enter play mode and load a world first — the diagnostic " +
                                 "counters are per-session runtime state.");
                return;
            }

            Debug.Log(World.Instance.BuildMeshOrchestrationDiagnostics());
        }
    }
}
