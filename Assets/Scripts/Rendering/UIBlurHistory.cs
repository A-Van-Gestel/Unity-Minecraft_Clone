using UnityEngine;
using UnityEngine.Rendering;

namespace Rendering
{
    /// <summary>
    /// Per-camera storage for the finished UI blur, held by URP's camera history system
    /// and sampled by UI shaders as <c>_UIBlurTexture</c>.
    /// </summary>
    /// <remarks>
    /// The blur target cannot be a render graph texture: Screen Space - Overlay canvases draw after
    /// the graph has executed, and a graph-created texture is returned to the pool at its last-used
    /// pass, where a later pass can be handed the same memory. Keying the storage per camera (rather
    /// than one handle on the feature) keeps a Game and a Scene view at different resolutions from
    /// reallocating each other's target every frame. Lifetime follows the camera.
    /// </remarks>
    public sealed class UIBlurHistory : CameraHistoryItem
    {
        private int _textureId;
        private Hash128 _descriptorKey;

        /// <inheritdoc/>
        public override void OnCreate(BufferedRTHandleSystem owner, uint typeId)
        {
            base.OnCreate(owner, typeId);
            _textureId = MakeId(0);
        }

        /// <summary>
        /// The blur target for this camera, or <c>null</c> before the first <see cref="Update"/>.
        /// </summary>
        public RTHandle CurrentTexture => GetCurrentFrameRT(_textureId);

        /// <summary>
        /// Releases this camera's blur target. Called by URP when the camera is destroyed or when
        /// the history goes unrequested.
        /// </summary>
        public override void Reset()
        {
            ReleaseHistoryFrameRT(_textureId);
            _descriptorKey = default;
        }

        /// <summary>
        /// Allocates the blur target, reallocating it when the descriptor changes (resolution,
        /// format, or downsample factor).
        /// </summary>
        /// <param name="descriptor">Descriptor of the downsampled blur target.</param>
        /// <returns>The target to render the final blur iteration into.</returns>
        public RTHandle Update(ref RenderTextureDescriptor descriptor)
        {
            Hash128 key = Hash128.Compute(ref descriptor);
            if (_descriptorKey != key)
            {
                Reset();
                _descriptorKey = key;
            }

            if (CurrentTexture == null)
                AllocHistoryFrameRT(_textureId, 1, ref descriptor, FilterMode.Bilinear, "_UIBlurTexture");

            return CurrentTexture;
        }
    }
}
