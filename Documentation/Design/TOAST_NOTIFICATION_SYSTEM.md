# Toast Notification System Design

**Version:** 1.4  
**Date:** 2026-09-02  
**Status:** Implemented — TN-0…TN-9 shipped and in-game confirmed 2026-09-02.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A general in-game toast card system — a corner-anchored, non-overlapping stack of transient
> cards, each owning its own dismissal timer — whose first consumer is a "now playing" card for
> the music scheduler. **The pivotal decision: song metadata lives in a standalone
> `MusicMetadataLibrary` ScriptableObject keyed by `AudioClip` reference, not in fields on
> `MusicTrack`** — the same clip appears in the global pool and in any number of biome pools, and
> per-entry fields would duplicate its title and artist once per appearance.

**Audited:** 2026-09-02, at commit `ba9ca09f` (branch `feat/world-scaling`).
Findings are from static review of `TooltipManager.cs`, `TooltipTrigger.cs`, `MusicScheduler.cs`,
`MusicTrack.cs`, `MusicCommand.cs`, `SoundManager.cs`, `AmbienceTrackListDrawer.cs`,
`SoundEditorWindow.Music.cs`, `RuntimeUIFactory.cs`, `ConsoleUI.cs`, `UIScaleController.cs`,
`DebugScreen.cs`, `SettingsManager.cs`, `SettingsUIGenerator.cs` and `WorldUIManager.cs`, plus the
canvas inventory in `RUNTIME_UI_FACTORY.md` §2 and the full `UI_BUGS #06` entry. Scene facts
(`MainMenu.unity` carries no `SoundManager`) were read from the serialized scene, not assumed.

**Relationship to other documents:**

- [`../Architecture/RUNTIME_UI_FACTORY.md`](../Architecture/RUNTIME_UI_FACTORY.md) — the
  code-built-UI factory this system is the third consumer of; its §2 canvas inventory gains a row
  when this ships.
- [`../Architecture/UI_BLUR_BACKDROP_SYSTEM.md`](../Architecture/UI_BLUR_BACKDROP_SYSTEM.md) — §4's
  authoring rules and §4.2's "a blurred panel replaces the UI beneath it" are why the frosted card
  falls back to a flat backdrop while a full-screen menu is up (§3.4's reversal note).
- [`../Architecture/COMMAND_CONSOLE_SYSTEM.md`](../Architecture/COMMAND_CONSOLE_SYSTEM.md) — the
  console that hosts the `/toast` verification command, and the precedent for building a whole UI
  hierarchy in code.
- [`SOUND_ENGINE_DESIGN.md`](SOUND_ENGINE_DESIGN.md) — §5.3 is the music layer this hooks; the
  metadata library is a sibling of its `AmbienceDatabase`, not a change to it.
- [`../Bugs/_FIXED_BUGS.md`](../Bugs/_FIXED_BUGS.md) — `UI_BUGS #06`, whose stacking half is the
  standing precedent against a high-sorting-order blurred panel (§3.4).

---

## 1. Goals & non-goals

### Goals

1. **A reusable toast surface** — any system can raise a transient card without knowing anything
   about UI construction, layout, or timing.
2. **Top-right by default, per-consumer override** — the anchor is a property of the *request*,
   with a manager-level default, mirroring how `TooltipHoverPosition` overrides work.
3. **A non-overlapping stack with independent timers** — several cards raised in quick succession
   stack cleanly, and each dismisses on its own schedule rather than sharing one.
4. **A clean trigger seam** — the music scheduler raises an event; nothing in the audio layer knows
   a UI exists.
5. **First consumer shipped** — a "now playing" card carrying song title, artist and (later) cover
   art.

### Non-goals (v1)

- **Authored cover art.** The field, the layout slot and the null-collapse behaviour all ship; the
  sprites do not (§3.3). Authoring them later needs no code change.
- **Toast variants** (achievement / warning / error styling) — one visual style in v1. Planned as a
  **v2 extension**, see §7's extension roadmap. *(Warning and Error shipped 2026-09-02 as TN-9, §3.5;
  achievement stays deferred until something raises one.)*
- **Localization.** No localization system exists in this project; strings are authored English on
  the metadata asset. Not a rejection — there is nothing to integrate with yet.
- **Reading metadata from the audio files.** Unity's `AudioClip` exposes no ID3 or container
  metadata, so title and artist are authored, never parsed.
- **Interactive toasts** (click to dismiss, click to act). Every card is explicitly non-interactive
  in v1 — see §3.4, where that is a correctness requirement rather than a scope cut.
- **Main-menu toasts.** `MainMenu.unity` carries no `SoundManager`, so no music plays there and
  nothing would fire. A future main-menu consumer would spawn its own host.

---

## 2. Current state (what exists today)

