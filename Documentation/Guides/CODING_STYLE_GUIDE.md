# C# Coding Style Guide

This document defines the coding style and conventions for this project. Following these guidelines is essential for maintaining code that is clean, readable, and easy to maintain.

## 1. Naming Conventions

Consistency in naming is one of the fastest ways to make code understandable.

| Element Type                      | Case             | Prefix | Example                                          |
|-----------------------------------|------------------|--------|--------------------------------------------------|
| Classes, Structs, Enums           | `PascalCase`     | N/A    | `WorldData`, `MeshGenerationJob`                 |
| Public Fields & Properties        | `PascalCase`     | N/A    | `PlayerChunkCoord`, `IsSolid`                    |
| `[SerializeField]` private fields | `camelCase`      | N/A    | `walkSpeed`, `chunkBorderPrefab`                 |
| Private Fields                    | `_camelCase`     | `_`    | `_world`, `_meshFilter`                          |
| `readonly` Private Fields         | `_camelCase`     | `_`    | `private readonly World _world;`                 |
| `static readonly` Fields          | `s_camelCase`    | `s_`   | `private static readonly int[] s_faceChecks;`    |
| Method Names                      | `PascalCase`     | N/A    | `CheckViewDistance()`, `ApplyModifications()`    |
| Local Variables                   | `camelCase`      | N/A    | `int vertexIndex`, `ChunkCoord coord`            |
| Constants (`const`)               | `PascalCase`     | N/A    | `public const int ChunkWidth = 16;`              |
| private Constants (`const`)       | `SCREAMING_CASE` | N/A    | `private const uint SUNLIGHT_MASK = 0x00000F00;` |

### Spelling: American English

**Use American English everywhere** — identifiers, comments, XML docstrings, `[Tooltip]` text, log
messages and player-facing strings alike. `color` not `colour`, `center` not `centre`, `neighbor`
not `neighbour`, `initialize` not `initialise`, `canceled` not `cancelled`, `behavior` not
`behaviour`.

This is not a stylistic preference so much as an interop one: the .NET and Unity APIs this codebase
sits on are American throughout (`Color`, `Vector3.center`, `Initialize`), so British spellings put
a seam through every identifier that touches them — `centreX` beside `bounds.center` reads as two
different concepts.

The one unavoidable exception is a framework name that is itself British: `MonoBehaviour` and
anything deriving its name from it stay as Unity spells them.

## 2. Formatting

### Braces

Use the "Allman" style for braces, where each brace gets its own line. This improves readability.

```csharp
// Good
if (isTransparent)
{
    transparentTriangles.Add(vertexIndex);
}

// Bad
if (isTransparent) {
    transparentTriangles.Add(vertexIndex);
}
```

### Spacing

- Use a single space after a comma between arguments.
- Use a single space around operators (`=`, `+`, `-`, `==`, etc.).
- Do not add a space after a method name and its opening parenthesis.

```csharp
// Good
for (int i = 0; i < VoxelData.ChunkWidth; i++)
{
    totalHeight += GetHeight(i, 1);
}

// Bad
for(int i=0;i<VoxelData.ChunkWidth;i++)
{
    totalHeight+=GetHeight(i,1);
}
```

## 3. Commenting

Comments explain the **why** — the intent, the constraint, the non-obvious reason a line exists. They must not restate the **what** that the code already makes obvious. A comment that only narrates the next statement is noise; delete it.

Comment prose follows the same American-English rule as identifiers (§1).

### XML Documentation Comments (`///`)

**All public methods, properties, and classes must have XML documentation.** This allows for rich tooltips in the IDE and helps enforce a clear API design.  
**Private methods, properties, and classes do not need XML documentation, but are allowed to have it.** Complex private methods should be documented, but small, easy to follow methods might be better off with a single line summary or no documentation at all.

Keep summaries **brief**:

- **Type-level** summaries (`class` / `struct` / `interface`) may run a little longer — they describe a whole unit's role and aren't sitting inline with the code.
- **Member-level** summaries (methods, properties) should stay tight: one line where the member allows it. Lean on `<param>` / `<returns>` for specifics instead of padding the `<summary>`.

```csharp
/// <summary>
/// Gets a VoxelState from any local position relative to the chunk origin, resolving into loaded neighbors when out of bounds.
/// </summary>
/// <param name="pos">The local position to check (e.g., (-1, 10, 16)).</param>
/// <returns>The VoxelState if the position is in a loaded neighbor chunk, otherwise null.</returns>
private VoxelState? GetVoxelStateFromLocalPos(Vector3Int pos)
{
    // ...
}
```

### Inline Comments (`//`)

Use inline comments to explain complex, non-obvious, or tricky lines of code.

