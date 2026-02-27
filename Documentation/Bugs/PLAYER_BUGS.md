# Known Player related bugs

This document outlines known bugs and major improvements related to the player controller and interaction systems.


## 01. Collision only checks at two height levels (feet and +1)

**Severity:** Bug  
**Files:** `Player.cs` — `Front`, `Back`, `Left`, `Right` properties (lines 335–353)

The horizontal collision properties (`Front`, `Back`, `Left`, `Right`) only check at two Y levels: the player's feet (`position.y`) and one block above (`position.y + 1f`). Since the player height is configurable (`playerHeight = 1.8f`), any height value greater than `2.0f` would leave the upper portion of the player unchecked, allowing them to walk into blocks at head/shoulder level.

Even at the default height, the `+1f` offset is hardcoded rather than being derived from `playerHeight`, creating a subtle coupling with the default configuration.


## 02. Collision checks don't account for player width in cross-axis

**Severity:** Bug  
**Files:** `Player.cs` — `Front`, `Back`, `Left`, `Right` properties (lines 335–353)

Each directional collision property only checks a single line along its axis (e.g., `Front` checks `z + playerWidth` but at the player's exact `x` coordinate). This means corners are not checked for horizontal movement — a player moving diagonally can clip into a block corner that neither the X-axis nor Z-axis check will detect individually.

In contrast, the vertical checks (`CheckDownSpeed`, `CheckUpSpeed`) do correctly check all 4 corner positions.


## 03. Player can fall through the world during loading

**Severity:** Bug  
**Files:** `Player.cs` — `FixedUpdate` (line 99), `World.cs` — `StartWorld` (line 409 TODO)

During the `StartWorld` coroutine, there is a multi-frame gap between when the player is positioned in the world and when chunks are fully loaded and meshed. During this time, `FixedUpdate` runs gravity and movement, potentially causing the player to fall through unloaded terrain. The code already has a TODO noting this issue:

```
// TODO: Prevent player position updates while the world is loading
// (eg: player falling through world because chunks aren't loaded yet)
```


## 04. `GameObject.Find("Main Camera")` is fragile

**Severity:** Improvement  
**Files:** `Player.cs` — `Start` (line 84) and `LoadSaveData` (line 400), `PlayerInteraction.cs` — `Awake` (line 35)

The camera reference is obtained using `GameObject.Find("Main Camera")`, which relies on a hardcoded string name. If the camera GameObject is renamed, the player controller silently breaks. `Camera.main` (which is already used elsewhere in the codebase, e.g., `PlayerInteraction.cs`) or a serialized inspector reference would be more robust.

Additionally, `PlayerInteraction.Awake` uses `Camera.main.transform` without null-checking.


## 05. `Application.Quit()` on Escape is not guarded

**Severity:** Improvement  
**Files:** `Player.cs` — `GetPlayerInputs` (line 227)

Pressing Escape immediately calls `Application.Quit()` with no confirmation dialog. In the Unity Editor, this call is ignored (it only works in builds), which means the behavior is invisible during development but could be surprising in a build. A confirmation step or pause menu would be a safer UX pattern.


## 06. Mouse input uses `Time.timeScale` instead of `Time.deltaTime`

**Severity:** Bug  
**Files:** `Player.cs` — `Update` (lines 123, 126)

Camera rotation uses `_mouseHorizontal * Time.timeScale` instead of `* Time.deltaTime`. This means:
- At `timeScale = 1`, the rotation speed depends entirely on frame rate (faster FPS = faster rotation).
- If `timeScale` is set to 0 (e.g., for a pause menu), the rotation stops, but this is coincidental rather than correct.

The mouse input values from `Input.GetAxis("Mouse X/Y")` are already frame-rate-dependent deltas, so multiplying by `Time.timeScale` does not correctly provide frame-rate-independent behavior. Typically, raw mouse input should only be scaled by sensitivity, not by time.


## 07. Raycast-based block placement can be incorrect on exact voxel edges

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `RaycastForVoxel` (lines 107–145)

The block placement algorithm uses modulus-based proximity checks (`pos.x % 1`) to determine which face was hit. The `% 1` operation on negative floating-point values produces negative results (e.g., `-0.1f % 1 = -0.1f`), which can cause incorrect face detection for blocks at negative coordinates (near X=0 or Z=0 world edges). While the world origin is positive (center is at `WorldSizeInVoxels / 2 = 800`), this is a latent bug if the world origin ever changes.

Additionally, the placement logic has an implicit priority: if `xCheck`, `yCheck`, and `zCheck` are equally close to a face (exact corner or edge hit), the Y-axis is always chosen as the tiebreaker fallback, which may not always be the player's intent.


## 08. Block placement overlap check only covers 2 voxels of player height

**Severity:** Bug  
**Files:** `PlayerInteraction.cs` — `PlaceCursorBlocks` (lines 168–172)

The placement validity check only prevents placing blocks at the player's feet coordinate and `+ Vector3Int.up` (head). If `playerHeight` is configured to be taller than 2 blocks, the player could place blocks inside their own body above the second voxel.