| Area                            | State                                                                                                                                                          |
|---------------------------------|----------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `TooltipManager.cs`             | Singleton + `[RuntimeInitializeOnLoadMethod]` reset, static `Show()`/`Hide()` facade, per-trigger position override falling back to `_hoverMode`. The model to follow. |
| …its multiplicity               | **One** live tooltip, **one** auto-hide coroutine, one reused prefab instance. It has no concept of a second simultaneous card — the gap this design fills.      |
| `TooltipManager.ShowInternal`   | Forces pivot `(0,1)`, sets `CanvasGroup.blocksRaycasts = false` / `interactable = false`, and calls `LayoutRebuilder.ForceRebuildLayoutImmediate` before bounds checks. All three patterns carry over. |
| `MusicScheduler.cs:248`         | `_source.Play()` appears **exactly once**, in `StartPending()`. Scheduled picks, `/music next` and `/music play <name>` all funnel through `QueueTrack` → `StartPending`. A single event site. |
| `MusicScheduler.QueueTrack`     | Takes `(AudioClip, float)` — the `MusicTrack` identity is discarded at the queue boundary. Carrying metadata to the start point means widening the pending state. |
| `MusicScheduler` timing         | `_openingGapSeconds = 60f`; gaps run `_minGapSeconds = 180` to `_maxGapSeconds = 480` (3–8 min). Nothing can fire in the first minute of a session.              |
| `MusicTrack.cs`                 | `[Serializable]` struct: `clip`, `weight`, `volume`, `environment`. **No title, artist or cover.**                                                              |
| `MusicCommand.cs:195`           | `TryFind` matches on `clip.name`, case-insensitively — `clip.name` is the de-facto song name today, and is the natural fallback title.                          |
| `SoundManager.cs:46`            | `private [SerializeField] AmbienceDatabase _ambience`, exposed as `public AmbienceDatabase Ambience` (`:196`). **Scene-wired, not `Resources.Load`** — the precedent a second database follows. |
| `AmbienceTrackListDrawer.cs:82` | `DrawMusicTrackList(SerializedProperty)` — the single authoring surface for music rows, shared by the global pool and every biome pool (`SoundEditorWindow.Music.cs:96`, `:120`). |
| `RuntimeUIFactory.cs`           | Static factory: canvas, panel, TMP text, button, scroll area, blur material + `ApplyBlurBackground`. **No image/icon helper** — the one primitive this design needs and does not find. |
| `ConsoleUI.cs`                  | The code-built-UI precedent: own overlay canvas at `sortingOrder = 100` via `ConfigureCanvas`, spawned as a runtime GameObject by `WorldUIManager.Awake`, zero scene or prefab edits. |
| Canvas sorting orders           | Benchmark HUD `-10`, scene UI canvas `0`, console `100`, benchmark results `200` (`RUNTIME_UI_FACTORY.md` §2). No slot above 200 is claimed.                    |
| `UIScaleController.cs`          | `[RequireComponent(CanvasScaler)]`; `Awake` applies the saved UI scale and it subscribes to `SettingsManager.OnSettingChanged`. It scales **its own** canvas, so any code-built canvas can opt in by adding the component. `ConsoleUI` does not. |
| `DebugScreen.cs:252`            | `_topRightText` (the performance panel) is active in both `Performance` and `Full` modes, and `CurrentMode` defaults to `Full`. Anchored top-right — a direct collision with the default toast position. |
| `SettingsManager.cs`            | A `public bool` with `[SettingField(SettingsTab.Audio, …)]` auto-generates a Toggle (`SettingsUIGenerator.cs:624`). Audio tab holds **six** volume sliders at `Order` 0–5 (Master, Music, Ambient, Block, Fluid, UI), so the toggle lands at `Order` 6. |
| Event conventions               | Instance `public event Action<T>` on singletons (`World.TeleportHoldEnded`, `PerformanceMonitor.OnMetricsSampled`, `BiomeTracker.BiomeChanged`, `CommandEngine.LineAppended`); one static (`SettingsManager.OnSettingChanged`). |
| Prior art                       | No toast or notification document exists anywhere in `Documentation/`. This is a cold start.                                                                    |

---

## 3. Decisions

### 3.1 Where song metadata lives — the pivotal choice

`MusicTrack` is the obvious home, and it is the wrong one.

#### Option A — new fields on `MusicTrack` (rejected)

- ✅ One authoring surface, already built (`AmbienceTrackListDrawer.DrawMusicTrackList`), reached
  from both the global pool and every biome pool.
- ✅ No lookup at play time — the metadata arrives with the track that was picked.
- ❌ **The same clip appears in many pools, so its title and artist are duplicated once per
  appearance.** A track offered globally and by three biomes is authored four times, and the four
  copies drift independently. `MusicTrack`'s own docstring establishes that per-entry fields are
  for things that are *properties of the entry* (its weight in this pool, its environment here) —
  a song's artist is a property of the song.
- ❌ Mixes concerns: `MusicTrack` answers "how should this be scheduled", and display text is not
  scheduling.
- ❌ Widens a struct serialized inside arrays on `AmbienceDatabase.asset` and every biome asset,
  putting authored weights and Loudness-tab trims in the blast radius of a serialization change.

