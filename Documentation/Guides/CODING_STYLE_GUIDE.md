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

Good comments explain the *why*, not the *what*.

### XML Documentation Comments (`///`)

**All public methods, properties, and classes must have XML documentation.** This allows for rich tooltips in the IDE and helps enforce a clear API design.  
**Private methods, properties, and classes do not need XML documentation, but are allowed to have it.** Complex private methods should be documented, but small, easy to follow methods might be better off with a single line summary or no documentation at all.

```csharp
/// <summary>
/// A robust method to get a VoxelState from any local position relative to the current chunk's origin.
/// It correctly handles positions that are outside the chunk's bounds.
/// </summary>
/// <param name="pos">The local position to check (e.g., (-1, 10, 16)).</param>
/// <returns>A VoxelState if the position is in a loaded neighbor chunk, otherwise null.</returns>
private VoxelState? GetVoxelStateFromLocalPos(Vector3Int pos)
{
    // ...
}
```

### Inline Comments (`//`)

Use inline comments to explain complex, non-obvious, or tricky lines of code.

```csharp
// Good: Explains the purpose of the line.
y = 1f - y - VoxelData.NormalizedBlockTextureSize; // To start reading the atlas from the top left

// Bad: Restates the obvious.
// Increment i by 1.
i++;
```

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
