using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace Editor.WorldTools.Libraries
{
    /// <summary>
    /// Shared drawing utilities for cross-section panels used by both the Cross-Section tab
    /// and the Biome Editor inline preview. All methods are static and take explicit parameters
    /// rather than reading instance fields.
    /// </summary>
    public static class CrossSectionPanelHelper
    {
        private static readonly Color s_crosshairColor = new Color(1f, 0.8f, 0.2f, 0.6f);
        private static readonly Color s_seaLevelColor = new Color(0.2f, 0.6f, 1f, 0.5f);

        /// <summary>
        /// Computes the aspect-ratio-preserving draw rect that fits a texture within a panel rect.
        /// The texture is centered within the panel on the shorter axis.
        /// </summary>
        /// <param name="panelRect">The available screen-space rect for the panel.</param>
        /// <param name="tex">The texture whose aspect ratio determines the fitted rect.</param>
        /// <returns>A rect within <paramref name="panelRect"/> that preserves the texture's aspect ratio.</returns>
        public static Rect GetFittedRect(Rect panelRect, Texture2D tex)
        {
            float texAspect = (float)tex.width / tex.height;
            float rectAspect = panelRect.width / panelRect.height;
            if (texAspect > rectAspect)
            {
                float h = panelRect.width / texAspect;
                return new Rect(panelRect.x, panelRect.y + (panelRect.height - h) * 0.5f, panelRect.width, h);
            }
            else
            {
                float w = panelRect.height * texAspect;
                return new Rect(panelRect.x + (panelRect.width - w) * 0.5f, panelRect.y, w, panelRect.height);
            }
        }

        /// <summary>
        /// Draws a texture in a dark-background panel with a title label overlay in the top-left corner.
        /// </summary>
        /// <param name="rect">The screen-space rect for the entire panel (including background).</param>
        /// <param name="tex">The texture to draw. If null, only the background and label are rendered.</param>
        /// <param name="label">The title text displayed in the panel's top-left corner.</param>
        /// <param name="locked">If true, appends "[locked]" to the label text.</param>
        public static void DrawPanelTexture(Rect rect, Texture2D tex, string label, bool locked = false)
        {
            EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f));

            if (tex != null)
            {
                Rect drawRect = GetFittedRect(rect, tex);
                GUI.DrawTexture(drawRect, tex, ScaleMode.StretchToFill);
            }

            GUIStyle labelStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                normal = { textColor = new Color(1f, 1f, 1f, 0.7f) },
                fontSize = 10,
            };
            GUI.Label(new Rect(rect.x + 4, rect.y + 2, 140, 16), locked ? label + " [locked]" : label, labelStyle);
        }

        /// <summary>
        /// Draws crosshair lines (vertical and horizontal) on a panel at the given local texture coordinates.
        /// Lines are drawn in screen space at 1px width with a semi-transparent yellow/orange color.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="tex">The panel's texture (used for coordinate mapping). No-op if null.</param>
        /// <param name="localCol">The horizontal position in texture pixels (0 = left edge).</param>
        /// <param name="localRow">The vertical position in texture pixels (0 = bottom edge, Y-up).</param>
        public static void DrawCrosshairOnPanel(Rect panelRect, Texture2D tex, int localCol, int localRow)
        {
            if (tex == null) return;
            Rect drawRect = GetFittedRect(panelRect, tex);

            float blockW = drawRect.width / tex.width;
            float blockH = drawRect.height / tex.height;

            if (localCol >= 0 && localCol < tex.width)
            {
                float lineX = drawRect.x + (localCol + 0.5f) * blockW;
                EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), s_crosshairColor);
            }

            if (localRow >= 0 && localRow < tex.height)
            {
                float lineY = drawRect.yMax - (localRow + 0.5f) * blockH;
                EditorGUI.DrawRect(new Rect(drawRect.x, lineY, drawRect.width, 1), s_crosshairColor);
            }
        }

        /// <summary>
        /// Draws a horizontal sea level indicator line on a vertical (X-Y or Z-Y) panel.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="tex">The panel's texture (used for Y-axis mapping). No-op if null.</param>
        /// <param name="seaLevel">The Y-level of the sea surface in block coordinates.</param>
        public static void DrawSeaLevelLine(Rect panelRect, Texture2D tex, int seaLevel)
        {
            if (tex == null || seaLevel <= 0 || seaLevel >= tex.height) return;
            Rect drawRect = GetFittedRect(panelRect, tex);
            float blockH = drawRect.height / tex.height;
            float lineY = drawRect.yMax - (seaLevel * blockH);
            EditorGUI.DrawRect(new Rect(drawRect.x, lineY, drawRect.width, 1), s_seaLevelColor);
        }

        /// <summary>
        /// Draws vertical chunk border lines on a vertical (X-Y or Z-Y) panel.
        /// Lines appear at every <see cref="VoxelData.ChunkWidth"/> blocks in world space.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="tex">The panel's texture (used for width mapping). No-op if null.</param>
        /// <param name="worldOffset">The world-space coordinate of the panel's left edge (texture column 0).</param>
        public static void DrawChunkBordersVertical(Rect panelRect, Texture2D tex, int worldOffset)
        {
            if (tex == null) return;
            Rect drawRect = GetFittedRect(panelRect, tex);
            float blockW = drawRect.width / tex.width;

            for (int col = 0; col < tex.width; col++)
            {
                if ((col + worldOffset) % VoxelData.ChunkWidth == 0)
                    EditorGUI.DrawRect(new Rect(drawRect.x + col * blockW, drawRect.y, 1, drawRect.height), Color.cyan);
            }
        }

        /// <summary>
        /// Draws chunk border lines (both horizontal and vertical) on a top-down (X-Z) panel.
        /// Lines appear at every <see cref="VoxelData.ChunkWidth"/> blocks in both X and Z world space.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="tex">The panel's texture (used for dimension mapping). No-op if null.</param>
        /// <param name="offsetX">The world X coordinate of the panel's left edge (texture column 0).</param>
        /// <param name="offsetZ">The world Z coordinate of the panel's top edge (texture row 0).</param>
        public static void DrawChunkBordersTopDown(Rect panelRect, Texture2D tex, int offsetX, int offsetZ)
        {
            if (tex == null) return;
            Rect drawRect = GetFittedRect(panelRect, tex);
            float blockW = drawRect.width / tex.width;
            float blockH = drawRect.height / tex.height;

            for (int col = 0; col < tex.width; col++)
            {
                if ((col + offsetX) % VoxelData.ChunkWidth == 0)
                    EditorGUI.DrawRect(new Rect(drawRect.x + col * blockW, drawRect.y, 1, drawRect.height), Color.cyan);
            }

            for (int row = 0; row < tex.height; row++)
            {
                if ((row + offsetZ) % VoxelData.ChunkWidth == 0)
                    EditorGUI.DrawRect(new Rect(drawRect.x, drawRect.y + row * blockH, drawRect.width, 1), Color.cyan);
            }
        }

        /// <summary>
        /// Handles left-click on a panel to move the crosshair to the clicked position.
        /// Consumes the mouse event if the click lands within the texture's fitted rect.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="tex">The panel's texture (used for coordinate mapping). Returns false if null.</param>
        /// <param name="crosshair">The crosshair position to update (by ref).</param>
        /// <param name="panelType">Determines which crosshair axes are updated:
        /// 0 = X-Y panel (updates <c>crosshair.x</c> and <c>crosshair.y</c>),
        /// 1 = Z-Y panel (updates <c>crosshair.z</c> and <c>crosshair.y</c>),
        /// 2 = X-Z panel (updates <c>crosshair.x</c> and <c>crosshair.z</c>).</param>
        /// <param name="offsetX">World X offset of the panel's left edge.</param>
        /// <param name="offsetZ">World Z offset of the panel's top edge.</param>
        /// <returns>True if the crosshair was updated (click was consumed), false otherwise.</returns>
        public static bool HandlePanelClick(Rect panelRect, Texture2D tex, ref int3 crosshair,
            int panelType, int offsetX, int offsetZ)
        {
            if (tex == null) return false;
            Event e = Event.current;
            if (e.type != EventType.MouseDown || e.button != 0) return false;
            if (!panelRect.Contains(e.mousePosition)) return false;

            Rect drawRect = GetFittedRect(panelRect, tex);
            if (!drawRect.Contains(e.mousePosition)) return false;

            float normX = (e.mousePosition.x - drawRect.x) / drawRect.width;
            float normY = 1f - (e.mousePosition.y - drawRect.y) / drawRect.height;

            int col = (int)(normX * tex.width);
            int row = (int)(normY * tex.height);

            switch (panelType)
            {
                case 0:
                    crosshair.x = col + offsetX;
                    crosshair.y = row;
                    break;
                case 1:
                    crosshair.z = col + offsetZ;
                    crosshair.y = row;
                    break;
                case 2:
                    crosshair.x = col + offsetX;
                    crosshair.z = row + offsetZ;
                    break;
            }

            e.Use();
            return true;
        }

        /// <summary>
        /// Handles mouse scroll wheel on a panel to move the crosshair's depth coordinate.
        /// Scrolling adjusts the axis perpendicular to the panel's visible plane.
        /// Consumes the scroll event if the cursor is within the panel.
        /// </summary>
        /// <param name="panelRect">The screen-space rect of the panel.</param>
        /// <param name="crosshair">The crosshair position to update (by ref).</param>
        /// <param name="panelType">Determines which depth axis is scrolled:
        /// 0 = X-Y panel (scrolls <c>crosshair.z</c>),
        /// 1 = Z-Y panel (scrolls <c>crosshair.x</c>),
        /// 2 = X-Z panel (scrolls <c>crosshair.y</c>, clamped to [0, ChunkHeight-1]).</param>
        /// <returns>True if the crosshair was updated (scroll was consumed), false otherwise.</returns>
        public static bool HandlePanelScroll(Rect panelRect, ref int3 crosshair, int panelType)
        {
            Event e = Event.current;
            if (e.type != EventType.ScrollWheel) return false;
            if (!panelRect.Contains(e.mousePosition)) return false;

            int delta = e.delta.y > 0 ? -1 : 1;

            switch (panelType)
            {
                case 0: crosshair.z += delta; break;
                case 1: crosshair.x += delta; break;
                case 2: crosshair.y = math.clamp(crosshair.y + delta, 0, VoxelData.ChunkHeight - 1); break;
            }

            e.Use();
            return true;
        }

        /// <summary>
        /// Creates a new <see cref="Texture2D"/> or resizes an existing one to the given dimensions.
        /// Uses <see cref="TextureFormat.RGBA32"/> with point filtering and no mipmaps.
        /// </summary>
        /// <param name="tex">The texture reference to create or resize. Destroyed and replaced if dimensions don't match.</param>
        /// <param name="width">The desired texture width in pixels.</param>
        /// <param name="height">The desired texture height in pixels.</param>
        public static void EnsureTexture(ref Texture2D tex, int width, int height)
        {
            if (tex != null && tex.width == width && tex.height == height) return;
            if (tex != null) Object.DestroyImmediate(tex);
            tex = new Texture2D(width, height, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
        }
    }
}