#### Option B — `MusicMetadataLibrary` ScriptableObject ✅ **CHOSEN**

A standalone asset holding `(AudioClip clip, string title, string artist, Sprite cover)` entries,
keyed by **`AudioClip` reference**. Authored once per song, shared by every pool that offers it.
`MusicTrack` is not touched at all, which removes the serialization risk in Option A's last bullet
entirely — the authored weights and trims are never re-serialized.

Keyed by reference rather than by clip name deliberately: `MusicCommand.TryFind` already matches on
`clip.name`, and a name-keyed library would silently lose its metadata the moment an asset is
renamed. An object reference survives renames and moves.

The asset lives at `Assets/Resources/Data/MusicMetadataLibrary.asset` beside the databases already
there (`AmbienceDatabase`, `BlockDatabase`, `BlockSoundDatabase`, `EmitterSoundDatabase`, plus
`BuildStamp`), and is wired as a `private [SerializeField]` on `SoundManager` next to `_ambience` —
scene-wired, **not** `Resources.Load`, matching how `AmbienceDatabase` is reached today.

#### Option C — parse "Artist - Title" from the clip name (rejected)

- ✅ Zero new authoring, zero new assets, works immediately for every existing track.
- ❌ **Filenames become a load-bearing contract**, silently broken by any rename or by a track whose
  title legitimately contains a dash.
- ❌ No path to cover art at all.

### 3.2 How the card is built

Code-built via `RuntimeUIFactory`, following `ConsoleUI` — the manager creates its own overlay
canvas and every card's hierarchy in code. No scene edits, no prefab, no serialized references to
break, and the whole system works in any scene its host is spawned into. The cost is that styling
lives in constants rather than being visually editable; `TooltipPanel.prefab` shows the alternative,
and it drags prefab and `.meta` churn into every visual tweak.

One consequence worth stating: the toast canvas **adds a `UIScaleController` component**, so cards
honour the user's UI Scale setting. `ConsoleUI` never did this, which is why the console is the one
screen that ignores UI scale.

### 3.3 Cover art scope

The `Sprite cover` field, the card's icon slot, and its collapse-to-zero-width behaviour when the
sprite is null all ship in v1. The sprites themselves do not. Adding art later is pure authoring
against a layout that already reserves the space — no code change, no layout rework.

The deferral also postpones a real cost: `MusicMetadataLibrary.asset` lives under
`Assets/Resources/`, so everything it references is force-included in every build. Cover sprites
therefore ship whether or not a toast is ever shown, and want an import size budget when they are
authored.

### 3.4 Layering: always on top, frosted except over menus

**Decided: always visible, above everything** — canvas `sortingOrder = 250`, above the benchmark
results modal at 200. The alternative is recorded in full below because it had a specific bug
behind it, not because it was close.

The alternative was to suppress toasts while `WorldUIManager.InUI` is true and to offset the stack
below the F3 performance panel, citing `UI_BUGS #06` — where the benchmark HUD appeared to float
above the pause menu. It was rejected because a notification that hides exactly when the player has
opened a menu is not a notification surface; the `#06` risk is answered by making cards
non-interactive (point 1 below) rather than by hiding them.

Two things make this safe, and one makes it a hard constraint on the card's appearance:

1. **Cards never intercept input.** Every card sets `CanvasGroup.blocksRaycasts = false` and
   `interactable = false` (the `TooltipManager` pattern). A toast over an open menu can never eat a
   click. This is also why interactive toasts are a v1 non-goal — a dismissible toast would have to
   take raycasts back and would reintroduce the failure mode.
2. **`#06` was about a blurred, opaque, full-screen panel** — a different object. Its stacking half
   is intrinsic to the single-capture blur design, not a compositing bug that was fixed.
3. **Therefore the toast card uses a flat translucent `Image`, never `ApplyBlurBackground`.** This
   is the non-obvious one: `UIBlurRendererFeature` captures `_UIBlurTexture` *before any overlay
   canvas draws*, so every blur panel samples the same UI-free snapshot of the world. A blurred card
   at `sortingOrder 250` over a dimmed pause screen would paint un-dimmed world — reproducing `#06`'s
   exact symptom at the top of the stack. `RuntimeUIFactory`'s blur helpers are deliberately unused
   here.

Accepted consequences, stated plainly: a toast can cover the F3 performance panel's top-right
corner, and toasts draw over the pause menu.

> **Reversed 2026-09-02 — the card IS frosted, with a state-dependent fallback.** Point 3 above was
> right about the compositing and wrong about the conclusion. The card now shares `ToastManager`'s
> single blur material instance, and the manager swaps every live card to the flat translucent
> backdrop while a full-screen menu is up, swapping back when it closes. That keeps both
> properties this section argued were in conflict: frosted glass in normal play, and always-visible
> cards that never paint un-dimmed world over a dimmed menu. Cards stay visible in both states —
> only the backdrop material changes. The `#06` symptom is avoided by *never letting a blurred card
> overlap a blurred panel*, rather than by refusing blur outright.

