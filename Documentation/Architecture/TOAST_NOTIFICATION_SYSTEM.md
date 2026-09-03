# Toast Notification System

**Version:** 1.0  
**Date:** 2026-09-02  
**Status:** **Implemented (Stable)** — TN-0…TN-9 shipped and confirmed in game 2026-09-02.  
**Target:** Unity 6.6 (Mono for dev; IL2CPP for production)

> A corner-anchored, non-overlapping stack of transient cards, each owning its own dismissal timer.
> Any system raises one through a single static call and knows nothing about UI construction, layout
> or timing. **The pivotal decision: stacking is delegated to Unity's layout system, not hand-rolled**
> — non-overlap, variable card heights from wrapped titles, and mid-stack gap closure all fall out of
> a `VerticalLayoutGroup` rebuild.

**Audited:** 2026-09-02, at commit `c2694593` (branch `feat/world-scaling`).
Every claim below was re-verified against current code this session, from the code rather than from
the Design doc: `ToastAnchor.cs`, `ToastVariant.cs`, `ToastRequest.cs`, `ToastStyle.cs`,
`ToastCard.cs`, `ToastManager.cs`, `NowPlayingToastPresenter.cs`, `ToastCommand.cs`,
`ConsoleCommandInstaller.cs`, `MusicMetadata.cs`, `MusicMetadataLibrary.cs`, `SoundManager.cs`,
`MusicScheduler.cs`, `SettingsManager.cs`, `WorldUIManager.cs`,
`SoundEditorWindow.MusicMetadata.cs` and `SoundEngineValidationSuite.Content.cs`. Constants were
extracted from source rather than transcribed. Panel rects came from parsing `World.unity`; font
coverage from a live `TMP_FontAsset` query.

**Relationship to other documents:**

- [`../Design/TOAST_NOTIFICATION_SYSTEM.md`](../Design/TOAST_NOTIFICATION_SYSTEM.md) — the design
  this was promoted from. It remains the dated record of intent; this document describes the code.
- [`RUNTIME_UI_FACTORY.md`](RUNTIME_UI_FACTORY.md) — the code-built-UI factory this is the third
  consumer of; §2's canvas inventory carries the toast rows.
- [`UI_BLUR_BACKDROP_SYSTEM.md`](UI_BLUR_BACKDROP_SYSTEM.md) — §4's authoring rules and §4.2's
  "a blurred panel replaces the UI beneath it" govern the backdrop; §8 owns the residual stacking
  limit this system works around.
- [`COMMAND_CONSOLE_SYSTEM.md`](COMMAND_CONSOLE_SYSTEM.md) — §8.5 documents the `/toast` command.
- [`../Design/SOUND_ENGINE_DESIGN.md`](../Design/SOUND_ENGINE_DESIGN.md) — §5.3 is the music layer
  the first consumer hooks.

---

## ID index

| ID | Scope | Where it now lives |
|----|-------|--------------------|
| **TN-0** | `MusicMetadata` + `MusicMetadataLibrary`, asset, `SoundManager` wiring | §6.1 |
| **TN-1** | Sound Editor authoring section + "Sync from pools" | §6.2 |
| **TN-2** | `ToastAnchor` + `ToastRequest` | §2 |
| **TN-3** | `ToastManager`: canvas, anchors, pooling, queue | §3 |
| **TN-4** | `ToastCard`: view + lifetime | §4 |
| **TN-5** | `/toast` dev command | §7.1, and `COMMAND_CONSOLE_SYSTEM.md` §8.5 |
| **TN-6** | `MusicScheduler.TrackStarted` | §6.3 |
| **TN-7** | `NowPlayingToastPresenter` | §6.3 |
| **TN-8** | `showNowPlayingToasts` setting | §6.4 |
| **TN-9** | `ToastVariant` + `ToastStyle` style table | §5 |

---

## 1. Runtime shape

