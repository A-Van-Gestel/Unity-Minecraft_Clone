# Cave Tuning — Ready-to-Adapt Unity CLI Scripts

Companion reference for the `cave-tuning` skill. Each script is a small C# file run with
`unity command run_script` (see the `unity-editor` skill for the CLI mechanics). Save it under the
scratchpad, never under `Assets/`, and call its static entry point:

```bash
unity command run_script --file <path>.cs --entry <Class>.<Method> --args '[...]' \
    --timeout_ms 180000 --timeout 200 --result-only
```

`run_script` compiles a real file, so `using` directives work. **Every argument must be passed**
(C# default values are not applied). Check a script compiles without running it by adding
`--dry_run true`. One-line analyzer calls go through `eval` instead, with fully qualified names,
because `eval` rejects `using` directives (see the skill's "How to run it").

## Analyzing a biome NOT in the WorldTypeDefinition

The string-name `RunAnalysis` overload only resolves biomes registered in the active
`WorldTypeDefinition`. For others (e.g. Steep Grasslands), load the asset directly.
Entry: `CaveUnregisteredBiome.Run`, args `["Steep Grasslands"]`.

```csharp
using System.IO;
using Data.WorldTypes;
using Editor.Dev;
using UnityEditor;

public static class CaveUnregisteredBiome
{
    public static string Run(string biomeFileName)
    {
        // FindAssets matches substrings ("Grasslands" also finds "Steep Grasslands"): filter by exact file name.
        foreach (string guid in AssetDatabase.FindAssets(biomeFileName + " t:StandardBiomeAttributes"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == biomeFileName)
                return CaveDensityAnalyzer.RunAnalysis(8, 42, 0, 0, true,
                    AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(path));
        }

        return "biome asset not found: " + biomeFileName;
    }
}
```

## Trunk worm domination — diagnostic script

Temporarily disable trunk worms, analyze, re-enable. See the skill's "Trunk worm volume
domination" section for when to run this and how to interpret the result. The `finally` block
restores the asset even if the analysis throws. Entry: `CaveTrunkDiagnostic.Run`, args
`["Grasslands"]`.

```csharp
using Data.WorldTypes;
using Editor.Dev;
using UnityEditor;

public static class CaveTrunkDiagnostic
{
    public static string Run(string biomeName)
    {
        var wtd = AssetDatabase.LoadAssetAtPath<WorldTypeDefinition>(
            AssetDatabase.GUIDToAssetPath(AssetDatabase.FindAssets("Standard t:WorldTypeDefinition")[0]));
        var so = new SerializedObject(wtd);
        SerializedProperty enabled = so.FindProperty("trunkWormConfig.enabled");

        enabled.boolValue = false;
        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        try
        {
            // A fresh origin bypasses the analyzer's cache.
            return CaveDensityAnalyzer.RunAnalysis(8, 42, 200, 200, biomeName);
        }
        finally
        {
            enabled.boolValue = true;
            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }
    }
}
```

## Modifying biome .asset files

Per CLAUDE.md rules, never edit `.asset` files directly. Use `SerializedObject`
(property paths in [parameter-reference.md](parameter-reference.md)). Adapt the values, then run
it with entry `CaveBiomeTuning.Run`, args `["Grasslands"]`.

```csharp
using System.IO;
using Data.WorldTypes;
using UnityEditor;

public static class CaveBiomeTuning
{
    public static string Run(string biomeFileName)
    {
        // Safe biome lookup — FindAssets("Grasslands") also matches "Steep Grasslands".
        string targetPath = null;
        foreach (string guid in AssetDatabase.FindAssets(biomeFileName + " t:StandardBiomeAttributes"))
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            if (Path.GetFileNameWithoutExtension(path) == biomeFileName) { targetPath = path; break; }
        }

        if (targetPath == null)
            return "biome asset not found: " + biomeFileName;

        var so = new SerializedObject(AssetDatabase.LoadAssetAtPath<StandardBiomeAttributes>(targetPath));
        so.FindProperty("caveZoneNoiseConfig.frequency").floatValue = 0.008f;

        SerializedProperty layers = so.FindProperty("caveLayers");
        SerializedProperty worm = layers.GetArrayElementAtIndex(0); // WormCarver layer
        worm.FindPropertyRelative("wormSpawnChance").floatValue = 0.025f;
        worm.FindPropertyRelative("wormShape.radiusMax").floatValue = 3.5f;
        worm.FindPropertyRelative("wormShape.radiusNoiseStrength").floatValue = 0.15f;
        worm.FindPropertyRelative("wormYAttraction.strength").floatValue = 0.18f;
        worm.FindPropertyRelative("wormYAttraction.maxY").floatValue = 52.0f;

        SerializedProperty cheese = layers.GetArrayElementAtIndex(1); // Cheese layer
        cheese.FindPropertyRelative("threshold").floatValue = 0.84f;
        cheese.FindPropertyRelative("zoneAttenuation").floatValue = 0.26f;
        cheese.FindPropertyRelative("isSeekableByLocalWorms").boolValue = true;
        cheese.FindPropertyRelative("isSeekableByTrunkWorms").boolValue = true;

        so.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();
        return "updated " + targetPath;
    }
}
```