### 3.5 Variants and the style table

Shipped 2026-09-02 as the first half of §7's v2 extension row. `ToastRequest` carries a
`ToastVariant`, and `ToastStyles.For(variant)` returns the one value that decides how the card looks:
accent colour, fallback icon glyph, blur tint, flat backdrop, and default dwell.

| Variant   | Accent    | Glyph | Default dwell | Notes                                              |
|-----------|-----------|:-----:|:-------------:|----------------------------------------------------|
| `Info`    | `#F5F5F5` |  none | 4.5 s         | The default. Icon slot collapses unless a consumer supplies a sprite or glyph. |
| `Warning` | `#FFC24B` |  `!`  | 7 s           | U+0021.                                            |
| `Error`   | `#FF6060` |  `×`  | 7 s           | U+00D7.                                            |

**Accent colours are the console's.** `Warning` and `Error` are parsed from
`ConsoleTextFormatter.WarningColor` / `.ErrorColor` rather than written again, so the two surfaces
cannot drift on what a warning looks like. That class is deliberately free of Unity types — its
colours are TMP hex strings — which is why they are parsed once into `Color` instead of shared as
values.

**Alerts dwell longer than notices.** 7 s against `Info`'s 4.5 s, because a warning arrives
unprompted and reports something the player did not ask about, so it has to survive not being looked
at immediately. A request that names its own duration still wins.

**The backdrop is tinted, not just the text.** Each variant owns a blur material instance tinted
`Lerp(neutral, accent, 0.35)` and a flat fallback `Lerp(black, accent, 0.18)`. Both tint strengths
are deliberately low: `_MultiplyColor` multiplies the blurred world, and a saturated tint turns the
whole card into a colour cast the text then has to fight. The manager therefore owns **one material
per variant**, resolved **per card** during the menu swap (§3.4) — cards of different variants can
be on screen together, and each needs its own tint back when the menu closes.

#### Icon glyphs and what the font actually has

The icon slot resolves **sprite → request glyph → variant glyph → collapsed**, so a variant's mark is
a default rather than a fixture.

`⚠` **U+26A0 WARNING SIGN is not in the project font.** `Monocraft` (`Assets/Fonts/Monocraft/`,
1475 glyphs) has `atlasPopulationMode = Static` and no fallback assets, so a missing codepoint
renders as a blank box and nothing supplies it at runtime. Measured 2026-09-02:

| Present | Absent |
|---------|--------|
| `!` U+0021 · `×` U+00D7 · `X` U+0058 · `‼` U+203C · `▲` U+25B2 · `⚡` U+26A1 · `✘` U+2718 · `❌` U+274C | `⚠` U+26A0 · `✕` U+2715 · `✖` U+2716 · `✗` U+2717 · `❗` U+2757 |

All eight present glyphs were confirmed to render (none blank) — a character-table entry can point at
an empty glyph, which `HasCharacter` cannot tell you, so `/toast glyphs` raises the shortlist at real
icon size to be judged by eye. `!` and `×` were chosen for legibility at 44 px.

---

## 4. Data model & architecture

### 4.1 Metadata

```csharp
/// <summary>
/// Display metadata for one music track: what a "now playing" card shows.
/// </summary>
/// <remarks>
/// Keyed by <see cref="clip"/> reference rather than by name, because the clip name is already a
/// matching key for <c>/music play</c> and a rename would silently orphan a name-keyed entry.
/// </remarks>
[Serializable]
public struct MusicMetadata
{
    public AudioClip clip;
    public string title;    // blank -> falls back to clip.name
    public string artist;
    public Sprite cover;    // null -> the card's icon slot collapses
}
```

`MusicMetadataLibrary` holds `MusicMetadata[]` and exposes a single
`bool TryGet(AudioClip clip, out MusicMetadata metadata)`. The backing
`Dictionary<AudioClip, MusicMetadata>` is built lazily on first access rather than eagerly, because
a library with no consumer in the scene should cost nothing. One lookup happens per track start —
every 3 to 8 minutes — so there is no hot-path consideration either way.

### 4.2 Toast request

```csharp
/// <summary>Where a toast stack is anchored. <c>None</c> defers to the manager's default.</summary>
public enum ToastAnchor { None, TopRight, TopLeft, BottomRight, BottomLeft }
```

`ToastRequest` is a struct carrying title, subtitle, optional icon `Sprite`, dwell duration, and an
anchor override. The enum mirrors `TooltipHoverPosition` exactly, including the `None`-means-default
convention, so a reader who knows one knows the other.

### 4.3 Runtime shape

