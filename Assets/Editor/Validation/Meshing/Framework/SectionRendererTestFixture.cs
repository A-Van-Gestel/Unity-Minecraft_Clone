using System;
using System.Reflection;
using Data;
using Unity.Collections;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Editor.Validation.Meshing.Framework
{
    /// <summary>
    /// MH-6 — a lightweight, edit-mode fixture for the renderer apply-path
    /// (<see cref="SectionRenderer.UpdateMeshNative"/>), the unit that <b>MR-3</b> (material-combination
    /// caching) and the renderer side of <b>MR-4</b> (constant <see cref="Mesh.bounds"/>) will change.
    /// <para>
    /// This is deliberately a <b>separate</b> harness from the meshing-<i>job</i> suite's
    /// <see cref="MeshingTestWorld"/>: it instantiates a real <see cref="SectionRenderer"/>
    /// <see cref="MonoBehaviour"/>-backed object and observes the result through its public
    /// <see cref="SectionRenderer.GameObject"/> — never the private <c>_meshRenderer</c>/<c>_mesh</c>
    /// fields. Material selection in <c>UpdateMeshNative</c> depends only on the three submesh
    /// <c>count</c> arguments, so no real meshing job is needed; the synthetic <see cref="NativeArray{T}"/>
    /// inputs only have to be sized consistently.
    /// </para>
    /// <para>
    /// <b>Seam (the blocker, option (a) — reflection-stub):</b> <c>UpdateMeshNative</c> reaches into
    /// <c>World.Instance.{Opaque,Transparent,Liquid}Material</c>, which is <c>null</c> in edit mode and
    /// would NRE. This fixture reflects the private <see cref="World.Instance"/> setter onto an
    /// <c>AddComponent</c>'d <see cref="World"/> whose public <c>blockDatabase</c> field is a stub
    /// <see cref="BlockDatabase"/> holding three <b>distinct</b> dummy <see cref="Material"/>s. Because
    /// <see cref="World"/> is a plain <see cref="MonoBehaviour"/> (no <c>[ExecuteAlways]</c>/
    /// <c>OnValidate</c>), <c>AddComponent</c> runs <b>no</b> lifecycle in edit mode and the setter is
    /// driven directly — so <c>World.Awake</c> never executes (zero production-code change, no world-init
    /// side effects). <see cref="Dispose"/> restores the previous <see cref="World.Instance"/> and
    /// <see cref="Object.DestroyImmediate(Object)"/>s every object it created.
    /// </para>
    /// <para>
    /// <b>Build-alongside follow-up:</b> when MR-3 (and the renderer side of MR-4 / the MR-6 pooling work)
    /// is implemented, upgrade this seam to option (b) — give <c>UpdateMeshNative</c> its materials (or a
    /// cached material-set) by injection instead of reaching into the singleton — the long-term cleaner
    /// architecture this reflection stub stands in for.
    /// </para>
    /// </summary>
    public sealed class SectionRendererTestFixture : IDisposable
    {
        /// <summary>The real renderer under test.</summary>
        public readonly SectionRenderer Renderer;

        /// <summary>The stub opaque-submesh material (distinct identity).</summary>
        public readonly Material OpaqueMaterial;

        /// <summary>The stub transparent-submesh material (distinct identity).</summary>
        public readonly Material TransparentMaterial;

        /// <summary>The stub fluid-submesh material (distinct identity).</summary>
        public readonly Material LiquidMaterial;

        private readonly GameObject _worldGo;
        private readonly GameObject _parentGo;
        private readonly BlockDatabase _stubDatabase;
        private readonly World _previousInstance;
        private bool _disposed;

        /// <summary>
        /// Builds the seam (stub <see cref="World.Instance"/> + materials) and a real
        /// <see cref="SectionRenderer"/> parented under a throwaway GameObject.
        /// </summary>
        /// <param name="sectionIndex">Section index passed to the renderer (controls only its local Y offset).</param>
        public SectionRendererTestFixture(int sectionIndex = 0)
        {
            // Capture the previous instance up front so the failure-cleanup path can always restore it.
            _previousInstance = World.Instance;

            try
            {
                // Three DISTINCT materials so submesh-order assertions can never pass with aliased materials.
                // "Hidden/Internal-Colored" is a built-in shader that is always present in the editor; the
                // actual shader is irrelevant — only the three Material object identities matter.
                Shader shader = Shader.Find("Hidden/Internal-Colored");
                if (shader == null)
                    throw new InvalidOperationException(
                        "MH-6 fixture: built-in shader 'Hidden/Internal-Colored' was not found; cannot build stub materials.");

                OpaqueMaterial = new Material(shader) { name = "MH6_Opaque" };
                TransparentMaterial = new Material(shader) { name = "MH6_Transparent" };
                LiquidMaterial = new Material(shader) { name = "MH6_Liquid" };

                _stubDatabase = ScriptableObject.CreateInstance<BlockDatabase>();
                _stubDatabase.opaqueMaterial = OpaqueMaterial;
                _stubDatabase.transparentMaterial = TransparentMaterial;
                _stubDatabase.liquidMaterial = LiquidMaterial;

                _worldGo = new GameObject("MH6_StubWorld");
                // AddComponent on a plain MonoBehaviour does NOT invoke Awake/OnEnable/OnValidate in edit mode,
                // so no World initialization runs — we only need the component as the typed Instance target.
                World world = _worldGo.AddComponent<World>();
                world.blockDatabase = _stubDatabase;

                _parentGo = new GameObject("MH6_SectionParent");

                // Construct the renderer BEFORE claiming the World.Instance singleton: the ctor does not read
                // Instance (only UpdateMeshNative does), so doing it first means a renderer-ctor failure can
                // never strand a stub world in the global singleton. The try/catch below is the second line of
                // defense — the caller's `using` does NOT run Dispose on a constructor that threw (the variable
                // was never assigned), so any partial-construction failure must be torn down here, restoring
                // World.Instance and destroying whatever was already created before the exception escapes.
                Renderer = new SectionRenderer(_parentGo.transform, sectionIndex);

                // Drive the private `World.Instance` setter directly (bypassing Awake). It is null in a normal
                // edit-mode session; _previousInstance (captured above) is restored on Dispose.
                SetWorldInstance(world);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>The renderer GameObject's current shared materials (a fresh copy per Unity's getter).</summary>
        public Material[] SharedMaterials => Renderer.GameObject.GetComponent<MeshRenderer>().sharedMaterials;

        /// <summary>The renderer mesh's current bounds (read via <c>sharedMesh</c> to avoid instantiating a copy).</summary>
        public Bounds MeshBounds => Renderer.GameObject.GetComponent<MeshFilter>().sharedMesh.bounds;

        /// <summary>Whether the renderer GameObject is currently active.</summary>
        public bool IsActive => Renderer.GameObject.activeSelf;

        /// <summary>
        /// Drives the real <see cref="SectionRenderer.UpdateMeshNative"/> with synthetic inputs. The
        /// vertex streams are sized to <paramref name="verts"/>; each present submesh gets a same-length
        /// index run of all-zero indices (validation is skipped via <c>DontValidateIndices</c>, so the
        /// degenerate triangles are harmless — nothing is rendered). Material selection and the
        /// active/inactive decision therefore depend only on the counts, exactly as in production.
        /// </summary>
        /// <param name="verts">Vertex positions (mesh-local). Length is the vertex count; pass an empty array for the empty-section path.</param>
        /// <param name="opaqueCount">Opaque-submesh index count (&gt;0 marks the opaque submesh present).</param>
        /// <param name="transparentCount">Transparent-submesh index count (&gt;0 marks it present).</param>
        /// <param name="fluidCount">Fluid-submesh index count (&gt;0 marks it present).</param>
        public void RunUpdate(Vector3[] verts, int opaqueCount, int transparentCount, int fluidCount)
        {
            int vertexCount = verts.Length;

            NativeArray<Vector3> v = new NativeArray<Vector3>(verts, Allocator.Temp);
            NativeArray<Vector4> uvs = new NativeArray<Vector4>(vertexCount, Allocator.Temp);
            NativeArray<Color> colors = new NativeArray<Color>(vertexCount, Allocator.Temp);
            NativeArray<NormalLightVertex> stream3 = new NativeArray<NormalLightVertex>(vertexCount, Allocator.Temp);

            NativeArray<int> opaqueTris = new NativeArray<int>(Mathf.Max(opaqueCount, 0), Allocator.Temp);
            NativeArray<int> transparentTris = new NativeArray<int>(Mathf.Max(transparentCount, 0), Allocator.Temp);
            NativeArray<int> fluidTris = new NativeArray<int>(Mathf.Max(fluidCount, 0), Allocator.Temp);

            try
            {
                Renderer.UpdateMeshNative(
                    v, uvs, colors, stream3, vertexStart: 0, vertexCount,
                    opaqueTris, opaqueStart: 0, opaqueCount,
                    transparentTris, transparentStart: 0, transparentCount,
                    fluidTris, fluidStart: 0, fluidCount);
            }
            finally
            {
                v.Dispose();
                uvs.Dispose();
                colors.Dispose();
                stream3.Dispose();
                opaqueTris.Dispose();
                transparentTris.Dispose();
                fluidTris.Dispose();
            }
        }

        /// <summary>Restores the previous <see cref="World.Instance"/> and destroys every object the fixture created.</summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            SetWorldInstance(_previousInstance);

            // Destroy the renderer's mesh (a bare Object, not a child of any GameObject) before the GO,
            // then the section GO (a child of _parentGo) via its parent, then the stub world + assets.
            if (Renderer?.GameObject != null)
            {
                MeshFilter filter = Renderer.GameObject.GetComponent<MeshFilter>();
                if (filter != null && filter.sharedMesh != null) Object.DestroyImmediate(filter.sharedMesh);
            }

            if (_parentGo != null) Object.DestroyImmediate(_parentGo);
            if (_worldGo != null) Object.DestroyImmediate(_worldGo);
            if (_stubDatabase != null) Object.DestroyImmediate(_stubDatabase);
            if (OpaqueMaterial != null) Object.DestroyImmediate(OpaqueMaterial);
            if (TransparentMaterial != null) Object.DestroyImmediate(TransparentMaterial);
            if (LiquidMaterial != null) Object.DestroyImmediate(LiquidMaterial);
        }

        /// <summary>Sets the private static <see cref="World.Instance"/> auto-property via reflection.</summary>
        private static void SetWorldInstance(World value)
        {
            PropertyInfo prop = typeof(World).GetProperty(
                nameof(World.Instance), BindingFlags.Public | BindingFlags.Static);
            MethodInfo setter = prop?.GetSetMethod(nonPublic: true);
            if (setter == null)
                throw new InvalidOperationException("Could not locate the private World.Instance setter via reflection.");

            setter.Invoke(null, new object[] { value });
        }
    }
}
