# Project References & Credits

This document tracks all third-party assets, libraries, algorithms, and resources used in the development of this project.

> [!NOTE]
> This file is a human-readable **mirror**. The in-game credits screen is driven by
> `Assets/Resources/CreditsDatabase.asset` (edited via **Minecraft Clone → Credits Editor**), which
> is the authoritative copy — add an entry there first, then reflect it here. Sections below map to
> `CreditCategory` values.

## 🛠️ Libraries & Algorithms

| Name                        | Author                                                                                               | License                                | Usage Details                                                                                                                                                 |
|:----------------------------|:-----------------------------------------------------------------------------------------------------|:---------------------------------------|:--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **NativeCompressions**      | [Cysharp](https://github.com/Cysharp/NativeCompressions)                                             | MIT                                    | High-performance native LZ4 bindings for Chunk Serialization.                                                                                                 |
| **Starlight / ScalableLux** | [RelativityMC](https://github.com/RelativityMC/ScalableLux)                                          | GNU Lesser General Public License v3.0 | Studied as a source of lighting optimization techniques — its `TECHNICAL_DETAILS.md` and the implementation behind it — after our own BFS flood-fill engine was already working. The edge consistency check and `max(1, opacity)` attenuation formula came from it. |
| **FastNoiseLite**           | [Auburn](https://github.com/Auburn/FastNoiseLite) (Burst port by Project Developer)                  | MIT                                    | Burst-compatible port of the v1.1 C# version used for high-performance `Standard` terrain generation. Located at `Assets/Scripts/Libraries/FastNoiseLite.cs`. |
| **Perlin Noise**            | Unity Technologies                                                                                   | Proprietary                            | Used via `Mathf.PerlinNoise` for `Legacy` world terrain generation.                                                                                           |
| **Spiral Loop**             | [Unity Discussions](https://discussions.unity.com/t/how-to-generate-a-grid-from-the-center/171186/2) | N/A                                    | Math logic for chunk loading iteration in a Spiral Loop.                                                                                                      |
| **FPSCounter**              | [ManlyMarco](https://github.com/ManlyMarco/FPSCounter)                                               | Apache License 2.0                     | Implementation reference for the `PerformanceMonitor` dual-hook `Stopwatch` architecture and per-phase CPU timing methodology.                                |

## 🎨 Graphics & Textures

### Terrain Textures

*All textures below are sourced from `Assets/Editor/AtlasPacker/SourceTextures/` and packed into
`Assets/Textures/packed_texture_atlas.png`.*

* **50 free textures 5 (with Normalmaps)** by [rubberduck](https://opengameart.org/content/50-free-textures-5-with-normalmaps)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....
* **High-res texture pack 1** by [rubberduck](https://opengameart.org/content/high-res-texture-pack-1)
    * *License:* CC0 (Public Domain)
    * *Files used:* ...
* **60 CC0 Vegetation textures** by [rubberduck](https://opengameart.org/content/60-cc0-vegetation-textures)
    * *License:* CC0 (Public Domain)
    * *Files used:* `016-oak_leaves.png`
* **Terrain textures pack (from Stunt Rally 2.3)** by [CryHam](https://opengameart.org/content/terrain-textures-pack-from-stunt-rally-23)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....
* **Seamless, tiling tree bark texture** by [Bart K.](https://opengameart.org/node/7789)
    * *License:* **CC-BY-SA 3.0** — the author offers GPL 2.0, GPL 3.0 or CC-BY-SA 3.0 (and later
      versions of those); CC-BY-SA 3.0 is the one selected for this project, being the content
      licence of the three, so its share-alike attaches to the artwork and the packed atlas rather
      than raising scope questions over the engine code.
    * *Files used:* `Assets/Editor/AtlasPacker/SourceTextures/014-oak_log_side.png` (oak log side),
      packed into `Assets/Textures/packed_texture_atlas.png`.
* **paramecij's tree trunks and stumps texture pack 1** by [pare](https://opengameart.org/content/paramecijs-tree-trunks-and-stumps-texture-pack-1)
    * *License:* CC0 (Public Domain)
    * *Files used:* `stump-end/wood_end_02.png` (from `para - CC0_tex-pack-tree`) → `015-oak_log_top.png`
* **Grass 2** by [virtushda](https://opengameart.org/content/grass-2-0)
    * *License:* CC0 (Public Domain)
    * *Files used:* `255-grass_blades.png`

### UI Elements

* **Block Icons**
    * *Source:* Custom made / Originally generated using [Minecraft Blocks Render](https://github.com/TABmk/minecraft-blocks-render) by [TABmk](https://github.com/TABmk).
    * *Notes:* Now also used as an implementation reference for the project's own in-editor block icon renderer.
* **Crosshair**
    * *Source:* Custom made.
* **Hotbar, UI Slot's, Buttons & Dirt Main Menu background**
    * *Source:* Ripped from Minecraft.

## 🔊 Audio

*Clips are referenced by `Assets/Resources/Data/BlockSoundDatabase.asset`, which isolates content from
architecture — packs can be swapped without code changes. **Licensing on the sourcing sites is per-asset,
not per-site:** verify and record the licence of every individual download here, with author, source URL
and licence, before it ships. See `Documentation/Design/SOUND_ENGINE_DESIGN.md` §9 for the sourcing policy,
including the three-step permission rule for "free to download but no attached licence" packs.*

*No audio content has been imported yet — the engine ships with an empty sound database.*

## ✒️ Fonts

* [**Monocraft**](https://github.com/IdreesInc/Monocraft) by [IdreesInc](https://github.com/IdreesInc)
    * *Version:* v4.2.1
    * *License:* SIL Open Font License 1.1
    * *Source:* `Assets/Fonts/Monocraft/Monocraft.ttc`
    * *Notes:* A monospaced font inspired by the Minecraft typeface.

* [**Fira Code**](https://github.com/tonsky/FiraCode) by [tonsky](https://github.com/tonsky)
    * *Version:* v6.2
    * *License:* SIL Open Font License 1.1
    * *Source:* `Assets/Fonts/FireCode/` (Light, Regular, Medium, SemiBold and Bold weights)
    * *Notes:* Free monospaced font with programming ligatures. Used as fallback for Monocraft for "Box Drawing" characters.

## 📄 Shaders & Technical Art

* **MaskedUIBlur** based on logic by [cician](https://forum.unity3d.com/threads/simple-optimized-blur-shader.185327/#post-1267642)
    * *Notes:* Optimized grab-pass blur for inventory backgrounds.

## 📚 References & Further Reading

*Published techniques this project implements. No code was taken from these sources — they are
credited for the ideas, and each entry records where our implementation deliberately differs.*

* [**Domain Warping**](https://iquilezles.org/articles/warp/) by Iñigo Quílez
    * *Published:* 2002
    * *License:* N/A — technique only, no code used
    * *Used for:* Distorting the input coordinates of the 3D density and cave noises
      (`p' = p + Warp(p)`), breaking up grid-aligned noise structure and producing organic,
      geologically folded terrain.
    * *We differ:* a single `DomainWarp()` call per warp source, driven by its own dedicated
      noise instance — not the article's recursive `fbm(p + fbm(p + fbm(p)))`.
    * *See:* [Architecture/World Generation/PROCEDURAL_TERRAIN_GENERATION.md](Architecture/World%20Generation/PROCEDURAL_TERRAIN_GENERATION.md) §2.4, §7.1

* [**GPU Gems 3, Chapter 1: Generating Complex Procedural Terrains Using the GPU**](https://developer.nvidia.com/gpugems/gpugems3/part-i-geometry/chapter-1-generating-complex-procedural-terrains-using-gpu) by Ryan Geiss (NVIDIA)
    * *License:* N/A — technique only, no code used
    * *Used for:* The 3D density-function terrain model — positive density is solid, negative is
      air, and the zero-crossing is the surface. This replaced the 2D heightmap and is what makes
      overhangs, arches and caves possible.
    * *We differ:* evaluated in Burst on CPU worker threads rather than on the GPU (the voxel grid,
      not an isosurface, is the authoritative game state), and restricted to the Dynamic Density
      Band instead of the full volume.
    * *See:* [Architecture/World Generation/PROCEDURAL_TERRAIN_GENERATION.md](Architecture/World%20Generation/PROCEDURAL_TERRAIN_GENERATION.md) §2.3, §4, §7.2
