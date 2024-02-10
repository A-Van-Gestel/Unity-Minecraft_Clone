using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(fileName = "BiomeAttributes", menuName = "MinecraftTutorial/Biome Attributes")]
public class BiomeAttributes: ScriptableObject
{
    [Tooltip("Name of the biome.")]
    public string biomeName;
    
    [Tooltip("Terrain under this value will always be solid.")]
    public int solidGroundHeight;
    [Tooltip("Additional height the terrain should go, starting from the solid terrain.")]
    public int terrainHeight;
    [Tooltip("Terrain scale for the noise function. Smaller values means larger sizes.")]
    public float terrainScale;

    public Lode[] Lodes;
}

[System.Serializable]
public class Lode
{
    [Tooltip("Name of the lode.")]
    public string nodeName;
    [Tooltip("ID of the block that will be generated.")]
    public byte blockID;
    [Tooltip("Blocks will not be generated below this height.")]
    public int minHeight;
    [Tooltip("Blocks will not be generated above this height.")]
    public int maxHeight;
    [Tooltip("Node scale for the noise function. Smaller values means larger sizes.")]
    public float scale;
    [Tooltip("Terrain scale for the noise function. Smaller values means larger sizes.")]
    public float threshold;
    [Tooltip("Node offset for the noise function. This moves the sampled area of the generated noise map so that same parameters still results in different noise maps.")]
    public float noiseOffset;
}
