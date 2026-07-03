# Cave Tuning — Ready-to-Adapt Unity_RunCommand Scripts

Companion reference for the `cave-tuning` skill. All scripts run via `Unity_RunCommand`
(see the `unity-mcp` skill for the `CommandScript` calling conventions).

## Analyzing a biome NOT in the WorldTypeDefinition

The string-name `RunAnalysis` overload only resolves biomes registered in the active
`WorldTypeDefinition`. For others (e.g. Steep Grasslands), load the asset directly:

```csharp
using Editor.Dev;
using Data.WorldTypes;
using UnityEditor;
var guids = AssetDatabase.FindAssets("Steep Grasslands t:StandardBiomeAttributes");
var biome = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(
    AssetDatabase.GUIDToAssetPath(guids[0]));
return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, true, biome);
```

## Trunk worm domination — diagnostic script

Temporarily disable trunk worms, analyze, re-enable. See the skill's "Trunk worm volume
domination" section for when to run this and how to interpret the result.

```csharp
using Editor.Dev;
using Data.WorldTypes;
using UnityEditor;

// 1. Temporarily disable trunk worms
var wtd = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(
    AssetDatabase.GUIDToAssetPath(
        AssetDatabase.FindAssets("Standard t:WorldTypeDefinition")[0]));
var so = new SerializedObject(wtd);
so.FindProperty("trunkWormConfig.enabled").boolValue = false;
so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();

// 2. Analyze the biome (use fresh origin to bypass cache)
var result = CaveDensityAnalyzer.RunAnalysis(8, 42, 200, 200, "Grasslands");

// 3. Re-enable trunk worms
so.FindProperty("trunkWormConfig.enabled").boolValue = true;
so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
return result;
```

## Modifying biome .asset files

Per CLAUDE.md rules, never edit `.asset` files directly. Use `SerializedObject`
(property paths in [parameter-reference.md](parameter-reference.md)):

```csharp
using Data.WorldTypes;
using UnityEditor;
using System.IO;

// Safe biome lookup — FindAssets("Grasslands") also matches "Steep Grasslands"
// (substring match). Always filter by exact asset filename.
var guids = AssetDatabase.FindAssets("Grasslands t:StandardBiomeAttributes");
string targetPath = null;
foreach (var g in guids)
{
    var p = AssetDatabase.GUIDToAssetPath(g);
    if (Path.GetFileNameWithoutExtension(p) == "Grasslands") { targetPath = p; break; }
}
var biome = AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(targetPath);
var so = new SerializedObject(biome);

so.FindProperty("caveZoneNoiseConfig.frequency").floatValue = 0.008f;

var layers = so.FindProperty("caveLayers");
var l0 = layers.GetArrayElementAtIndex(0); // WormCarver layer
l0.FindPropertyRelative("wormSpawnChance").floatValue = 0.025f;
l0.FindPropertyRelative("wormShape.radiusMax").floatValue = 3.5f;
l0.FindPropertyRelative("wormShape.radiusNoiseStrength").floatValue = 0.15f;
l0.FindPropertyRelative("wormYAttraction.strength").floatValue = 0.18f;
l0.FindPropertyRelative("wormYAttraction.maxY").floatValue = 52.0f;

var l1 = layers.GetArrayElementAtIndex(1); // Cheese layer
l1.FindPropertyRelative("threshold").floatValue = 0.84f;
l1.FindPropertyRelative("zoneAttenuation").floatValue = 0.26f;
l1.FindPropertyRelative("isSeekableByLocalWorms").boolValue = true;
l1.FindPropertyRelative("isSeekableByTrunkWorms").boolValue = true;

so.ApplyModifiedProperties();
AssetDatabase.SaveAssets();
return "Done";
```
