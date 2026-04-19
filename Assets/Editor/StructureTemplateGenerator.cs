using System.Collections.Generic;
using Data;
using Data.Structures;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    public class StructureTemplateGenerator : EditorWindow
    {
        [MenuItem("Minecraft Clone/Generate Structure Templates")]
        public static void ShowWindow()
        {
            GetWindow<StructureTemplateGenerator>("Generate Structure Templates");
        }

        private void OnGUI()
        {
            GUILayout.Label("Structure Generators", EditorStyles.boldLabel);

            if (GUILayout.Button("Generate Standard Oak Tree"))
            {
                GenerateOakTree();
            }

            if (GUILayout.Button("Generate Standard Cactus"))
            {
                GenerateCactus();
            }
        }

        private static void GenerateOakTree()
        {
            EnsureDirectory("Assets/Data/WorldGen/Structures");
            EnsureDirectory("Assets/Data/WorldGen/Structures");
            EnsureDirectory("Assets/Data/WorldGen/Structures/Trees");

            // 1. Create Parts
            StructurePartTemplate leavesPart = CreateOrGetAsset<StructurePartTemplate>("Assets/Data/WorldGen/Structures/Trees/Part_OakLeaves.asset");
            StructurePartTemplate trunkPart = CreateOrGetAsset<StructurePartTemplate>("Assets/Data/WorldGen/Structures/Trees/Part_OakTrunk.asset");

            // Populate Leaves (matching MakeTree output exactly)
            List<StructureBlock> leavesBlocks = new List<StructureBlock>();
            for (int x = -2; x < 3; x++)
            {
                for (int z = -2; z < 3; z++)
                {
                    leavesBlocks.Add(new StructureBlock { localPosition = new Vector3Int(x, 0, z), blockID = BlockIDs.OakLeaves });
                    leavesBlocks.Add(new StructureBlock { localPosition = new Vector3Int(x, -1, z), blockID = BlockIDs.OakLeaves });
                }
            }

            for (int x = -1; x < 2; x++)
            {
                for (int z = -1; z < 2; z++)
                {
                    leavesBlocks.Add(new StructureBlock { localPosition = new Vector3Int(x, 1, z), blockID = BlockIDs.OakLeaves });
                }
            }

            for (int x = -1; x < 2; x++)
            {
                if (x == 0)
                {
                    for (int z = -1; z < 2; z++)
                    {
                        leavesBlocks.Add(new StructureBlock { localPosition = new Vector3Int(x, 2, z), blockID = BlockIDs.OakLeaves });
                    }
                }
                else
                {
                    leavesBlocks.Add(new StructureBlock { localPosition = new Vector3Int(x, 2, 0), blockID = BlockIDs.OakLeaves });
                }
            }

            leavesPart.blocks = leavesBlocks.ToArray();
            EditorUtility.SetDirty(leavesPart);

            // Populate Trunk Part (single block, meant to be stacked)
            trunkPart.blocks = new[]
            {
                new StructureBlock { localPosition = Vector3Int.zero, blockID = BlockIDs.OakLog },
            };
            EditorUtility.SetDirty(trunkPart);

            // 2. Create Composite Template
            CompositeStructureTemplate treeTemplate = CreateOrGetAsset<CompositeStructureTemplate>("Assets/Data/WorldGen/Structures/Trees/OakTree.asset");
            treeTemplate.pivotOffset = Vector3Int.zero;
            treeTemplate.components = new[]
            {
                new StructureComponent
                {
                    name = "Trunk",
                    partVariants = new[] { trunkPart },
                    type = StructureComponentType.StackedPart,
                    baseOffset = Vector3Int.up, // starts 1 block above root
                    attachToEndOfPreviousStack = false,
                    stackDirection = Vector3Int.up,
                    minRepeat = 4, // MakeTree minHeight=4
                    maxRepeat = 8, // MakeTree maxHeight=8
                    placementChance = 1f,
                },
                new StructureComponent
                {
                    name = "Canopy",
                    partVariants = new[] { leavesPart },
                    type = StructureComponentType.StaticPart,
                    attachToEndOfPreviousStack = true, // attach to top of trunk stack
                    baseOffset = new Vector3Int(0, -2, 0), // matching MakeTree: leaves top relative to trunk top is Y-2
                    placementChance = 1f,
                },
            };
            EditorUtility.SetDirty(treeTemplate);

            AssetDatabase.SaveAssets();
            Debug.Log("Generated Standard Oak Tree Template at Assets/Data/WorldGen/Structures/Trees/");
        }

        private static void GenerateCactus()
        {
            EnsureDirectory("Assets/Data/Structures");
            EnsureDirectory("Assets/Data/WorldGen/Structures/Flora");

            // 1. Create Parts
            StructurePartTemplate cactusPart = CreateOrGetAsset<StructurePartTemplate>("Assets/Data/WorldGen/Structures/Flora/Part_CactusBody.asset");

            // Populate Cactus Part (single block, meant to be stacked)
            cactusPart.blocks = new[]
            {
                new StructureBlock { localPosition = Vector3Int.zero, blockID = BlockIDs.Cactus },
            };
            EditorUtility.SetDirty(cactusPart);

            // 2. Create Composite Template
            CompositeStructureTemplate cactusTemplate = CreateOrGetAsset<CompositeStructureTemplate>("Assets/Data/WorldGen/Structures/Flora/Cactus.asset");
            cactusTemplate.pivotOffset = Vector3Int.zero;
            cactusTemplate.components = new[]
            {
                new StructureComponent
                {
                    name = "Body",
                    partVariants = new[] { cactusPart },
                    type = StructureComponentType.StackedPart,
                    baseOffset = Vector3Int.up, // starts 1 block above root
                    attachToEndOfPreviousStack = false,
                    stackDirection = Vector3Int.up,
                    minRepeat = 1, // MakeCacti minHeight=1
                    maxRepeat = 8, // MakeCacti maxHeight=8
                    placementChance = 1f,
                },
            };
            EditorUtility.SetDirty(cactusTemplate);

            AssetDatabase.SaveAssets();
            Debug.Log("Generated Standard Cactus Template at Assets/Data/WorldGen/Structures/Flora/");
        }

        private static T CreateOrGetAsset<T>(string path) where T : ScriptableObject
        {
            T asset = AssetDatabase.LoadAssetAtPath<T>(path);
            if (asset == null)
            {
                asset = CreateInstance<T>();
                AssetDatabase.CreateAsset(asset, path);
            }

            return asset;
        }

        private static void EnsureDirectory(string path)
        {
            if (!AssetDatabase.IsValidFolder(path))
            {
                string parent = path.Substring(0, path.LastIndexOf('/'));
                string newFolder = path.Substring(path.LastIndexOf('/') + 1);

                if (!AssetDatabase.IsValidFolder(parent))
                {
                    EnsureDirectory(parent);
                }

                AssetDatabase.CreateFolder(parent, newFolder);
            }
        }
    }
}
