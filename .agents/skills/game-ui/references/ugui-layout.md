# uGUI layout craft

General uGUI layout rules the `game-ui` skill relies on. Project-specific contracts (bands, blur,
`RectMask2D`, factory) live in `SKILL.md` and win over anything here.

Adapted from Unity's `ui-ugui` skill (`Unity-Technologies/unity-agent-plugin` @ `566368b`, Unity
Companion License); the scroll-view hierarchy is corrected for this project's band pass.

## Who controls a child's size

Settle this before setting any value. When a parent has a Layout Group, the parent owns its children's
position and, with **Control Child Size** on, their size. Values set on the child are overwritten.

- **Control Child Size** — the parent sets child dimensions from their preferred/flexible sizes
  (`LayoutElement` supplies those). Off ⇒ children need explicit sizes.
- **Child Force Expand** — children stretch to fill the parent's free space.
- When a layout is wrong, check the **parent's** settings before touching the child.

**Conflicts that fail silently (jitter, ignored sizes, flicker):**

- `ContentSizeFitter` on a **child** whose parent has Control Child Size on — the fitter is overridden.
  Turn Control Child Size off on the parent for that axis instead.
- A Layout Group on an element that should keep a fixed size.

Not a conflict: a fitter **beside** a Layout Group on the same object. That is the standard way a
container sizes itself to its content (the scroll-view Content below uses it).
- Nested Layout Groups where an inner level has no preferred size to report.

**Grid Layout Group** sizes every child to **Cell Size**, which must be set explicitly; **Constraint**
fixes the row or column count.

**ContentSizeFitter** with Preferred Size needs something that reports a preferred size (a Layout
Group, TMP text, or a `LayoutElement`) — otherwise it sizes to zero.

## Anchoring from a described position

For "top right", "bottom bar", "left panel":

1. Classify it: **corner** (fixed point, fixed size), **edge** (stretches along one axis), or **fill**
   (stretches both).
2. Set `anchorMin` / `anchorMax`: equal for a corner, spanning the axis for an edge or fill.
3. **Set the pivot to the anchor point** — a top-right element pivots at `(1, 1)`, a top bar at
   `(0.5, 1)`. Leaving the default `(0.5, 0.5)` offsets every later value.
4. Only then set `anchoredPosition` / `sizeDelta` (or the offsets for stretched axes).
5. Confirm the resulting `rect` size is non-zero.

Prefer anchors and Layout Groups over absolute positions; the UI must hold across the UI-scale setting.

## Scroll views

```
ScrollView (ScrollRect + Image, Raycast Target on)
├── Viewport (RectTransform + RectMask2D)      ← RectMask2D, never Mask + Image (SKILL.md Step 4)
│   └── Content (RectTransform + VerticalLayoutGroup + ContentSizeFitter [Vertical: Preferred])
│       └── items
└── Scrollbar (optional)
```

- Assign `ScrollRect.content` and `ScrollRect.viewport`; the scrollbar is optional.
- The root `Image` is the raycast surface. Without it, wheel and drag only work over items that
  raycast, so gaps, padding and a near-empty list do not scroll. It may be fully transparent.
- The Content's fitter is what makes the scroll area grow with its children; without it nothing scrolls.
- Runtime-populated lists: **clear the existing children before populating**, or every open duplicates
  them.
- For long read-only text, `RuntimeUIFactory.CreateScrollableTextArea` already builds this.

## Visibility checklist

An element is invisible unless all hold:

- width and height > 0, and it lies inside its parent's rect
- not covered by a later sibling (later siblings draw on top within a canvas)
- its `Image` has a sprite, or a color with alpha > 0
- it and every parent are active; no `CanvasGroup.alpha` of 0 above it

Elements created hidden on purpose (toggled later) are fine; say so.

## Interaction readiness

Interactive UI that fails these still draws, then ignores input:

1. Exactly one `EventSystem` in the scene.
2. A `GraphicRaycaster` on the canvas that owns the element (in this project, on each interactive
   nested band root — `SKILL.md` Step 3).
3. `Raycast Target` on for interactive graphics, **off** for decorative images and text above them.
4. Callbacks wired (inspector or code) — only when logic was requested.

## Component quick reference

| Need | Component |
|---|---|
| Background, icon, blur panel | `UnityEngine.UI.Image` |
| Render texture / video | `UnityEngine.UI.RawImage` |
| Any text | `TextMeshProUGUI` (never legacy `Text`) |
| Click | `UnityEngine.UI.Button` — reuse `Assets/Prefabs/UI/Components/Button.prefab` |
| Choice list | `TMP_Dropdown` — reuse `Dropdown.prefab` (it carries the band fix) |
| On/off, value range, text entry | `Toggle`, `Slider`, `TMP_InputField` — reuse the component prefabs |
| Clipping | `RectMask2D` |
