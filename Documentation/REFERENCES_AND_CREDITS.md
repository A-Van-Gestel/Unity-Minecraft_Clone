# Project References & Credits

This document tracks all third-party assets, libraries, algorithms, and resources used in the development of this project.

## 🛠️ Libraries & Algorithms

| Name                        | Author                                                                                               | License                                | Usage Details                                                                                                                                                 |
|:----------------------------|:-----------------------------------------------------------------------------------------------------|:---------------------------------------|:--------------------------------------------------------------------------------------------------------------------------------------------------------------|
| **NativeCompressions**      | [Cysharp](https://github.com/Cysharp/NativeCompressions)                                             | MIT                                    | High-performance native LZ4 bindings for Chunk Serialization.                                                                                                 |
| **Starlight / ScalableLux** | [RelativityMC](https://github.com/RelativityMC/ScalableLux)                                          | GNU Lesser General Public License v3.0 | Reference implementation for the BFS flood-fill lighting propagation and optimization details.                                                                |
| **FastNoiseLite**           | [Auburn](https://github.com/Auburn/FastNoiseLite) (Burst port by Project Developer)                  | MIT                                    | Burst-compatible port of the v1.1 C# version used for high-performance `Standard` terrain generation. Located at `Assets/Scripts/Libraries/FastNoiseLite.cs`. |
| **Perlin Noise**            | Unity Technologies                                                                                   | Proprietary                            | Used via `Mathf.PerlinNoise` for `Legacy` world terrain generation.                                                                                           |
| **Spiral Loop**             | [Unity Discussions](https://discussions.unity.com/t/how-to-generate-a-grid-from-the-center/171186/2) | N/A                                    | Math logic for chunk loading iteration in a Spiral Loop.                                                                                                      |
| **FPSCounter**              | [ManlyMarco](https://github.com/ManlyMarco/FPSCounter)                                               | Apache License 2.0                     | Implementation reference for the `PerformanceMonitor` dual-hook `Stopwatch` architecture and per-phase CPU timing methodology.                                |

## 🎨 Graphics & Textures

### Terrain Textures

*All textures below have been packed into `Textures/Packed_Atlas.png`.*

* **50 free textures 5 (with Normalmaps)** by [rubberduck](https://opengameart.org/content/50-free-textures-5-with-normalmaps)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....
* **High-res texture pack 1** by [rubberduck](https://opengameart.org/content/high-res-texture-pack-1)
    * *License:* CC0 (Public Domain)
    * *Files used:* ...
* **60 CC0 Vegetation textures** by [rubberduck](https://opengameart.org/content/60-cc0-vegetation-textures)
    * *License:* CC0 (Public Domain)
    * *Files used:* Oak Leaves
* **Terrain textures pack (from Stunt Rally 2.3)** by [CryHam](https://opengameart.org/content/terrain-textures-pack-from-stunt-rally-23)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....
* **Tree Bark** by [qubodup](https://opengameart.org/node/8005)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....
* **Seamless, tiling tree bark texture** by [Bart K.](https://opengameart.org/node/7789)
    * *License:* GPL 2.0, GPL 3.0, CC-BY-SA 3.0
    * *Files used:* ....
* **paramecij's tree trunks and stumps texture pack 1** by [pare](https://opengameart.org/content/paramecijs-tree-trunks-and-stumps-texture-pack-1)
    * *License:* CC0 (Public Domain)
    * *Files used:* ....

### UI Elements

* **Block Icons**
    * *Source:* Custom made / Originally generated using [Minecraft Blocks Render](https://github.com/TABmk/minecraft-blocks-render) by [TABmk](https://github.com/TABmk).
    * *Notes:* Now also used as an implementation reference for the project's own in-editor block icon renderer.
* **Crosshair**
    * *Source:* Custom made.
* **Hotbar**
    * *Source:* Ripped from Minecraft.

## ✒️ Fonts

* [**Monocraft**](https://github.com/IdreesInc/Monocraft) by [IdreesInc](https://github.com/IdreesInc)
    * *Version:* v4.2.1
    * *License:* SIL Open Font License 1.1
    * *Source:* `Assets/Fonts/Monocraft/Monocraft.ttc`
    * *Notes:* A monospaced font inspired by the Minecraft typeface.

* [**FireCuda**](https://github.com/tonsky/FiraCode) by [tonsky](https://github.com/tonsky)
    * *Version:* v6.2
    * *License:* SIL Open Font License 1.1
    * *Source:* `Assets/Fonts/FireCuda/FireCuda.ttf`
    * *Notes:* Free monospaced font with programming ligatures. Used as fallback for Monocraft for "Box Drawing" characters.

## 📄 Shaders & Technical Art

* **MaskedUIBlur** based on logic by [cician](https://forum.unity3d.com/threads/simple-optimized-blur-shader.185327/#post-1267642)
    * *Notes:* Optimized grab-pass blur for inventory backgrounds.
