using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Serialization;

namespace Editor.ProjectUtilities
{
    /// <summary>
    /// Provides utility methods for managing asset serialization within the project.
    /// </summary>
    public static class AssetReserializer
    {
        /// <summary>
        /// Forcibly loads and re-serializes all assets, flushing any outstanding data changes to disk.
        /// This is useful when changes in serialize field names or structs occur, and assets need to be updated.
        /// </summary>
        [MenuItem("Tools/Voxel Engine/Force Reserialize All Assets")]
        public static void ForceReserializeAllAssets()
        {
            if (EditorUtility.DisplayDialog("Force Reserialize Assets",
                    "This will forcibly load and re-serialize all assets in the project.\n\n" +
                    "This might take a while depending on the project size. Do you want to continue?",
                    "Yes, Reserialize", "Cancel"))
            {
                Debug.Log("Starting to force reserialize all assets...");
                AssetDatabase.ForceReserializeAssets();
                Debug.Log("Finished reserializing all assets.");
            }
        }

        /// <summary>
        /// Logs — without writing to any asset on disk — the orphaned prefab-instance property overrides
        /// in every scene under <c>Assets/</c>. The read-only companion to
        /// <see cref="PruneStalePrefabOverridesInScenes"/>: always run this first and audit the log.
        /// </summary>
        /// <remarks>
        /// "Read-only" means no file is modified; the pass does transiently open each scene to inspect it
        /// and restores the original scene setup afterward.
        /// </remarks>
        [MenuItem("Tools/Voxel Engine/Audit Stale Prefab Overrides In Scenes (dry run)")]
        public static void AuditStalePrefabOverridesInScenes() => RunOverridePass(apply: false);

        /// <summary>
        /// Removes orphaned prefab-instance property overrides from every scene under <c>Assets/</c> and
        /// saves the changed scenes.
        /// </summary>
        /// <remarks>
        /// <see cref="AssetDatabase.ForceReserializeAssets"/> rewrites assets in the current format but
        /// deliberately preserves prefab-instance overrides in <c>m_Modifications</c> even when the
        /// override's <c>propertyPath</c> no longer resolves to a serialized field (e.g. after a
        /// <c>[SerializeField]</c> is deleted) — Unity treats an unresolved override as data to keep, not
        /// a stale entry to drop, so a plain re-save leaves them behind. This pass removes exactly those.
        /// <para>
        /// Staleness is judged against the LIVE INSTANCE object, never the source prefab: a prefab
        /// override by definition differs from its source, so an override that ADDS an array element (a
        /// UnityEvent handler, a list entry) is absent from the source but present on the instance — that
        /// distinction is why an earlier source-based check wrongly deleted live Button <c>onClick</c>
        /// handlers. A removed field resolves to null on the instance too; a legitimate override does not.
        /// Overrides whose target cannot be mapped to a live instance object are left untouched
        /// (conservative), as are overrides on components with a missing script and any compound (dotted)
        /// path — array/collection entries and nested fields alike — so only simple top-level scalar/object
        /// field overrides are ever pruned.
        /// </para>
        /// <para>
        /// Known limits (both err toward leaving data alone, and the dry-run audit + version control are
        /// the backstop): (1) a field resolves to null identically whether it was removed OR merely
        /// compiled out for the current build target (platform <c>#if</c>) / otherwise conditionally
        /// serialized — reflection cannot tell these apart, so every audited candidate must be eyeballed
        /// before pruning. (2) Stale overrides originating on a NESTED prefab are left behind: the
        /// source→instance map keys on the immediate source, which does not match the outermost instance's
        /// modification target for nested-origin objects, so they resolve as unmappable and are skipped.
        /// </para>
        /// </remarks>
        [MenuItem("Tools/Voxel Engine/Prune Stale Prefab Overrides In Scenes")]
        public static void PruneStalePrefabOverridesInScenes()
        {
            if (!EditorUtility.DisplayDialog("Prune Stale Prefab Overrides",
                    "This opens every scene under Assets/ and removes prefab-instance property overrides " +
                    "that target serialized fields which no longer exist, then saves the modified scenes.\n\n" +
                    "Run the dry-run audit first and review EACH candidate: a field that is merely compiled " +
                    "out for the current build target (platform #if) or otherwise conditionally serialized " +
                    "looks identical to a removed one and would be dropped. Make sure your work is committed " +
                    "so the diff can be reverted. Continue?",
                    "Yes, Prune", "Cancel"))
            {
                return;
            }

            RunOverridePass(apply: true);
        }

        /// <summary>
        /// Core pass over every project scene: identifies stale prefab-instance overrides and, when
        /// <paramref name="apply"/> is set, removes and saves them. Read-only otherwise.
        /// </summary>
        /// <param name="apply">When true, mutate and save scenes; when false, only log candidates.</param>
        private static void RunOverridePass(bool apply)
        {
            // Opening scenes in Single mode discards unsaved edits without asking — let the user save (or
            // explicitly discard) first, and abort the whole pass if they cancel.
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                Debug.Log("[PrunePrefabOverrides] Cancelled — no scenes were touched.");
                return;
            }

