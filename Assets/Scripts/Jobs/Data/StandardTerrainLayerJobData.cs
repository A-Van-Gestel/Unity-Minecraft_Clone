using Data.WorldTypes;

namespace Jobs.Data
{
    /// <summary>
    /// Blittable, job-safe representation of a <see cref="Data.WorldTypes.StandardTerrainLayer"/>.
    /// Used to sequentially apply subsurface block strata (e.g. Dirt) below the surface block, dynamically.
    /// </summary>
    public struct StandardTerrainLayerJobData
    {
        public readonly byte BlockID;
        public readonly int Depth;

        /// <summary>
        /// Constructs a <see cref="StandardTerrainLayerJobData"/> from its authoring class.
        /// </summary>
        public StandardTerrainLayerJobData(StandardTerrainLayer authoring)
        {
            BlockID = (byte)authoring.blockID;
            Depth = authoring.depth;
        }
    }
}