```
┌────────────────────┐  TrackStarted   ┌──────────────────────────┐
│   MusicScheduler   │ ───(AudioClip)─▶│ NowPlayingToastPresenter │
│   (knows no UI)    │                 │   (the only coupling)    │
└────────────────────┘                 └────────────┬─────────────┘
                                                    │ resolves via
                                       ┌────────────▼─────────────┐
                                       │   MusicMetadataLibrary   │
                                       └────────────┬─────────────┘
                                                    │ ToastRequest
                                       ┌────────────▼─────────────┐
                                       │       ToastManager       │
                                       │  own canvas @ order 250  │
                                       │  + UIScaleController     │
                                       │  + 1 blur material/variant│
                                       └────────────┬─────────────┘
                                    one container per anchor
                                    (VerticalLayoutGroup)
                                       ┌────────────▼─────────────┐
                                       │  ToastCard × N (pooled)  │
                                       │   own unscaled timer     │
                                       └──────────────────────────┘
```

`ToastManager` and `NowPlayingToastPresenter` share one runtime GameObject named `Toasts`, created
by `WorldUIManager.Awake` beside the console's — no scene object, no prefab, no serialized reference.

`NowPlayingToastPresenter` is **the only file that knows music and toasts are related.** The
scheduler holds no UI reference; the toast system holds no audio reference.

---

## 2. The request contract

`ToastRequest` is a `readonly struct` the caller fills:

| Field | Meaning |
|-------|---------|
| `Title` | The headline. A request whose title is null/whitespace is not shown (`IsShowable`). |
| `Subtitle` | Second line; null/empty collapses the row. |
| `Icon` | `Sprite` for the icon slot. |
| `Glyph` | Text glyph for the icon slot, used when `Icon` is null. |
| `DwellSeconds` | Seconds to dwell; ≤ 0 uses the variant's default. |
| `Anchor` | `ToastAnchor`; `None` uses the manager's default. |
| `Variant` | `ToastVariant`; selects accent, glyph and default dwell. |

`ToastAnchor` is `{ None, TopRight, TopLeft, BottomRight, BottomLeft }` and mirrors
`TooltipHoverPosition`'s `None`-means-default convention, so a reader who knows one knows the other.

Every field after `Title` is an optional constructor parameter, so a caller states only what it
cares about.

---

## 3. `ToastManager`

Singleton with a single `[RuntimeInitializeOnLoadMethod]` `DomainReset` clearing `Instance`, plus
`OnDestroy`. Entry point is `static void Show(in ToastRequest)`, which is safe to call when no
manager exists — toasts are a feedback layer and must never break a caller.

### 3.1 Canvas

`RuntimeUIFactory.ConfigureCanvas(gameObject, 250)`, then `AddComponent<UIScaleController>()` — in
that order, because `UIScaleController` requires a `CanvasScaler` and reads it in its own `Awake`,
which runs synchronously inside `AddComponent`.

`sortingOrder 250` is above the benchmark results modal at 200, so toasts draw over every screen
including the pause menu. That is safe only because every card sets `blocksRaycasts = false` and
`interactable = false`, so a card can never eat a click on the menu beneath it.

### 3.2 Anchors and stacking

Four containers, one per corner, indexed by `(int)anchor - 1` (with `None` folded onto the first
slot). Each is anchored and pivoted to its corner, inset by `EDGE_MARGIN`, and carries a
`VerticalLayoutGroup` (`spacing = CARD_SPACING`) plus a `ContentSizeFitter` set to `PreferredSize`
on both axes.

- `childAlignment` is `UpperRight` for right-hand corners, `UpperLeft` otherwise.
- `reverseArrangement` is true for the bottom corners, so the newest card — always the last sibling
  — is drawn at the bottom and the stack grows upward.

### 3.3 Pooling and the overflow queue

| Constant | Value |
|----------|------:|
| `MAX_CARDS_PER_ANCHOR` | 3 |
| `MAX_QUEUED_PER_ANCHOR` | 8 |
| `AnchorCapacity` (public) | 11 |
| `EDGE_MARGIN` | 16 |
| `CARD_SPACING` | 8 |

Cards come from a manager-owned free-list shared by every anchor, never `Instantiate`/`Destroy` per
toast. When an anchor is full the request is queued; beyond `MAX_QUEUED_PER_ANCHOR` the **newest** is
dropped, so the queue keeps the order it was raised.

`OnCardFinished` **admits the queued request before returning the finished card to the free-list.**
That ordering is deliberate: pooling the card first would let the queued request draw the very card
that is retiring, re-entering `Show` from inside its own `Retire()`. That works only while the
lifetime coroutine happens to clear its handle first, which is not something a future edit should
have to know about.

