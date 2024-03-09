using UnityEngine;
using UnityEngine.Serialization;

namespace Data
{
    [CreateAssetMenu(fileName = "New Voxel Mesh Data", menuName = "MinecraftTutorial/Voxel Mesh Data")]
    public class VoxelMeshData : ScriptableObject
    {
        [Tooltip("Name of the block type.")]
        public string blockName;
        
        [Tooltip("6 Faces in order: Back, Front, Top, Bottom, Left, Right")]
        public FaceMeshData[] faces;
    }
    
    [System.Serializable]
    public class VertData
    {
        [Tooltip("Position relative to the voxel's origin point.")]
        public Vector3 position;
        [Tooltip("Texture UV relative to the origin as defined by BlockTypes.")]
        public Vector2 uv;

        public VertData(Vector3 position, Vector2 uv)
        {
            this.position = position;
            this.uv = uv;
        }
    }

    [System.Serializable]
    public class FaceMeshData
    {
        // Because all of the verts in this face are facing the same direction,
        // we can store a single normal value for each face and use that for each vert in the face.
        
        public string direction;  // Purely to make things easier to read in the inspector.
        public Vector3 normal;
        public VertData[] vertData;
        public int[] triangles;
    }
}