```
┌────────────────────┐   TrackStarted    ┌──────────────────────────┐
│   MusicScheduler   │ ────(MusicTrack)─▶│ NowPlayingToastPresenter │
│  (knows no UI)     │                   │  (the only coupling)     │
└────────────────────┘                   └────────────┬─────────────┘
                                                      │ resolves via
                                         ┌────────────▼─────────────┐
                                         │  MusicMetadataLibrary    │
                                         └────────────┬─────────────┘
                                                      │ ToastRequest
                                         ┌────────────▼─────────────┐
                                         │      ToastManager        │
                                         │ own canvas @ order 250   │
                                         │ + UIScaleController      │
                                         └────────────┬─────────────┘
                                    one container per anchor
                                    (VerticalLayoutGroup)
                                         ┌────────────▼─────────────┐
                                         │  ToastCard × N (pooled)  │
                                         │  own unscaled timer      │
                                         └──────────────────────────┘
```

**Stacking is delegated to Unity's layout system, not hand-rolled.** Each anchor owns a container
with a `VerticalLayoutGroup` + `ContentSizeFitter`. Non-overlap falls out for free, and so does the
case that would otherwise need bespoke math: a card whose title wraps to two lines is taller, and
every card below it moves by the right amount without anyone computing an offset.

**A card expiring in the middle of the stack therefore needs no compaction pass.** Its exit
animation shrinks its own `LayoutElement.preferredHeight` to zero while fading its `CanvasGroup`;
the layout group closes the gap smoothly as a side effect of the normal rebuild. The alternative —
removing the card outright — would work but would snap.

**Ownership and lifetime.** `ToastManager` is a singleton with a `DomainReset`
`[RuntimeInitializeOnLoadMethod]` (one per class, per `CLAUDE.md`), spawned as a runtime GameObject
by `WorldUIManager.Awake`, exactly as the console is. Cards are drawn from and returned to a
free-list — never `Instantiate`/`Destroy` per toast. Each anchor caps simultaneous cards; overflow
waits in a queue and enters as a slot frees.

**Timers are unscaled.** `WaitForSecondsRealtime`, matching both `TooltipManager`'s auto-hide and
`MusicScheduler`'s `Time.unscaledDeltaTime`. A toast must dismiss itself whatever the time scale.

### 4.4 The trigger seam

`MusicScheduler` gains `public event Action<MusicTrack> TrackStarted`, raised inside
`StartPending()` — the single point where a clip becomes audible, so scheduled picks, `/music next`
and `/music play <name>` are all covered by one raise. Reaching that point with the metadata intact
requires widening the pending state from `(_pendingTrack, _pendingVolume)` to carry the full
`MusicTrack`, since `QueueTrack` currently discards it.

> **Shipped differently.** The payload is `AudioClip`, not `MusicTrack`, and the pending state was
> never widened — see §9's row for why the widening was rejected once its blast radius was read.
> The rest of this section (single raise site, instance event, `Start`/`OnDestroy` subscription)
> shipped as written.

An instance event on the singleton, matching `World.TeleportHoldEnded` and
`PerformanceMonitor.OnMetricsSampled` — not a static, which would need its own domain-reload
handling for subscriber lists.

`NowPlayingToastPresenter` subscribes in `Start()` (after every `Awake` has run — the
`WorldUIManager` precedent) and unsubscribes in `OnDestroy`. **It is the only file in the codebase
that knows music and toasts are related.** The scheduler gains no UI reference; the toast system
gains no audio reference.

---

## 5. Prerequisites & integration points

Nothing blocks this design — every system it touches is shipped and stable.

- **`RuntimeUIFactory` has no image/icon primitive** (verified: panel, TMP text, button, scroll area
  and the blur helpers only). The card needs a small local helper for its icon slot. Whether that
  helper is promoted into the factory is a judgement call for the implementer; one consumer does not
  yet justify it.
- **Ordering is not tight.** `_openingGapSeconds = 60f` means no track can start within a minute of
  scene load, so a host spawned in `WorldUIManager.Awake` is always ready first.
- **Reserved seats.** `ToastAnchor` covers four corners from the start, and `ToastRequest` is a
  struct the presenter fills — a second consumer (achievements, warnings) adds a presenter and a
  style, not a change to the manager.

### Documentation follow-ups (for `docs-sync`, when this ships)

- `RUNTIME_UI_FACTORY.md` §2's canvas inventory needs a toast row at `sortingOrder 250`.
- `RUNTIME_UI_FACTORY.md` §2 currently reads "`UIScaleController` rescales only the scene canvas,
  not the console's own". That is true as shipped but frames as a controller limitation what is
  really the console never adding the component — this design adds it (§3.2), and the sentence
  should be re-worded when the row lands.

---

## 6. Constraint compliance checklist