`AnchorCapacity` is public so a caller raising a batch can size it to what will actually be shown
rather than hard-coding a number that desyncs when either limit moves.

### 3.4 Backdrop material and suppression

The manager owns **one blur material instance per `ToastVariant`**, built in `Awake` from
`ToastStyles.For(variant).BlurTint` and all destroyed in `OnDestroy`. Per variant rather than per
card: a variant needs its own tint, but cards are pooled and built lazily, so a per-card instance
would leak one material per card the session ever needed.

`IsBlurSuppressed` is true while `WorldUIManager.IsPauseMenuOpen`. While suppressed,
`BackdropMaterialFor` returns null and each card paints its variant's flat colour instead.

The reason is `UI_BLUR_BACKDROP_SYSTEM.md` §4.2: a blurred panel does not composite over the UI
beneath it, it *replaces* it with a hole back to the pre-UI frame. A frosted card at order 250 over
a dimmed pause screen would therefore paint un-dimmed world — `UI_BUGS #06`'s symptom.

**Keyed to the pause menu rather than to "some UI is open"**, because only the pause-menu family is
full-screen: `PauseMenu` and `HelpMenu` are anchored (0,0)–(1,1) with zero `sizeDelta`, and
`IsPauseMenuOpen` stays true across the pause panel, the settings menu and the help menu (opening
either hides the pause panel without clearing the flag). Every other blurred surface is bounded and
nowhere near the default anchor — the creative inventory is centre-anchored 216×168, the toolbar
bottom-centre 218×26, the console panel bottom-left 680×440.

`Update` polls that flag every frame — a null check and a bool compare — and runs the swap only on
the transition, re-resolving the material **per card** from its own `Variant`, since cards of
different variants can be on screen together.

---

## 4. `ToastCard`

Built in code by `ToastCard.Create` and pooled. Hierarchy: a root carrying the backdrop `Image`, a
`RectMask2D`, a `CanvasGroup`, a `HorizontalLayoutGroup` and a `LayoutElement`; children are the
icon `Image`, the glyph label, and a vertical text column holding title and subtitle.

| Constant | Value |
|----------|------:|
| `CARD_WIDTH` | 340 |
| `CARD_PADDING` | 12 |
| `ICON_GAP` | 10 |
| `ICON_SIZE` | 44 |
| `GLYPH_FONT_SCALE` | 0.8 |
| `TEXT_SPACING` | 2 |
| `TITLE_FONT_SIZE` | 21 |
| `SUBTITLE_FONT_SIZE` | 16 |
| `ENTER_SECONDS` | 0.22 |
| `EXIT_SECONDS` | 0.3 |

### 4.1 Icon resolution

**sprite → request glyph → variant glyph → collapsed.** The sprite `Image` and the glyph label are
siblings occupying the same slot, and exactly one is ever active; an inactive object is skipped by
the layout group, which is what collapses the slot when a card has neither.

Sprite wins because it is the more specific medium — a consumer that authored art meant it. The
glyph exists so a variant can carry a recognizable mark with nothing authored.

### 4.2 Lifetime

`WaitForSecondsRealtime` and `Time.unscaledDeltaTime` throughout, matching `TooltipManager`'s
auto-hide and `MusicScheduler`'s timing: a toast must dismiss itself whatever the time scale.

1. Fade the `CanvasGroup` in over `ENTER_SECONDS`.
2. Hold for the resolved dwell.
3. Over `EXIT_SECONDS`, drive `LayoutElement.preferredHeight` **and** `minHeight` from the current
   height to zero while fading out.

Driving both heights matters: the card's own layout group reports a minimum from its content, and
the parent would honour that floor and stop the collapse short.

Shrinking rather than removing is what makes a card expiring in the **middle** of the stack slide
shut instead of snapping — the `VerticalLayoutGroup` closes the gap as a normal rebuild.

The text is clipped by the root's `RectMask2D` while the card shrinks; without it the content
overflows the collapsing rect and the card appears to slide under its neighbor rather than close.
The blur shader supports `UNITY_UI_CLIP_RECT`, so the frosted backdrop clips correctly too.

### 4.3 Reuse

`Show` rewrites **every** variant-dependent property — variant, flat backdrop colour, title colour,
glyph colour, backdrop material, icon state, and the layout heights left zeroed by the last exit —
not only the ones a request sets. A pooled card last shown as an error must not come back red.

