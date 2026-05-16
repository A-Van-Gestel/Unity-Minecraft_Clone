using System.Collections.Generic;
using Data;
using Data.Structures;
using Editor.Libraries;
using Jobs.BurstData;
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
            EditorUILayoutHelper.SectionHeader("Structure Generators");

            if (GUILayout.Button("Generate Standard Oak Tree"))
            {
                GenerateOakTree();
            }

            if (GUILayout.Button("Generate Standard Cactus"))
            {
                GenerateCactus();
            }

            EditorGUILayout.Space(10);
            EditorUILayoutHelper.SubHeader("Asymmetric Structures (Rotation Test)");

            if (GUILayout.Button("Generate Fallen Oak Log"))
            {
                GenerateFallenOakLog();
            }

            if (GUILayout.Button("Generate Boulder"))
            {
                GenerateBoulder();
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

        private static void GenerateFallenOakLog()
        {
            EnsureDirectory("Assets/Data/WorldGen/Structures/Trees");

            // 1. Create Part — a horizontal trunk extending along +X with leaf decoration at the far end
            StructurePartTemplate fallenTrunkPart = CreateOrGetAsset<StructurePartTemplate>("Assets/Data/WorldGen/Structures/Trees/Part_FallenTrunk.asset");
            fallenTrunkPart.blocks = new[]
            {
                // Horizontal log trunk (4 blocks along +X). meta=AXIS_X so the logs render
                // along their long axis. Y-rotation of the whole part swaps X↔Z as needed.
                new StructureBlock { localPosition = new Vector3Int(0, 0, 0), blockID = BlockIDs.OakLog, meta = BurstVoxelMetadataUtility.AXIS_X },
                new StructureBlock { localPosition = new Vector3Int(1, 0, 0), blockID = BlockIDs.OakLog, meta = BurstVoxelMetadataUtility.AXIS_X },
                new StructureBlock { localPosition = new Vector3Int(2, 0, 0), blockID = BlockIDs.OakLog, meta = BurstVoxelMetadataUtility.AXIS_X },
                new StructureBlock { localPosition = new Vector3Int(3, 0, 0), blockID = BlockIDs.OakLog, meta = BurstVoxelMetadataUtility.AXIS_X },
                // Leaf decoration on far end (asymmetric — only on +X side)
                new StructureBlock { localPosition = new Vector3Int(3, 0, 1), blockID = BlockIDs.OakLeaves, rule = ReplacementRule.OnlyReplaceAir },
                new StructureBlock { localPosition = new Vector3Int(3, 0, -1), blockID = BlockIDs.OakLeaves, rule = ReplacementRule.OnlyReplaceAir },
                new StructureBlock { localPosition = new Vector3Int(3, 1, 0), blockID = BlockIDs.OakLeaves, rule = ReplacementRule.OnlyReplaceAir },
            };
            EditorUtility.SetDirty(fallenTrunkPart);

            // 2. Create Composite Template
            CompositeStructureTemplate fallenLogTemplate = CreateOrGetAsset<CompositeStructureTemplate>("Assets/Data/WorldGen/Structures/Trees/FallenOakLog.asset");
            fallenLogTemplate.pivotOffset = Vector3Int.zero;
            fallenLogTemplate.allowRandomRotation = true; // The whole point — rotation should be clearly visible
            fallenLogTemplate.components = new[]
            {
                new StructureComponent
                {
                    name = "Fallen Trunk",
                    partVariants = new[] { fallenTrunkPart },
                    type = StructureComponentType.StaticPart,
                    baseOffset = new Vector3Int(0, 1, 0),
                    attachToEndOfPreviousStack = false,
                    placementChance = 1f,
                    allowRandomRotation = false, // Global rotation is enough
                },
            };
            EditorUtility.SetDirty(fallenLogTemplate);

            AssetDatabase.SaveAssets();
            Debug.Log("Generated Fallen Oak Log Template at Assets/Data/WorldGen/Structures/Trees/");
        }

        private static void GenerateBoulder()
        {
            EnsureDirectory("Assets/Data/WorldGen/Structures/Rocks");

            // 1. Create Part — an asymmetric L-shaped stone cluster
            StructurePartTemplate boulderPart = CreateOrGetAsset<StructurePartTemplate>("Assets/Data/WorldGen/Structures/Rocks/Part_BoulderSmall.asset");
            boulderPart.blocks = new[]
            {
                // Base layer (L-shape — 3 blocks)
                new StructureBlock { localPosition = new Vector3Int(0, 0, 0), blockID = BlockIDs.Stone },
                new StructureBlock { localPosition = new Vector3Int(1, 0, 0), blockID = BlockIDs.Stone },
                new StructureBlock { localPosition = new Vector3Int(0, 0, 1), blockID = BlockIDs.Stone },
                // Half slab in the L-shaped corner gap
                new StructureBlock { localPosition = new Vector3Int(1, 0, 1), blockID = BlockIDs.StoneHalfSlab },
                // Top layer (only one side — clearly asymmetric)
                new StructureBlock { localPosition = new Vector3Int(0, 1, 0), blockID = BlockIDs.Stone },
                new StructureBlock { localPosition = new Vector3Int(1, 1, 0), blockID = BlockIDs.GrassRocky },
            };
            EditorUtility.SetDirty(boulderPart);

            // 2. Create Composite Template
            CompositeStructureTemplate boulderTemplate = CreateOrGetAsset<CompositeStructureTemplate>("Assets/Data/WorldGen/Structures/Rocks/Boulder.asset");
            boulderTemplate.pivotOffset = Vector3Int.zero;
            boulderTemplate.allowRandomRotation = true;
            boulderTemplate.components = new[]
            {
                new StructureComponent
                {
                    name = "Boulder Body",
                    partVariants = new[] { boulderPart },
                    type = StructureComponentType.StaticPart,
                    baseOffset = new Vector3Int(0, 1, 0),
                    attachToEndOfPreviousStack = false,
                    placementChance = 1f,
                    allowRandomRotation = false,
                },
            };
            EditorUtility.SetDirty(boulderTemplate);

            AssetDatabase.SaveAssets();
            Debug.Log("Generated Boulder Template at Assets/Data/WorldGen/Structures/Rocks/");
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