| Project constraint                              | How this design complies                                                                                                              |
|-------------------------------------------------|-----------------------------------------------------------------------------------------------------------------------------------------|
| Voxels are packed `uint`s, no per-voxel objects | Not applicable — no voxel data is read or written anywhere in this system.                                                             |
| Burst jobs 100 % Burst-compatible               | Not applicable — nothing here runs in a job; all work is main-thread UI.                                                               |
| No GC / LINQ in hot paths                       | Nothing runs per frame except the exit animation on a live card (≤ N cards, no allocation). Metadata lookup is one dictionary hit every 3–8 min. Cards come from a free-list, never `Instantiate` per toast. |
| Pooling conventions                             | Card GameObjects are pooled in a manager-owned free-list, reused across the session, matching the "reuse the instance" pattern `TooltipManager` already uses for its single tooltip. |
| No BinaryFormatter/JSON for terrain             | Not applicable — nothing reaches disk. `MusicMetadataLibrary` is a Unity asset, not a save file; **the on-disk save format is untouched** and no version bump is involved. |
| `BlockIDs` constants, no raw IDs                | Not applicable — no block IDs are referenced.                                                                                          |
| No magic numbers                                | Dwell duration, card cap, spacing, card width, animation length and the canvas sorting order are all named `private const` (SCREAMING_CASE) on their owning class. |
| Domain-reload static discipline                 | `ToastManager.Instance` is cleared by a single `[RuntimeInitializeOnLoadMethod]` `DomainReset` and by `OnDestroy`, matching `TooltipManager` and `MusicScheduler`. No second reset method is added to any class that already has one. |
| No backlog IDs in user-facing UI                | `TN-*` appears in this document only. The `/toast` command's help text, the settings label and every card string are plain English.     |

---

## 7. Phased implementation plan

| Phase                             | Scope                                                                                                                                                | Effort | Depends on   | Status |
|-----------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------|:------:|--------------|--------|
| **TN-0 — Metadata library**       | `MusicMetadata` + `MusicMetadataLibrary` in `Assets/Scripts/Data/`; asset at `Assets/Resources/Data/`; `[SerializeField]` + property on `SoundManager`; lazy dictionary. | 🟢     | —            | ✅ 2026-09-02 |
| **TN-1 — Sound Editor authoring** | New section in `SoundEditorWindow.Music.cs`: one row per entry, plus a "Sync from pools" button appending rows for any clip in a pool with no entry. Editor assembly. | 🟡     | TN-0         | ✅ 2026-09-02 |
| **TN-2 — Toast contract**         | `ToastAnchor` enum + `ToastRequest` struct in `Assets/Scripts/UI/Toast/`.                                                                              | 🟢     | —            | ✅ 2026-09-02 |
| **TN-3 — ToastManager**           | Singleton + `DomainReset`; own canvas at `sortingOrder 250` via `RuntimeUIFactory`, plus `UIScaleController`; per-anchor `VerticalLayoutGroup` containers; card free-list and overflow queue; static `Show(in ToastRequest)`. Spawned by `WorldUIManager.Awake`. | 🟡     | TN-2         | ✅ 2026-09-02 |
| **TN-4 — ToastCard**              | Card view + lifetime: unscaled `WaitForSecondsRealtime` timer, shrink-and-fade exit, `blocksRaycasts = false` / `interactable = false`, frosted backdrop from the manager's blur material with a flat fallback while a full-screen menu is up (§3.4), collapsing icon slot. | 🟡     | TN-3         | ✅ 2026-09-02 |
| **TN-5 — `/toast` dev command**   | Console command spawning N test cards with staggered durations. The verification instrument for TN-3/TN-4, not a player feature.                       | 🟢     | TN-4         | ✅ 2026-09-02 |
| **TN-6 — `TrackStarted` event**   | Widen `MusicScheduler`'s pending state to carry the full `MusicTrack`; raise `public event Action<MusicTrack> TrackStarted` in `StartPending()`.       | 🟢     | —            | ✅ 2026-09-02 |
| **TN-7 — Now-playing presenter**  | `NowPlayingToastPresenter`: subscribes in `Start`, unsubscribes in `OnDestroy`, resolves metadata, falls back to `clip.name`, formats the request.     | 🟢     | TN-0, 4, 6   | ✅ 2026-09-02 |
| **TN-8 — Settings toggle**        | `showNowPlayingToasts` — one attributed `public bool` on `SettingsManager`, Audio tab, after the volume sliders.                                       | 🟢     | TN-7         | ✅ 2026-09-02 |
| **TN-9 — Variants**               | `ToastVariant` + `ToastStyle`/`ToastStyles` table; `Variant` and `Glyph` on `ToastRequest`; per-variant blur material on the manager; glyph slot beside the sprite slot on the card; `/toast [variant]` and `/toast glyphs`. Warning + Error only (§3.5). | 🟡     | TN-4, TN-5   | ✅ 2026-09-02 |
Status: `—` not started · `In progress` · `✅ YYYY-MM-DD` complete (dated at in-game confirmation) ·
`⏸️ YYYY-MM-DD` deliberately not implemented · `⛔ Superseded YYYY-MM-DD — <by what>`.

**TN-2 → TN-5 delivers standalone value**: a working, demonstrable toast system with a dev trigger
and no consumer. TN-0/TN-1 plus TN-6 → TN-8 add the music consumer on top of it. The two halves can
land in either order, and each is independently useful.

### Verification gates