---

## 5. Variants and the style table

`ToastStyles.For(variant)` returns a `ToastStyle` carrying accent colour, fallback glyph, blur tint,
flat backdrop and default dwell.

| Variant | Accent | Glyph | Default dwell |
|---------|--------|:-----:|:-------------:|
| `Info` | `#F5F5F5` | none | 4.5 s |
| `Warning` | `#FFC24B` | `!` | 7 s |
| `Error` | `#FF6060` | `×` | 7 s |

`For` is a **total switch with a `_ =>` Info default**, so a variant added to the enum and forgotten
here renders as a neutral card rather than an unstyled one — there is no "missing entry" state.

**Accent colours are the console's.** `Warning` and `Error` are parsed once from
`ConsoleTextFormatter.WarningColor` / `.ErrorColor` rather than written again, so the two surfaces
cannot drift on what a warning looks like. That class is deliberately free of Unity types — its
colours are TMP hex strings — which is why they are parsed into `Color` here instead of shared as
values.

**Alerts dwell longer than notices** (7 s vs 4.5 s) because they arrive unprompted and report
something the player did not ask about. A request naming its own duration still wins.

**Backdrop tints are derived, not authored:** `Lerp(neutral, accent, 0.35)` for the blur tint and
`Lerp(black, accent, 0.18)` for the flat colour, from a neutral input of `0.415` grey and black at
alpha 0.55. Both strengths are low because `_MultiplyColor` multiplies the blurred world, and a
saturated tint turns the card into a colour cast the text then has to fight.

Resolved values, since the neutral input never reaches the screen:

| Variant | Blur tint (`_MultiplyColor`) |
|---------|------------------------------|
| `Info` | `(0.606, 0.606, 0.606)` |
| `Warning` | `(0.620, 0.536, 0.373)` |
| `Error` | `(0.620, 0.402, 0.402)` |

Note **`Info` is `0.606` grey, not the `0.415` the console and the five scene panels use** — the lerp
is applied to every variant, and Info's near-white accent pulls its tint 35 % toward white. An
info-variant card is therefore a lighter frost than every other blurred surface in the project. That
is a consequence of the derivation rather than a deliberate choice; see §8.

### 5.1 Glyphs and the font constraint

The project's TMP default font is `Monocraft` (1475 glyphs), with
**`atlasPopulationMode = Static` and no fallback assets** — a codepoint the atlas lacks renders as a
blank box and nothing supplies it at runtime.

**`⚠` U+26A0 WARNING SIGN is absent**, along with `✕` U+2715, `✖` U+2716, `✗` U+2717 and
`❗` U+2757. Present and confirmed to render: `!` U+0021, `×` U+00D7, `X` U+0058, `‼` U+203C,
`▲` U+25B2, `⚡` U+26A1, `✘` U+2718, `❌` U+274C.

`HasCharacter` proves presence but not that the glyph is non-empty — a character-table entry can
point at a blank glyph, which no API reveals. `/toast glyphs` renders the shortlist at real icon size
for that reason and is kept for the next variant.

---

## 6. The music consumer

### 6.1 Metadata library

`MusicMetadata` is a `[Serializable]` struct of `(AudioClip clip, string title, string artist,
Sprite cover)`, **keyed by clip reference**. `DisplayTitle` falls back to `clip.name` when no title
is authored; `IsValid` is `clip != null`.

`MusicMetadataLibrary` is a `ScriptableObject` holding `MusicMetadata[]`, exposing
`bool TryGet(AudioClip, out MusicMetadata)` over a `Dictionary<AudioClip, MusicMetadata>` built
lazily on first lookup — a library with no consumer in the scene costs nothing, and one lookup
happens per track start. Duplicate clips keep the first entry (`TryAdd`) rather than throwing: two
rows for one song is an authoring slip whose only real consequence is that one row is ignored.
`OnValidate` drops the cached index so an edit is visible without a reload.

The asset lives at `Assets/Resources/Data/MusicMetadataLibrary.asset` and is reached through a
`[SerializeField]` on `SoundManager`, exposed as `MusicMetadata` — scene-wired, **not**
`Resources.Load`, matching `AmbienceDatabase`. Null is a supported state.

