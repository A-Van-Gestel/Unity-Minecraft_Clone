WIP Release introducing a fully data-driven **Multi-Noise Terrain Generation** system, a comprehensive **Cave Generation Overhaul**, a new **Pause Menu** with full UI scaling, a **Data-Driven Settings UI** architecture, and a **Benchmark System**.

This release includes the following major new features and improvements:

- **Multi-Noise Terrain Generation**: Complete overhaul of world generation to use a multi-noise pipeline replacing the legacy single-noise system:
    - New biome blending, per-layer strata, and biome surface dithering driven by FastNoiseLite.
    - Ocean biome (initial version) and a new "Steep Grasslands" variant split from the base Grasslands biome.
    - FastNoiseLite batch evaluation API (`GetNoiseGrid`, `GetNoiseBatch`) with 20–25% performance gain via `FloatMode.Fast`.
    - `BiomeConfigValidator` to catch common biome config mistakes at edit time.
    - Terrain gen debug overlay (F8) with biome preview colors and blending seam fixes.
- **Cave Generation Overhaul** (Phases 1–5): Major rework of the worm carver and noise cave systems:
    - Per-layer zone attenuation, trunk worm layer, worm Y-level attraction, worm-to-worm mask seeking, and ellipsoidal carving.
    - Spaghetti revival using 3D noise; worm radius noise using FastNoiseLite.
    - Surface-relative cave suppression to prevent carving into the surface.
    - Cave isolation filter removing small (1–12 block) air pockets from noise methods.
    - `CaveDensityAnalyzer` editor tool for automated cave generation reports.
- **Pause Menu & UI Overhaul**: New in-world Pause Menu with proper UI scaling support:
    - Pause Menu with `WorldUIManager`, keybinds help screen, UIBlur background, and read-only settings visual distinction.
    - Global Tooltip system with per-slot block info tooltips in the Toolbar, Creative Inventory, and Item Slots.
    - Credits & Licenses page in the Main Menu with a dedicated Credits Editor tool.
- **Data-Driven Settings UI Architecture**: Replaced hand-authored settings UI with a fully generated system (Phases 1–3), including a hidden Dev tab for editor/debug builds and a configurable `SettingsManager`.
- **3D Chunk Preview & World Gen Preview Editor Tools**: New visual editor tools for biome and world generation tuning:
    - Full 4-panel cross-section layout; biome editor tab with inline 3-panel preview; X/Y/Z clip planes, sea-level override, and debounce timer.
    - World Gen Preview window (renamed from Noise Preview) with WorldType Definition CRUD, seed sync, and biome sync.
    - Generation Feature Flags for major/minor flora in both editor tools and the in-game settings menu.
- **Benchmark System**: Initial benchmark system with configurable settings, runtime HUD, results screen, and report generation.
- **Performance**: Lighting scan optimized from O(N) to O(dirty) via an event-driven work queue with thread-safe `ConcurrentQueue` staging; GC allocations eliminated in `World.Tick` coroutine.
- **Bug Fixes**: Blocks replacing liquids incorrectly; `RaycastForVoxel` not honoring `CanReplace` tags (preventing placement under water); biome blending seam due to non-continuous cellular noise; various UI and editor tool fixes.
- **Unity Upgrade**: Updated to Unity 6000.4.9f1 (from 6000.4.5f1).

This release also contains the changes & improvements of the previous releases:

- **Per-Block Metadata Schemas** & **Sub-Voxel Collision System**
- **Fallen Oak Trees** & **World Save V7 Migration**
- **Minor Flora Interactability** & **Structure Preview Tool Overhaul**
- **Per-Entry Flora Zones** & **Custom [Data.BlockID] Inspector Drawer**
- **Data-Driven Structure Pools** & **Worm Carver Caves**
- High-Performance **CrossMesh Flora Rendering**
- Subsurface block depth randomness & biome surface block dithering
- WIP **_FastNoiseLite_** library based World Generation system overhaul
- Migration to **_Universal Rendering Pipeline (URP)_** and **_Linear color space_**
- Performance monitor overhaul & Debug screen improvements
- Experimental **_DirectX 12_** & **_Vulkan_** graphics API support

## What's Changed

* feat/Modular-World-Generation-&-World-Types by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/6

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-05-03...2026-06-01