Every phase ends with `dotnet build "Assembly-CSharp.csproj"`; **TN-1 additionally requires**
`dotnet build "Assembly-CSharp-Editor.csproj"`, since editor-only code lives in an assembly the
runtime project does not compile. After editing, wait until the built DLL is newer than the source
before running anything in-editor — a green `dotnet build` beside a stale DLL means Unity's own
compile failed.

The two gates that matter are behavioural:

- **`/toast 5` with staggered durations** (TN-5) proves the stack is non-overlapping, that timers
  are genuinely independent, and — the case worth building the command for — **that a card expiring
  in the *middle* of the stack closes its gap without overlapping its neighbours**. That is the
  single most likely thing in this design to be subtly wrong, and it is why the verification
  instrument ships as its own phase rather than being improvised at the end.
- **`/music play <clipName>`** (TN-7) proves the real path end to end: the event fires, the presenter
  resolves metadata, and the card shows the authored title and artist. Waiting for a natural pick is
  not a usable gate — gaps run 3 to 8 minutes.

`lint_files` runs on every new file (the UDR0004/UDR0005 domain-reload analyzers catch exactly the
static-state class this design introduces).

**Two gate corrections found during execution (2026-09-02):**

- **TN-5 must bump `ConsoleCommandInstaller.InstalledCommandCount`** (17 → 18) and re-run
  `Minecraft Clone/Dev/Validate Command Console`. The constant is asserted in three places by that
  suite's B32 count-floor, so registering `/toast` without the bump reds it. Not anticipated here.
- **`Validate Sound Engine` cannot observe TN-6.** Every one of its music scenarios exercises the
  pure `MusicResolution` layer; **no scenario touches `MusicScheduler`**, so it would stay green
  through an arbitrarily broken change to the scheduler. Keeping it green is a regression guard on
  the *rest* of the sound engine, never a verification of the trigger seam — `/music play <name>`
  in game is the only gate that reaches it.

**No new validation suite is proposed.** The stacking behaviour is a Unity layout-system outcome
rather than project logic, and a suite that asserted it would be testing `VerticalLayoutGroup`. See
§8 for the one part of this that may deserve reconsidering.

### Extension roadmap (post-TN-8, in intended order)

| Version | Extension                                                                                                                                  |
|---------|----------------------------------------------------------------------------------------------------------------------------------------------|
| **v2**  | Toast variants — ✅ **Warning + Error shipped 2026-09-02** (TN-9, §3.5). Achievement deferred until a consumer exists. Note the row's original claim that "the API already takes them" was wrong: `ToastRequest` had no variant field, so the contract change was part of the work. |
| **v2**  | Authored cover art for the existing music pool, with an import size budget (§3.3).                                                          |
| **v3+** | Interactive toasts (click to dismiss or act). Requires re-taking raycasts, so it must resolve §3.4's input rule first — gets its own design. |
| **v3+** | A main-menu toast host, if the main menu ever gains music or notifications.                                                                  |

---

## 8. Open questions

1. **Dwell duration and the simultaneous-card cap are unset.** Both are tuning values best chosen by
   eye in game rather than argued on paper. They land as named constants in TN-3/TN-4 and get
   adjusted on first in-game confirmation.