### 6.2 Authoring

The Sound Editor's Music tab carries a **Song Metadata** section inside the Global scope — the
library is project-level content like the biome share and the music trim beside it. Rows are drawn
from a `SerializedObject`, so undo and the window's dirty/save flow work unchanged.

**Sync from pools** appends a row for every clip offered by the global pool or any biome pool that
has no entry, prefilling `artist` from `CreditsDatabase`'s folder-scoped `author` by matching the
clip's asset path against each entry's `projectFiles`. It is append-only: an existing row may carry
hand-written text. Title is left blank rather than seeded with the clip name, because the runtime
already falls back to exactly that — seeding it would turn a later rename into stale text.

### 6.3 The trigger seam

`MusicScheduler` exposes `public event Action<AudioClip> TrackStarted`, raised in `StartPending()`
immediately after `_source.Play()` — the single point where a clip becomes audible, so scheduled
picks, `/music next` and `/music play <name>` are covered by one raise.

The payload is the **clip**, not the `MusicTrack`: a subscriber wants to identify the song, and the
entry's weight and environment describe the pool it was drawn from rather than the song itself.

`NowPlayingToastPresenter` subscribes in `Start` (after every `Awake`, so
`MusicScheduler.Instance` is assigned) and unsubscribes in `OnDestroy`. It resolves the metadata,
falls back to `clip.name` at every miss — no library, no entry, blank title — and raises a card
prefixed `♪ ` with a 6 s dwell.

### 6.4 Setting

`showNowPlayingToasts` is a `public bool` on `Settings` with
`[SettingField(SettingsTab.Audio, Label = "Now Playing Toasts", Order = 6)]`, landing after the six
volume sliders at `Order` 0–5. The presenter reads it at raise time.

---

## 7. Verification

### 7.1 `/toast`

`/toast [count] [anchor] [variant]` raises test cards; `/toast glyphs` raises the icon-glyph
shortlist. Anchor and variant are both bare words, so each token is offered to both parsers and typing
order does not matter. `MAX_COUNT` is `ToastManager.AnchorCapacity`, so the reply cannot claim to
have raised cards the manager dropped.

**The dwell schedule is deliberately non-monotonic.** `ExpiryRank` swaps the first two ranks so the
*second* card expires first. Dwells that simply grow with the index expire cards in the order they
were raised, making the departing card always the **top** of the stack — so the mid-stack case, the
one thing this command exists to demonstrate, never occurs at any count. Do not simplify it back.

Registered through `ConsoleCommandInstaller.RegisterAll`; `InstalledCommandCount` is **18**, asserted
in three places by the Command Console suite's B32 count-floor.

### 7.2 What is and is not covered

**No validation suite reaches `ToastManager` or `ToastCard`.** Stacking is a Unity layout-system
outcome, and a suite asserting it would be testing `VerticalLayoutGroup`. The behavioural gates are
in game: `/toast 5` for mid-stack gap closure, `/toast 3 <variant>` sequences for per-variant
styling and pooled-card reset, and `/music play <name>` for the end-to-end music path.

The one automated check is `Every Pooled Music Clip Has A Metadata Entry` in the Sound Engine suite
(79 baselines). It finds the library **by type, not by path** — a path lookup returns null when the
asset moves, and this scenario reads null as "no library authored", which would silently turn it into
a vacuous pass. A missing library is a pass by design; an incomplete one is a failure.

**`Validate Sound Engine` cannot observe the scheduler.** Every music scenario exercises the pure
`MusicResolution` layer; none touches `MusicScheduler`. Keeping it green is a regression guard on the
rest of the sound engine, never a verification of the trigger seam.

---

## 8. Known limitations

- **A card over a bounded blurred panel still paints un-dimmed world.** Suppression is keyed to
  full-screen menus, so a bottom-left toast raised while the console is open is not covered. This is
  the blur system's inability to stack panels, not a policy gap here — see
  `UI_BLUR_BACKDROP_SYSTEM.md` §8, where the real fix (a second capture point) is tracked.
- **The enter/exit fade briefly bleeds sharp screen.** `CanvasGroup.alpha` sweeps during the ~0.22 s
  and ~0.3 s transitions, and alpha *is* sharpness bleed for a blurred panel (blur doc §4.1). No tint
  compensates; removing the fade is the only alternative.
