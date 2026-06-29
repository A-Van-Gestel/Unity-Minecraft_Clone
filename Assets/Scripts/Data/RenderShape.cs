namespace Data
{
    /// <summary>
    /// Represents the mesh generation strategy used for a block.
    /// </summary>
    public enum RenderShape : byte
    {
        /// <summary>
        /// A standard cube composed of 6 cardinal faces.
        /// </summary>
        Cube = 0,

        /// <summary>
        /// A custom mesh defined by a VoxelMeshData ScriptableObject.
        /// </summary>
        CustomMesh = 1,

        /// <summary>
        /// Two intersecting diagonal planes (4 faces total, double-sided) used for flora like grass and flowers.
        /// Bypasses cardinal face culling to ensure internal planes are always rendered.
        /// </summary>
        CrossMesh = 2,
    }
}
