using System;
using UnityEngine;

namespace Data
{
    [CreateAssetMenu(fileName = "New Voxel Mesh Data", menuName = "Minecraft/Voxel Mesh Data")]
    public class VoxelMeshData : ScriptableObject
    {
        [Tooltip("Name of the block type.")]
        public string blockName;

        [Tooltip("6 Faces in order: Back, Front, Top, Bottom, Left, Right")]
        public FaceMeshData[] faces;
    }

    [Serializable]
    public class VertData
    {
        [Tooltip("Position relative to the voxel's origin point.")]
        public Vector3 position;

        [Tooltip("Texture UV relative to the origin as defined by BlockTypes.")]
        public Vector2 uv;

        /// <summary>
        /// Creates a new vertex data configuration.
        /// </summary>
        /// <param name="position">The relatively local position of the vertex.</param>
        /// <param name="uv">The texture UV offset.</param>
        public VertData(Vector3 position, Vector2 uv)
        {
            this.position = position;
            this.uv = uv;
        }

        /// <summary>
        /// Gets the vertex position rotated around the center (0.5, 0.5, 0.5) by the given Euler angles.
        /// </summary>
        /// <param name="angles">Euler angles to rotate by.</param>
        /// <returns>The rotated Vector3 position.</returns>
        public Vector3 GetRotatedPosition(Vector3 angles)
        {
            Vector3 center = new Vector3(0.5f, 0.5f, 0.5f); // The center of the block that we are pivoting around.
            Vector3 direction = position - center; // Get the direction from the center of the current vertice.
            direction = Quaternion.Euler(angles) * direction; // Rotate the direction by angels specified in the function parameters.
            return direction + center;
        }
    }

    [Serializable]
    public class FaceMeshData
    {
        // Because all the verts in this face are facing the same direction,
        // we can store a single normal value for each face and use that for each vert in the face.

        public string direction; // Purely to make things easier to read in the inspector.
        public VertData[] vertData;
        public int[] triangles;

        /// <summary>
        /// Retrieves the vertex data at the specified index.
        /// </summary>
        /// <param name="index">The zero-based index of the vertex.</param>
        /// <returns>The <see cref="VertData"/> for the given index.</returns>
        public VertData GetVertData(int index)
        {
            return vertData[index];
        }
    }
}