- **A toast can cover the F3 performance panel's top-right corner.** Accepted.
- **No authored cover art.** The field, the slot and the null-collapse behaviour all ship; no sprites
  are authored. Everything a `Resources/` asset references is force-included in every build, so cover
  sprites want an import size budget when they are added.
- **No main-menu host.** `MainMenu.unity` carries no `SoundManager`, and `WorldUIManager` spawns the
  toast host, so nothing raises toasts there.
- **No achievement variant.** Deferred until something raises one.
- **`Info` cards do not match the project's frost.** Their blur tint resolves to `0.606` grey where
  every other frosted surface uses `0.415` (§5). Nothing is visibly wrong, but the "neutral" tint the
  code names is never what renders. Making `Info` bypass the lerp would align it exactly.
- **Never measured.** No benchmark has been run against this system.

---

## 9. Rejected alternatives

| Alternative | Why rejected | Date |
|-------------|--------------|------|
| `title`/`artist`/`cover` fields on `MusicTrack` | The same clip appears in the global pool and any number of biome pools, so metadata would be authored once per appearance and drift; it also mixes display data into a scheduling struct and widens a struct serialized inside `AmbienceDatabase.asset` and every biome asset. | 2026-09-02 |
| Parsing `"Artist - Title"` from the clip filename | Makes filenames a load-bearing contract, breaks on rename or on a title containing a dash, and offers no path to cover art. | 2026-09-02 |
| Keying metadata by clip **name** | `clip.name` is already the matching key for `/music play`; a rename would silently orphan the entry. An object reference survives renames and moves. | 2026-09-02 |
| A `ToastCard.prefab` | Needs a scene-wired manager reference and drags prefab/`.meta` churn into every visual tweak. | 2026-09-02 |
| Suppressing toasts entirely while any UI is open | A surface that hides whenever a menu opens is not a notification surface. Made safe by non-interactive cards instead. | 2026-09-02 |
| Offsetting the stack below the F3 panel | Same reasoning; the overlap is accepted. | 2026-09-02 |
| No blur on the card at all | The compositing constraint is real but the all-or-nothing conclusion was wrong: frosted glass is the intended treatment for every UI surface here, and a state-dependent fallback satisfies both. | 2026-09-02 |
| Suppressing the backdrop on `WorldUIManager.InUI` | Also true for the console and the creative inventory, neither of which is full-screen nor near the default anchor — it cost the frost and bought nothing. Narrowed to `IsPauseMenuOpen`. | 2026-09-02 |
| Hand-rolled stack offset math | `VerticalLayoutGroup` + `ContentSizeFitter` handles non-overlap, variable card heights and mid-stack gap closure for free; hand-rolled math would re-derive all three and get wrapped titles wrong. | 2026-09-02 |
| A `static TrackStarted` event | Would need its own domain-reload handling for the subscriber list. | 2026-09-02 |
| `event Action<MusicTrack> TrackStarted` | Would require widening the scheduler's pending state, rippling into `QueueTrack`, `ForcePick`, `DiagPendingTrack` and `ForceTrack` — which holds no `MusicTrack` and would have had to fabricate a weight and environment — for fields no subscriber reads. | 2026-09-02 |
| Moving the flat-fallback policy into `RuntimeUIFactory` | The factory is a stateless builder that deliberately holds no policy; the fallback needs per-frame state and a `WorldUIManager` dependency the factory must not have (it also serves scenes with no `WorldUIManager`). Extract to a `MonoBehaviour` if a second consumer appears. | 2026-09-02 |
| A per-variant style entry guarded by a test | Made unnecessary by a total switch with an Info default — there is no missing-entry state to catch. | 2026-09-02 |

---

## Document History

* **v1.0** - Promoted from `Design/TOAST_NOTIFICATION_SYSTEM.md` (v1.4) on TN-9's in-game
  confirmation. Every claim re-verified against code at `c2694593`; constants extracted from source.
  Phase structure removed and merged into current-state sections; the design's superseded text — the
  blur ban, the `Action<MusicTrack>` payload, the `InUI` suppression condition and the "one visual
  style" non-goal — is described only as it now stands, with each supersession recorded in §9.

---

**Last Updated:** 2026-09-02  
**Next Review:** when a second consumer, cover art, or an achievement variant lands
