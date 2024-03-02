using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

[CreateAssetMenu(fileName = "BiomeAttributes", menuName = "MinecraftTutorial/Biome Attributes")]
public class BiomeAttributes : ScriptableObject
{
    [Header("Biome Terrain")]
    [Tooltip("Name of the biome.")]
    public string biomeName;

    public int offset;
    public float scale;

    [Tooltip("Additional height the terrain should go, starting from the solid terrain.")]
    public int terrainHeight;

    [Tooltip("Terrain scale for the noise function. Smaller values means larger sizes.")]
    public float terrainScale;

    public byte surfaceBlock = 2;
    public byte subSurfaceBlock = 3;

    [Header("Major Flora")]
    public bool placeMajorFlora = true;
    public int majorFloraIndex = 0;
    public float majorFloraZoneScale = 1.3f;
    [Range(0.1f, 1f)]
    public float majorFloraZoneThreshold = 0.6f;
    public float majorFloraPlacementScale = 15f;

    [Range(0.1f, 1f)]
    public float majorFloraPlacementThreshold = 0.8f;
    
    public int maxHeight = 12;
    public int minHeight = 5;

    [Header("Second Pass")]
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