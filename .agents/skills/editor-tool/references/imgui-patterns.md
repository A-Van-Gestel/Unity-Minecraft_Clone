# IMGUI Widget Patterns

Companion reference for the `editor-tool` skill: recurring IMGUI implementation patterns
(styling, texture display, collapsible panels, tooltips).

## GUIStyle Caching

Cache as static fields with lazy initialization. Suppress `UDR0001` since styles are recreated on null-check after domain reload:

```csharp
#pragma warning disable UDR0001
private static GUIStyle s_myStyle;
#pragma warning restore UDR0001

private static void EnsureStyles()
{
    if (s_myStyle != null) return;
    s_myStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 13, fixedHeight = 20 };
}
```

**`fixedHeight` rule:** When overriding `fontSize` on a GUIStyle derived from `EditorStyles`, always set `fixedHeight` to prevent IMGUI from clipping descenders (g, y, p, q) based on the base style's smaller font metrics.

## Responsive Texture Display

For preview textures that should fill available space:

```csharp
Rect rect = GUILayoutUtility.GetRect(0, 0, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
if (rect.width > 10 && rect.height > 10)
{
    Rect drawRect = CrossSectionPanelHelper.GetFittedRect(rect, texture);
    GUI.DrawTexture(drawRect, texture, ScaleMode.StretchToFill);
    
    // Draw overlays in SCREEN SPACE after the texture (not baked into pixels)
    EditorGUI.DrawRect(new Rect(lineX, drawRect.y, 1, drawRect.height), Color.cyan);
}
```

**Rule:** Never bake overlay lines (chunk borders, sea level, crosshairs) into the texture pixels. Draw them as screen-space `EditorGUI.DrawRect` calls after the texture — this keeps them 1px regardless of zoom and avoids regeneration on toggle.

## Collapsible Panels

For optional preview panels or advanced settings:

```csharp
string label = _showPanel ? "Section ▼" : "Section ▲";
if (GUILayout.Button(label, EditorStyles.toolbarButton))
    _showPanel = !_showPanel;

if (_showPanel)
{
    // Panel content
}
```

Pair with `GUILayout.MaxHeight(position.height * 0.55f)` on the content above to prevent the panel from being squeezed out.

## Tooltips

All user-facing controls MUST have tooltips via `GUIContent`:

```csharp
_value = EditorGUILayout.IntSlider(
    new GUIContent("Field Name", "Description of what this field does and valid ranges."),
    _value, min, max);

_toggle = GUILayout.Toggle(_toggle,
    new GUIContent("Label", "What enabling/disabling this does."),
    EditorStyles.miniButton);
```

For `EditorGUI.EnumPopup` with absolute `Rect` positioning, tooltips don't work on the popup itself — use a separate `GUI.Label` with the tooltip next to it.
