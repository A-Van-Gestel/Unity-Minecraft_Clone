// Play-mode world rig for the Unity CLI: list saves, launch a world, drive it, and capture frames.
//
// RUN     unity command run_script --file Tools/UnityCli/PlayMode/WorldRig.cs \
//             --entry UnityCli.WorldRig.<Entry> --args '<json array>' --result-only
//         Every argument is required (run_script applies no C# defaults). Compiled in memory on each
//         call, never imported by Unity.
// NEEDS   Saves works in edit mode. Launch needs Play mode on the MainMenu scene; everything else
//         needs a loaded World scene. Entering Play mode is the user's call.
// TRAP    Never change world state and Capture in the same call: World.Update() pushes the global
//         shader uniforms, so a render in the same call still shows the previous frame's lighting.

using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace UnityCli
{
    /// <summary>Entry points for driving a live world from the shell through <c>run_script</c>.</summary>
    public static class WorldRig
    {
        private const string WORLD_SCENE = "Scenes/World";
        private const string MAIN_MENU_SCENE = "MainMenu";

        private static readonly int s_globalLightLevel = Shader.PropertyToID("GlobalLightLevel");

        /// <summary>Lists loadable saves (current save version only) in both save roots.</summary>
        /// <param name="maxPerRoot">Cap on rows per root; the editor accumulates hundreds of test saves.</param>
        /// <returns>One line per save: root, name, seed.</returns>
        public static string Saves(int maxPerRoot)
        {
            var sb = new StringBuilder();
            sb.AppendLine("current save version = " + SaveSystem.CURRENT_VERSION
                          + " (older saves need the WorldSelectMenu UI, which runs AOT migration)");
            foreach (bool isVolatile in new[] { true, false })
            {
                int shown = 0;
                foreach (var w in SaveSystem.GetAvailableWorlds(isVolatile))
                {
                    if (w.version != SaveSystem.CURRENT_VERSION) continue;
                    if (shown++ >= maxPerRoot) break;
                    sb.AppendLine((isVolatile ? "volatile" : "persistent") + "  '" + w.worldName + "'  seed=" + w.seed);
                }
            }

            return sb.ToString();
        }

        /// <summary>Launches a world the way <c>WorldSelectMenu</c> does, minus its AOT-migration step.</summary>
        /// <param name="worldName">Save name to load, or the name of the new world.</param>
        /// <param name="seed">Seed; for an existing save pass the seed <see cref="Saves"/> listed.</param>
        /// <param name="isNewGame">True generates a fresh world, which is always at the current version.</param>
        /// <returns>What was launched, or why not.</returns>
        public static string Launch(string worldName, int seed, bool isNewGame)
        {
            if (!Application.isPlaying) return "not in Play mode";
            if (SceneManager.GetActiveScene().name != MAIN_MENU_SCENE)
                return "active scene is '" + SceneManager.GetActiveScene().name + "', expected " + MAIN_MENU_SCENE;

            Data.WorldLaunchState.WorldName = worldName;
            Data.WorldLaunchState.Seed = seed;
            Data.WorldLaunchState.IsNewGame = isNewGame;
            SceneManager.LoadScene(WORLD_SCENE, LoadSceneMode.Single);
            return "loading '" + worldName + "' seed=" + seed + " new=" + isNewGame + "; poll Status until chunks > 0";
        }

        /// <summary>Reports the loaded world, the camera's voxel cell, and the pushed global light level.</summary>
        /// <returns>A one-line status, or the active scene when no world is loaded.</returns>
        public static string Status()
        {
            var world = Object.FindAnyObjectByType<World>();
            if (world == null) return "scene=" + SceneManager.GetActiveScene().name + " (no World)";

            Camera cam = Camera.main;
            string cell = cam == null ? "no camera" : Helpers.WorldOrigin.UnityToVoxelCell(cam.transform.position).ToString();
            return "scene=" + SceneManager.GetActiveScene().name
                   + " world='" + world.worldData.worldName + "' chunks=" + world.worldData.Chunks.Count
                   + " cameraCell=" + cell
                   + " GlobalLightLevel=" + Shader.GetGlobalFloat(s_globalLightLevel).ToString("0.###", CultureInfo.InvariantCulture);
        }

        /// <summary>Runs a console command on the live console's engine, which has the commands registered.</summary>
        /// <param name="line">The command line, e.g. <c>/time set midnight</c>.</param>
        /// <returns>The command's output lines.</returns>
        public static string Command(string line)
        {
            var console = Object.FindAnyObjectByType<UI.ConsoleUI>(FindObjectsInactive.Include);
            if (console == null) return "no ConsoleUI in the scene";

            var sb = new StringBuilder();
            foreach (var output in console.Engine.Execute(line).Lines) sb.AppendLine(output.Text);
            return sb.Length == 0 ? "(no output)" : sb.ToString();
        }

        /// <summary>Places the camera's eye at a voxel position, converting through the floating origin.</summary>
        /// <param name="eyeX">Voxel-space X of the eye.</param>
        /// <param name="eyeY">Voxel-space Y of the eye.</param>
        /// <param name="eyeZ">Voxel-space Z of the eye.</param>
        /// <param name="yaw">Degrees; 0 faces +Z.</param>
        /// <param name="pitch">Degrees; positive looks down.</param>
        /// <returns>The camera's round-tripped voxel cell, to check against the request.</returns>
        public static string Pose(float eyeX, float eyeY, float eyeZ, float yaw, float pitch)
        {
            var player = Object.FindAnyObjectByType<Player>();
            Camera cam = Camera.main;
            if (player == null || cam == null) return "no Player or main camera";

            // The origin shifts across reloads, so a literal Unity position from an earlier session is wrong.
            Vector3 eyeUnity = Helpers.WorldOrigin.VoxelToUnity(new Vector3(eyeX, eyeY, eyeZ));
            float eyeOffset = cam.transform.position.y - player.transform.position.y;
            player.transform.position = new Vector3(eyeUnity.x, eyeUnity.y - eyeOffset, eyeUnity.z);
            player.transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            cam.transform.localEulerAngles = new Vector3(pitch, 0f, 0f);
            return "cameraCell=" + Helpers.WorldOrigin.UnityToVoxelCell(cam.transform.position);
        }

        /// <summary>Renders the main camera to a PNG.</summary>
        /// <param name="path">Absolute output path (the scratchpad, or <c>Assets/AgentCaptures~/</c>).</param>
        /// <param name="width">Pixel width.</param>
        /// <param name="height">Pixel height.</param>
        /// <returns>The written path and the light level the frame was rendered with.</returns>
        public static string Capture(string path, int width, int height)
        {
            Camera cam = Camera.main;
            if (cam == null) return "no main camera";

            var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
            RenderTexture prevTarget = cam.targetTexture;
            RenderTexture prevActive = RenderTexture.active;
            try
            {
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, tex.EncodeToPNG());
            }
            finally
            {
                cam.targetTexture = prevTarget;
                RenderTexture.active = prevActive;
                Object.DestroyImmediate(tex);
                rt.Release();
                Object.DestroyImmediate(rt);
            }

            return "wrote " + path + " at GlobalLightLevel="
                   + Shader.GetGlobalFloat(s_globalLightLevel).ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
