# Project References & Credits

This document tracks all third-party assets, libraries, algorithms, and resources used in the development of this project.

## 🛠️ Libraries & Algorithms

| Name                        | Author                                                                                               | License                                | Usage Details                                                                                  |
|:----------------------------|:-----------------------------------------------------------------------------------------------------|:---------------------------------------|:-----------------------------------------------------------------------------------------------|
| **NativeCompressions**      | [Cysharp](https://github.com/Cysharp/NativeCompressions)                                             | MIT                                    | High-performance native LZ4 bindings for Chunk Serialization.                                  |
| **Starlight / ScalableLux** | [RelativityMC](https://github.com/RelativityMC/ScalableLux)                                          | GNU Lesser General Public License v3.0 | Reference implementation for the BFS flood-fill lighting propagation and optimization details. |
| **Perlin Noise**            | Unity Technologies                                                                                   | Proprietary                            | Used via `Mathf.PerlinNoise` for terrain generation (pending replacement with FastNoiseLite).  |
| **Spiral Loop**             | [Unity Discussions](https://discussions.unity.com/t/how-to-generate-a-grid-from-the-center/171186/2) | N/A                                    | Math logic for chunk loading iteration in a Spiral Loop.                                       |

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
    * *Source:* Custom made / Generated using [Minecraft Blocks Render](https://github.com/TABmk/minecraft-blocks-render) by [TABmk](https://github.com/TABmk).
* **Crosshair**
    * *Source:* Custom made.
* **Hotbar**
    * *Source:* Ripped from Minecraft.

## ✒️ Fonts

* **Monocraft** by [IdreesInc](https://github.com/IdreesInc/Monocraft)
    * *License:* SIL Open Font License 1.1
    * *Source:* `Assets/Fonts/Monocraft.ttf`
    * *Notes:* A monospaced font inspired by the Minecraft typeface.

## 📄 Shaders & Technical Art

* **MaskedUIBlur** based on logic by [cician](https://forum.unity3d.com/threads/simple-optimized-blur-shader.185327/#post-1267642)
    * *Notes:* Optimized grab-pass blur for inventory backgrounds.