Keep them **brief — a single line wherever possible, three lines maximum.** Exceed three lines only when it is genuinely justifiable, e.g. a passage of truly complex logic that cannot be understood without it. When a comment gets that long, treat it as a smell: the code itself may need refactoring (extract a well-named method, simplify the branch). **Flag that possibility to the user rather than silently shipping the wall of text.**

```csharp
// Good: Explains the purpose of the line.
y = 1f - y - VoxelData.NormalizedBlockTextureSize; // To start reading the atlas from the top left

// Bad: Restates the obvious.
// Increment i by 1.
i++;
```

### Describe the current code, not its history

Comments and doc comments document the code **as it stands now** — never how it used to behave.

- When you fix a bug, **update the comment or `<summary>` to describe the corrected behavior.** Do not leave (or add) text describing the old broken behavior, the symptom, or the fix.
- No "war stories" in the source. The narrative of what was wrong and why belongs in the archived bug report at `Documentation/Bugs/_FIXED_BUGS.md`, not in a code comment.

```csharp
// Bad: war story — describes history, not current behavior.
// NOTE: used to read from the wrong neighbor and leak light across the seam;
// fixed 2026-06-21 by clamping to the local section.
skylight = SampleLocalSection(pos);

// Good: describes what the code does now, and why.
skylight = SampleLocalSection(pos); // Clamp to the local section so light never crosses the chunk seam.
```

### Document what a thing is, never who uses it

A docstring describes the type or member it sits on: what it is, what it guarantees, why it is
shaped that way. It must **not** name the code that consumes it — no callers, no consumers, no
"the X screen uses this", no "production passes false and the harness passes true".

A consumer named in a docstring is a fact about the *rest* of the codebase, parked in the one place
nobody updates when that codebase changes. It goes stale the first time a second consumer appears,
and it couples a low-level type's documentation to a high-level one — so reading `ToastVariant`
teaches you about the music player, and deleting the music player leaves a lie behind.

```csharp
// Bad: names a consumer. Stale the moment a second one exists.
/// <summary>Neutral notice. The default, and what the now-playing card uses.</summary>

// Good: says what the value is.
/// <summary>Neutral notice, carrying no accent coloring. The default.</summary>
```

What a consumer roster is usually reaching for is a real property — state that instead:

- roster → **invariant**: "this is the sole arm-selection rule; a second implementation defeats it".
- "caller X does A, caller Y does B" → **obligation**: "a caller that keeps ready/waiting sets must
  implement all three bullets identically".
- a named exception → **shape**, not identity: "a caller that keeps no sets treats Park and Remove
  as no-ops" survives a second such caller; "the startup coroutine does X" does not.

Three things this rule does **not** forbid, because they are not consumers:

- A type documenting **itself** — `NowPlayingToastPresenter` may say it raises now-playing cards.
- A **dependency** the code reads, and the reason it reads it.
- An **owner** in a lifecycle sense — "pooled by `ToastManager`" is structural, not a usage roster.

Narrow exceptions where consumer awareness is the point: a **coverage limit** on a type whose
consumers are unbaselined (keep it a pointer to the design doc, not a caller count), and a
**baseline/test docstring** stating what the test does not cover.

## 4. Attributes

### `[SerializeField]`

Use `[SerializeField]` on private fields to expose them to the Unity Inspector. Avoid using `public` fields for this purpose unless the field truly needs to be publicly accessible from other scripts.

### `[Tooltip]`

**Always** add a `[Tooltip]` attribute to every `[SerializeField]` field. This makes the Inspector much more user-friendly and serves as inline documentation.

```csharp
// Good
[Tooltip("The maximum number of lighting jobs that can be scheduled in a single frame.")]
[SerializeField]
private int maxLightJobsPerFrame = 8;

// Bad
[SerializeField]
private int maxLightJobsPerFrame;
```

## 5. General Principles & Best Practices

- **Cache Component References:** In `MonoBehaviour` scripts, get references to components in `Awake()` or `Start()` and store them in private fields. Do not repeatedly call `GetComponent()` in `Update()`.

- **Use `readonly` Where Possible:** Mark any field that is only assigned in a constructor or at declaration as `readonly`. This communicates immutability and prevents accidental changes.

- **Separate Data from Logic:** Follow the pattern of `Chunk` (logic) vs. `ChunkData` (data). This makes data serialization easier and code more modular.

- **Use Regions for Organization:** Use `#region` and `#endregion` to group related methods and properties within a class. Standard regions used in this project include:
    - `Constructors`
    - `Public Methods`
    - `Private Methods`
    - `Helper Methods`
    - `Overrides`

```csharp
public class MyClass
{
    #region Public Properties

    public int MyProperty { get; private set; }

    #endregion

    #region Constructors

    public MyClass()
    {
        // ...
    }

    #endregion

    #region Public Methods

    public void DoSomething()
    {
        // ...
    }

    #endregion
}
```