2. **Does the overflow queue deserve a validation baseline?** The stack layout does not (it is
   Unity's), but "N cards raised while N are already live, and they enter in order as slots free" is
   project logic with an ordering invariant. If TN-5 makes that awkward to check by eye, a small
   scenario in the existing framework is the fallback.

---

## 9. Rejected alternatives

| Alternative                                            | Why rejected                                                                                                                                                   | Date       |
|--------------------------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------|------------|
| `title` / `artist` / `cover` fields on `MusicTrack`     | Duplicates a song's metadata once per pool it appears in, and mixes display data into a scheduling struct. Also widens a struct serialized inside `AmbienceDatabase.asset` and every biome asset (§3.1). | 2026-09-02 |
| Parsing `"Artist - Title"` from the clip filename      | Makes filenames a load-bearing contract, breaks on any rename or on a title containing a dash, and offers no path to cover art (§3.1).                          | 2026-09-02 |
| A `ToastCard.prefab` beside `TooltipPanel.prefab`      | Needs a scene-wired manager reference and drags prefab/`.meta` churn into every visual tweak. Code-built follows the `ConsoleUI` precedent instead (§3.2).      | 2026-09-02 |
| Suppressing toasts while `WorldUIManager.InUI`         | Proposed on the `UI_BUGS #06` precedent; **rejected** in favour of always-visible — a surface that hides whenever a menu opens is not a notification surface. Made safe by non-interactive cards, not by suppression (§3.4). | 2026-09-02 |
| Offsetting the stack below the F3 performance panel    | Same override. The overlap with `DebugScreen`'s top-right panel is accepted (§3.4).                                                                             | 2026-09-02 |
| A blur backdrop on the toast card                      | ~~`_UIBlurTexture` is captured before any overlay canvas draws, so a blurred card at `sortingOrder 250` would paint un-dimmed world over a dimmed pause screen — `UI_BUGS #06`'s stacking symptom, reproduced (§3.4). Flat translucent `Image` instead.~~ **Reversed 2026-09-02.** Frosted glass is the intended treatment for every UI surface in this project, so "no blur here" was never an acceptable resting state. The compositing analysis above holds — the all-or-nothing conclusion drawn from it did not. Shipped frosted, with the manager swapping live cards to the flat backdrop while a full-screen menu is up — see §3.4's reversal note. | 2026-09-02 |
| Hand-rolled stack offset math                          | `VerticalLayoutGroup` + `ContentSizeFitter` handles non-overlap, variable card heights and mid-stack gap closure for free; hand-rolled math would re-derive all three and get wrapped titles wrong (§4.3). | 2026-09-02 |
| A `static` `TrackStarted` event                        | Would need its own domain-reload handling for the subscriber list. The codebase's instance-event-on-singleton convention avoids it (§4.4).                      | 2026-09-02 |
| `event Action<MusicTrack> TrackStarted` (this doc's §4.4) | **Reversed at implementation.** Shipped as `Action<AudioClip>`. The presenter keys the library by clip and reads none of `MusicTrack`'s other fields, while carrying the struct required widening the scheduler's pending state — rippling into `QueueTrack`, `ForcePick` (returns `AudioClip`, read by `/music next`), `DiagPendingTrack` (read by the `/music` readout) and `ForceTrack`, which holds no `MusicTrack` at all and would have had to fabricate a weight and environment. The clip alone made TN-6 a three-line addition that touches no existing state. | 2026-09-02 |

---

## Document History

* **v1.4** - **Backdrop suppression narrowed to full-screen menus (2026-09-02).** The fallback was
  keyed to `WorldUIManager.InUI`, which is also true for the console and the creative inventory — so
  opening the console flattened every card for no reason, visible as the frost dropping out. Only the
  pause-menu family is full-screen (`PauseMenu` and `HelpMenu` are anchor 0,0→1,1 with zero
  sizeDelta), and `IsPauseMenuOpen` stays true across the pause panel, the settings menu and the help
  menu, so that flag is now the condition. Every other blurred surface is bounded and nowhere near
  the default anchor — the inventory is centre-anchored 216×168, the toolbar bottom-centre 218×26,
  the console panel bottom-left 680×440. The residual case — a bottom-left toast raised while the
  console is open — is the blur system's inability to stack panels rather than a gap in this policy,
  and is recorded as such in `UI_BLUR_BACKDROP_SYSTEM.md` §8.
* **v1.3** - **Toast variants shipped (TN-9, 2026-09-02)** — Warning and Error only; achievement
  deferred. New §3.5 records the style table, the accent colours shared with `ConsoleTextFormatter`,
  the tinted-backdrop consequence (one blur material per variant, resolved per card during the menu
  swap) and the 7 s alert dwell. **`⚠` U+26A0 is not in Monocraft** and its atlas is static, so the
  warning mark is `!` and the error mark is `×`, both chosen by eye from a shortlist rendered at icon
  size; §3.5 carries the measured coverage table. Corrected the v2 roadmap row, which claimed the
  API already took a variant — `ToastRequest` had no such field.
* **v1.2** - **Toast cards are frosted glass (2026-09-02).** §3.4's blur ban reversed — frosted
  glass is the intended treatment for every UI surface here, so the ban was a wrong reading of the
  constraint rather than a scope choice. The card shares one manager-owned blur material and the
  manager swaps live cards to the flat backdrop while `WorldUIManager.InUI` is true, so §4.2's
  "a blurred panel replaces the UI beneath it" is honoured without giving up always-visible cards.
  §9's blur row struck through with the reason. Note the enter/exit `CanvasGroup` fade still sweeps
  alpha, which per blur doc §4.1 briefly bleeds sharp screen — a ~0.2–0.3 s transition artifact,
  accepted.
* **v1.1** - **TN-0…TN-8 shipped + in-game CONFIRMED 2026-09-02** — all nine §7 rows flipped ✅.
  `TrackStarted` shipped as `Action<AudioClip>` rather than `Action<MusicTrack>`, so the scheduler's
  pending state was never widened (§4.4 note, §9 row). Metadata authoring landed in the Music tab's
  **Global scope** rather than a tab of its own, with a "Sync from pools" button that prefills the
  artist from `CreditsDatabase`'s folder-scoped `author`. Two gate corrections recorded in §7:
  TN-5 must bump `InstalledCommandCount` (17→18) for the B32 count-floor, and `Validate Sound
  Engine` structurally cannot observe TN-6. Sound Engine suite 78→79 with a new metadata census
  (proved red on an empty library). Three §2 drift items corrected (Audio `Order` 0–5 not 0–4;
  the `Assets/Resources/Data/` asset count; the TN-6 gate claim).
* **v1.0** - Initial design

---

**Last Updated:** 2026-09-02  
**Next Review:** when the toast system is promoted to an Architecture doc (due — last phase complete)
