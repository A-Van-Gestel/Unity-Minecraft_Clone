WIP Release introducing **Extended Graphics & Display Settings**, a new **Data-Driven Settings UI** attribute system, and a **Fluid Rendering Quality** control.

This release includes the following major new features and improvements:

- **Extended Graphics & Display Settings**: New user-facing settings for Camera FOV, VSync mode, Max FPS cap, Window Mode (Windowed / Borderless / Fullscreen), and Game Resolution — bringing the settings menu to feature parity with standard PC game options.
- **Data-Driven Settings UI Enhancements (Phase 4)**: New attribute-driven features for the auto-generated settings UI:
    - `SubHeaderAttribute` for visual grouping within settings sections ('Chunks', 'Fluids', 'Effects' sub-headers in Rendering).
    - `DisabledWhenAttribute` to conditionally disable settings based on other setting values.
    - `DynamicDropdownAttribute` with `IDropdownProvider` interface to populate dropdowns from runtime data (e.g., available screen resolutions).
- **Fluid Rendering Quality**: New dropdown setting controlling fluid shader complexity, with the fluid distortion effect extracted into a configurable slider that fully disables when set to zero.
- **Settings Robustness**: Fixed new settings values not being initialized when loading older `settings.json` files missing the new fields.
- **Water Material Tuning**: Reduced default refraction strength on the water material and increased the fluid refraction slider maximum to 200 (100 is baseline).
- **Unity Upgrade**: Updated to Unity 6000.4.10f1 (from 6000.4.9f1).

This release also contains the changes & improvements of the previous releases:

- **Multi-Noise Terrain Generation** & **Cave Generation Overhaul**
- **Pause Menu & UI Overhaul** with global Tooltip system
- **Data-Driven Settings UI Architecture** (Phases 1–3)
- **3D Chunk Preview & World Gen Preview Editor Tools**
- **Benchmark System**

## What's Changed

* feat/Modular-World-Generation-&-World-Types by @A-Van-Gestel in https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/pull/6

**Full Changelog**: https://github.com/A-Van-Gestel/Unity-Minecraft_Clone/compare/2026-06-01...2026-06-04