            SceneSetup[] originalSetup = EditorSceneManager.GetSceneManagerSetup();
            int scenesChanged = 0;
            int overridesFound = 0;
            string mode = apply ? "prune" : "dry run";

            try
            {
                foreach (string scenePath in EnumerateProjectScenePaths())
                {
                    Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
                    int foundInScene = ProcessOpenScene(scene, apply);
                    if (foundInScene > 0)
                    {
                        overridesFound += foundInScene;
                        if (apply)
                        {
                            EditorSceneManager.MarkSceneDirty(scene);
                            EditorSceneManager.SaveScene(scene);
                            scenesChanged++;
                        }
                    }
                }
            }
            finally
            {
                RestoreOriginalScenes(originalSetup);
            }

            Debug.Log(apply
                ? $"[PrunePrefabOverrides] Done ({mode}) — dropped {overridesFound} stale override(s) " +
                  $"across {scenesChanged} scene(s)."
                : $"[PrunePrefabOverrides] Done ({mode}) — found {overridesFound} candidate override(s). " +
                  "Nothing was modified. Review EACH above (a field compiled out for the current build " +
                  "target or otherwise conditionally serialized is indistinguishable from a removed one " +
                  "here), then run the prune command to apply.");
        }

        /// <summary>
        /// Restores the editor's scene setup captured before the pass, best-effort.
        /// </summary>
        /// <param name="setup">The <see cref="SceneSetup"/> array from before the pass opened any scene.</param>
        /// <remarks>
        /// <see cref="EditorSceneManager.RestoreSceneManagerSetup"/> requires every entry to have a valid
        /// saved path and one active scene; an untitled/unsaved scene yields an empty path it rejects with
        /// an exception. When the captured setup is not restorable we leave the last-processed scene open
        /// rather than let a restore failure surface as an unhandled error after an otherwise-clean pass.
        /// </remarks>
        private static void RestoreOriginalScenes(SceneSetup[] setup)
        {
            if (setup == null || setup.Length == 0 || setup.Any(s => string.IsNullOrEmpty(s.path)))
            {
                Debug.LogWarning("[PrunePrefabOverrides] Could not restore the original scene setup " +
                                 "(an unsaved/untitled scene was open before the pass); leaving the " +
                                 "last-processed scene open.");
                return;
            }

            try
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[PrunePrefabOverrides] Failed to restore the original scene setup: {e.Message}");
            }
        }

        /// <summary>All <c>.unity</c> scene asset paths under the project's <c>Assets/</c> folder.</summary>
        /// <returns>The project-relative scene paths (excludes packages).</returns>
        private static IEnumerable<string> EnumerateProjectScenePaths()
        {
            return AssetDatabase.FindAssets("t:Scene")
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(p => p.StartsWith("Assets/"))
                .Distinct();
        }

        /// <summary>
        /// Finds (and optionally removes) stale overrides on every outermost prefab instance in a scene.
        /// </summary>
        /// <param name="scene">The currently-open scene to process.</param>
        /// <param name="apply">When true, applies the pruned modification set back to the instance.</param>
        /// <returns>The number of stale overrides found in this scene.</returns>
        private static int ProcessOpenScene(Scene scene, bool apply)
        {
            int found = 0;

            foreach (GameObject root in scene.GetRootGameObjects())
            {
                foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                {
                    GameObject go = t.gameObject;
                    if (!PrefabUtility.IsAnyPrefabInstanceRoot(go)) continue;

                    // GetPropertyModifications on the outermost root returns the whole instance's
                    // modifications (nested included), and SetPropertyModifications expects the outermost
                    // target — so process outermost roots only, or a nested instance is handled twice.
                    if (PrefabUtility.GetOutermostPrefabInstanceRoot(go) != go) continue;

                    PropertyModification[] mods = PrefabUtility.GetPropertyModifications(go);
                    if (mods == null || mods.Length == 0) continue;

                    Dictionary<Object, Object> sourceToInstance = BuildSourceToInstanceMap(go);

                    List<PropertyModification> kept = new List<PropertyModification>(mods.Length);
                    foreach (PropertyModification mod in mods)
                    {
                        if (IsStaleOverride(mod, sourceToInstance))
                        {
                            found++;
                            Debug.Log($"[PrunePrefabOverrides] {scene.name}: stale override " +
                                      $"'{mod.propertyPath}' on '{go.name}' " +
                                      $"(target: {(mod.target != null ? mod.target.GetType().Name : "null")}).", go);
                            continue;
                        }

                        kept.Add(mod);
                    }

                    if (apply && kept.Count != mods.Length)
                    {
                        PrefabUtility.SetPropertyModifications(go, kept.ToArray());
                    }
                }
            }

            return found;
        }

        /// <summary>
        /// Maps each source-prefab object to its live instance counterpart under a prefab instance root,
        /// so a <see cref="PropertyModification.target"/> (a source object) can be resolved to the actual
        /// instantiated object whose serialized graph reflects the applied overrides.
        /// </summary>
        /// <param name="instanceRoot">The outermost prefab instance root.</param>
        /// <returns>A source-object → instance-object lookup for GameObjects and their components.</returns>
        private static Dictionary<Object, Object> BuildSourceToInstanceMap(GameObject instanceRoot)
        {
            Dictionary<Object, Object> map = new Dictionary<Object, Object>();

            foreach (Transform t in instanceRoot.GetComponentsInChildren<Transform>(true))
            {
                GameObject igo = t.gameObject;
                Object goSource = PrefabUtility.GetCorrespondingObjectFromSource(igo);
                if (goSource != null) map[goSource] = igo;

                foreach (Component c in igo.GetComponents<Component>())
                {
                    // Missing-script components surface as null here; skipping them keeps them out of the
                    // map, which is what makes IsStaleOverride conservative about them (unmappable target).
                    if (c == null) continue;
                    Object cSource = PrefabUtility.GetCorrespondingObjectFromSource(c);
                    if (cSource != null) map[cSource] = c;
                }
            }

            return map;
        }

        /// <summary>
        /// Decides whether a prefab-instance override targets a serialized property that no longer exists
        /// on the live instance object (i.e. the field was removed from the script).
        /// </summary>
        /// <param name="mod">The override to test.</param>
        /// <param name="sourceToInstance">Source → instance object map for this prefab instance.</param>
        /// <returns><c>true</c> only when the override is provably orphaned and safe to drop.</returns>
        /// <remarks>
        /// The property is resolved against the INSTANCE object, not the source, because a valid override
        /// (e.g. an added array element) is absent from the source by design. Conservative on every
        /// uncertainty — a null <c>propertyPath</c>, a null target, a target that cannot be mapped to a
        /// live instance object, any compound (dotted) path, or a field that resolves to null only
        /// because it was renamed via <see cref="FormerlySerializedAsAttribute"/> — is treated as NOT
        /// stale, so real data is never removed on a guess. (Missing-script components never reach here:
        /// they are excluded while <see cref="BuildSourceToInstanceMap"/> builds the map.)
        /// </remarks>
        private static bool IsStaleOverride(PropertyModification mod, Dictionary<Object, Object> sourceToInstance)
        {
            if (mod == null) return false;
            // A null target/path cannot be verified against a live object — never prune on that alone.
            if (mod.target == null || string.IsNullOrEmpty(mod.propertyPath)) return false;

            // Scope guard: only ever prune a SIMPLE top-level field override — the single-segment shape a
            // removed [SerializeField] leaves behind (e.g. "_enableParallelFluidTick"). Any compound path
            // (a '.') is excluded outright: it covers array/collection entries (UnityEvent persistent-call
            // lists — an out-of-range "...Array.data[1]" is orphaned yet harmless) AND nested fields,
            // whose [FormerlySerializedAs] renames can live on an inner field this pass cannot safely
            // resolve — mis-pruning either silently loses real data, no benefit worth that risk.
            if (mod.propertyPath.IndexOf('.') >= 0)
            {
                return false;
            }

            if (!sourceToInstance.TryGetValue(mod.target, out Object instanceObj) || instanceObj == null)
            {
                return false; // cannot resolve to a live object — do not guess
            }

            using SerializedObject so = new SerializedObject(instanceObj);
            if (so.FindProperty(mod.propertyPath) != null)
            {
                return false; // resolves under the current name — a live override
            }

            // Resolves to null: either a removed field (stale) OR a rename still carrying the old name,
            // which FindProperty cannot see (it matches current names only). Keep the latter so Unity's
            // [FormerlySerializedAs] migration still fires and the value is not lost.
            return !MatchesFormerlySerializedName(instanceObj, mod.propertyPath);
        }

        /// <summary>
        /// Reports whether <paramref name="fieldName"/> matches a <see cref="FormerlySerializedAsAttribute"/>
        /// old name on any serialized field of the object's type hierarchy — i.e. the override is a pending
        /// rename, not an orphan. Only ever called with a single-segment (top-level) field name.
        /// </summary>
        /// <param name="instanceObj">The live instance object the override targets.</param>
        /// <param name="fieldName">The override's (single-segment) serialized field name.</param>
        /// <returns><c>true</c> if the name corresponds to a renamed (not removed) field.</returns>
        private static bool MatchesFormerlySerializedName(Object instanceObj, string fieldName)
        {
            for (System.Type type = instanceObj.GetType();
                 type != null && type != typeof(object);
                 type = type.BaseType)
            {
                FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public |
                                                    BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                foreach (FieldInfo field in fields)
                {
                    foreach (FormerlySerializedAsAttribute attr in
                             field.GetCustomAttributes<FormerlySerializedAsAttribute>(false))
                    {
                        if (attr.oldName == fieldName) return true;
                    }
                }
            }

            return false;
        }
    }
}
