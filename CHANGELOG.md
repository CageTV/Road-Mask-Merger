# Road Mask Merger — Changelog

## v1.3.0 — 2026-09-15

**Fixed: stopped writing a pointless null water record into cells that have no water at all.** Found via
a user's own xEdit screenshot: this tool's water-preservation logic (added to stop it from silently
DELETING real water) was assigning the water field unconditionally, even for cells with no water — this
forced an explicit "no water" record into the output where no water record existed at all before,
harmless in-game but pure noise that then propagated into every other tool built on top of the same
cell. Now only writes the water field when the cell genuinely has water.

**New: automatic ESL flagging.** The generated patch is now checked for ESL eligibility every run (same
logic as SSEEdit's own "Find ESP plugins which could be turned into ESL" script) and automatically
flagged as an ESL if it qualifies — this tool's output almost never adds brand-new records, so it's
eligible essentially every time.

**New: a `log.txt` and `settings-used.json` are now written into the output folder alongside the
generated plugin**, matching the other tools in this family — a portable record of exactly what
happened and what settings produced it, without having to copy text out of the app's log box.

## v1.2.0 — 2026-09-14

**Fixed a real gap found via a user's own xEdit screenshot**: this tool's road-height merge correctly
excludes the road-source plugin's own patch family from "the other mod's" landscape data (needed so
height merging doesn't double-count the road mod's own edits) — but that same exclusion silently
dropped that patch's TEXTURE work too, even when it had nothing to do with roads. A real case: a
"Landscape Fixes for Grass Mods" patch for Northern Roads painted a grass-blend texture layer that
this tool's output plugin then completely lost, because texture data only ever came from "the other
mod," never from the road-source side. Fixed by additionally carrying forward the road-source
plugin's own texture layers into the merged output wherever the road-source plugin (or a genuine
patch of it) actually authored that specific texture — verified this is the road-source's own
texture by checking who originally DEFINED the texture record, not by any naming convention, so it
works correctly for whatever `--road-source=` plugin is configured, not just Northern Roads
specifically. Additive only: never overwrites or removes anything from the other mod's own texture
set. Verified against a real 1282-plugin profile: 833 cells merged, 1409 texture layers preserved
that would otherwise have been silently dropped.

## v1.1.0 — 2026-09-12

- Vortex and Direct-game-path modes are now actually wired up — the UI
  panels existed and could auto-detect/fill in a path, but "Generate Merge
  Plugin" hard-blocked both with a validation error regardless. Mirrors the
  sibling Landscape Seam Fixer tool: both point Mutagen straight at the
  Data folder, no MO2 profile parsing needed.
  (The console CLI is still MO2-only for now.)

## v1.0.0 — 2026-09-11

First public release.

- Merges a road-shaping mod's terrain into any other mod's landscape,
  restricted to a real road-network mask (ACMOS Road Generator, bundled),
  with region-growing to cover a road mod's full contiguous edit and a
  repair pass that keeps every write within the terrain format's limits.
- Cross-cell boundary continuity: the region-growing flood-fill runs across
  the whole worldspace at once, so two adjacent merged cells agree at their
  shared edge wherever the road mod has genuine data on both sides.
- Recognizes the road mod's own compatibility-patch family (e.g. "Northern
  Roads - Some Mod Patch.esp") and prefers a patch's data over the plain
  plugin wherever one exists — fixed a real, widespread case where the tool
  was reading road height from the wrong (less authoritative) source on
  hundreds of cells in testing.
- Fixed a data-loss bug where the tool was silently resetting a cell's
  water to blank on every cell it touched — water now always passes through
  unchanged from whichever cell is actually winning.
- Settings (paths, road-source plugin, worldspace, etc.) are now remembered
  between launches.
