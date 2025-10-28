/*
using Data;
using UnityEditor;
using UnityEngine;

namespace Editor
{
    [CustomEditor(typeof(World))]
    public class WorldEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            // Draw the default inspector for all other fields
            base.OnInspectorGUI();

            // Get a reference to the World instance being inspected
            World world = (World)target;

            // Check the referenced BlockDatabase asset.
            if (world.blockDatabase == null || world.blockDatabase.blockTypes == null || world.blockDatabase.blockTypes.Length == 0)
                return;

            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Block Tag Preset Tools", EditorStyles.boldLabel);

            // Iterate through the database's array.
            for (int i = 0; i < world.blockDatabase.blockTypes.Length; i++)
            {
                BlockType blockType = world.blockDatabase.blockTypes[i];

                // Check if a preset is assigned for this block type
                if (blockType.tagPreset != null)
                {
                    // Create a button in the inspector, including the block name for clarity
                    if (GUILayout.Button($"Apply Preset to '{blockType.blockName}' (ID: {i})"))
                    {
                        // This allows the action to be undone with Ctrl+Z
                        Undo.RecordObject(world.blockDatabase, $"Apply Preset to {blockType.blockName}");

                        // Core Logic: Copy values from the preset to the block type.
                        blockType.tags = blockType.tagPreset.tags;
                        blockType.canReplaceTags = blockType.tagPreset.canReplaceTags;

                        // Mark the object as "dirty" to ensure the changes are saved.
                        EditorUtility.SetDirty(world.blockDatabase);

                        Debug.Log($"Applied preset '{blockType.tagPreset.name}' to block '{blockType.blockName}'.");
                    }
                }
            }
        }
    }
}
*/
