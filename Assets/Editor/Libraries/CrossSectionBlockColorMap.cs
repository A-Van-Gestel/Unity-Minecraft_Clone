using System.Collections.Generic;
using System.Reflection;
using Data;
using UnityEngine;

namespace Editor.Libraries
{
    /// <summary>
    /// Static color palette for mapping block IDs to preview colors in the cross-section renderer.
    /// </summary>
    public static class CrossSectionBlockColorMap
    {
        private static readonly Color[] s_blockColors;
#pragma warning disable UDR0001 // Lazily initialized via BuildBlockNameLookup null-check
        private static Dictionary<ushort, string> s_blockNames;
#pragma warning restore UDR0001

        static CrossSectionBlockColorMap()
        {
            s_blockColors = new Color[256];

            // Default: hash-based deterministic color for unknown blocks
            for (int i = 0; i < 256; i++)
            {
                float h = (i * 0.618033988f) % 1f;
                s_blockColors[i] = Color.HSVToRGB(h, 0.5f, 0.7f);
            }

            // Known block colors
            s_blockColors[BlockIDs.Air] = Color.clear;
            s_blockColors[BlockIDs.Stone] = new Color(0.50f, 0.50f, 0.50f);
            s_blockColors[BlockIDs.Grass] = new Color(0.30f, 0.70f, 0.20f);
            s_blockColors[BlockIDs.Dirt] = new Color(0.55f, 0.35f, 0.15f);
            s_blockColors[BlockIDs.Sand] = new Color(0.85f, 0.78f, 0.45f);
            s_blockColors[BlockIDs.Snow] = new Color(0.95f, 0.95f, 0.98f);
            s_blockColors[BlockIDs.GrassSnowy] = new Color(0.80f, 0.90f, 0.80f);
            s_blockColors[BlockIDs.StoneWalkway] = new Color(0.55f, 0.55f, 0.52f);
            s_blockColors[BlockIDs.Bedrock] = new Color(0.20f, 0.20f, 0.20f);
            s_blockColors[BlockIDs.DesertCracked] = new Color(0.78f, 0.65f, 0.38f);
            s_blockColors[BlockIDs.GrassRocky] = new Color(0.45f, 0.55f, 0.35f);
            s_blockColors[BlockIDs.Tile] = new Color(0.70f, 0.65f, 0.60f);
            s_blockColors[BlockIDs.Wood] = new Color(0.60f, 0.40f, 0.20f);
            s_blockColors[BlockIDs.Facade] = new Color(0.75f, 0.72f, 0.68f);
            s_blockColors[BlockIDs.OakLog] = new Color(0.45f, 0.30f, 0.10f);
            s_blockColors[BlockIDs.OakLeaves] = new Color(0.15f, 0.55f, 0.10f);
            s_blockColors[BlockIDs.Cactus] = new Color(0.20f, 0.60f, 0.15f);
            s_blockColors[BlockIDs.StoneHalfSlab] = new Color(0.52f, 0.52f, 0.50f);
            s_blockColors[BlockIDs.DirectionalBlock] = new Color(0.58f, 0.48f, 0.38f);
            s_blockColors[BlockIDs.Water] = new Color(0.20f, 0.40f, 0.85f, 0.80f);
            s_blockColors[BlockIDs.Lava] = new Color(0.95f, 0.45f, 0.10f);
            s_blockColors[BlockIDs.CoalOre] = new Color(0.35f, 0.35f, 0.35f);
            s_blockColors[BlockIDs.GrassBlades] = new Color(0.25f, 0.65f, 0.18f);
        }

        /// <summary>
        /// Returns the preview color for the given block ID.
        /// </summary>
        public static Color GetBlockColor(ushort blockID)
        {
            return blockID < s_blockColors.Length ? s_blockColors[blockID] : s_blockColors[0];
        }

        /// <summary>
        /// Returns the display name for the given block ID, or "Unknown (ID)" for unmapped blocks.
        /// </summary>
        public static string GetBlockName(ushort blockID)
        {
            if (s_blockNames == null) BuildBlockNameLookup();
            return s_blockNames!.TryGetValue(blockID, out string name) ? name : $"Unknown ({blockID})";
        }

        private static void BuildBlockNameLookup()
        {
            // ReSharper disable once ConstantNullCoalescingCondition
            s_blockNames = s_blockNames ?? new Dictionary<ushort, string>();
            foreach (FieldInfo field in typeof(BlockIDs).GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                if (field.FieldType == typeof(ushort))
                    s_blockNames[(ushort)field.GetValue(null)] = field.Name;
            }
        }

        /// <summary>
        /// Returns a vertical sky gradient color for air blocks above terrain.
        /// </summary>
        public static Color GetSkyColor(int y, int maxY)
        {
            float t = (float)y / maxY;
            return Color.Lerp(new Color(0.55f, 0.75f, 0.95f), new Color(0.30f, 0.50f, 0.90f), t);
        }

        /// <summary>
        /// Returns a depth-tinted water color for underwater blocks.
        /// </summary>
        public static Color GetWaterColor(int y, int seaLevel)
        {
            float depth = Mathf.Clamp01((seaLevel - y) / 30f);
            return Color.Lerp(new Color(0.25f, 0.55f, 0.85f), new Color(0.08f, 0.20f, 0.55f), depth);
        }
    }
}
