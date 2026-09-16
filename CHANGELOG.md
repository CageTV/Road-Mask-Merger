# Road Mask Merger — Changelog

## v1.4.1 — 2026-09-16

**Fixed a real in-game regression introduced by v1.4.0's own boundary-residual auto-repair**,
confirmed via user screenshots at multiple cells (e.g. ChillfurrowFarmEdge 7,-4 and
RedoransRetreatExterior -4,1, both showing this tool's own output as the last plugin to touch the
Landscape) plus the in-game debug HUD naming this tool. `ReconcileEdge`'s "safe" snap only ever
wrote the single boundary vertex row/column to match the neighboring cell, leaving the very next
row completely untouched - trading a cross-cell seam for a brand-new, un-tapered intra-cell cliff
of up to 96 units over one 128-unit vertex step. Visually: a sharp-walled pit or ledge running
along a cell/quadrant edge, often right next to water. Same failure class this tool's own sibling
apps (PatchForeman's `BoundaryRepair`, Landscape Seam Fixer's small-seam-repair) already hit and
had to disable the same night - a hard snap with no interior taper always manufactures a new step
somewhere else instead of actually closing the seam.

**Fix**: the same per-vertex edge correction is now faded linearly to zero over 8 vertices moving
inward from the edge, instead of applied only to the single boundary row. Spreading the correction
across several vertices interacts with VHGT's cumulative per-row delta encoding (each step rounds
to the nearest 8 units, and rounding compounds along the chain during decode) enough to trip the
existing round-trip safety check's 10-unit tolerance on most real boundaries - that tolerance was
tuned for ordinary two-mod Offset misalignment noise (~3 units typical), not a deliberate
multi-vertex taper, so this repair path now verifies against its own, still-conservative 50-unit
tolerance (measured worst case on the real list: 46 units; a genuinely broken encode shows errors
in the hundreds to thousands).

**Verified against the real live profile before release**: all 89 eligible residual boundaries
(8-96 unit band, both sides rewritten by this run) now snap cleanly with 0 round-trip rejections,
up from 19 successful / 71 safely-skipped under the same fix with the original 10-unit tolerance.
Both exact regression cells from the user's screenshots ((6,-4)|(7,-4)|(8,-4) and (-4,1)|(-3,1))
now get a properly tapered correction instead of either the old un-tapered pit or a silent skip.

## v1.4.0 — 2026-09-16

**Fixed: Vortex and Direct game-path modes were silently hard-blocked again**, despite v1.1.0's
changelog entry claiming this was already fixed and despite both UI panels and the backend
(`RunForDirectDataFolder`) genuinely working. The one piece that was never actually wired up was
the "Generate Merge Plugin" button's own dispatch logic - it still showed a hard validation error
for both modes regardless of what was filled in. Reported by a user via Nexus after the v1.3.x
release; confirmed as a real gap in `MainWindow.xaml.cs`, not a packaging mistake. Both modes now
call straight through to `RunForDirectDataFolder`, matching the sibling Landscape Seam Fixer tool's
own equivalent code path.

**Fixed a real texture-drop bug found via a user's own hand-authored compatibility patch**: when a
genuine patch of the road-source plugin exists for a cell, this tool was still building the texture
stack from "Other" (deliberately excluding the patch) and only trying to bolt the patch's road
overlays on top afterward - which regularly failed outright once "Other" had already filled a
quadrant to the engine's 7-layer cap with its own, unrelated texture layers. Confirmed on the real
list: 129 "could not preserve ... at the 7-layer cap" drops in one run, including 3 of 5 road-texture
layers missing entirely in one reported cell. Fixed by using the genuine patch's own Landscape as
the texture/height foundation instead of rebuilding from "Other" and patching fragments on top -
verified byte-identical to a user's own trusted hand-authored patch after the fix.

**New: cross-cell boundary-residual detection.** The existing `[BOUNDARY]` check only ever flagged a
shared edge if THIS merge made it worse than it already was - a huge, genuinely pre-existing mismatch
between two unrelated mods (confirmed real case: 190 units between QuaintSkyrimFarms.esp and a CC
Tundra Homestead patch) shipped completely unflagged, even though it's a visible in-game seam. A
second, independent check now reports any edge left with more than 96 units of mismatch in the final
output as `[BOUNDARY RESIDUAL]`, regardless of who caused it, restricted to pairs this run actually
rewrote on both sides (so it doesn't flood the log with ordinary steep vanilla terrain against cells
this tool never touched).

**New: safe automatic repair for small residual boundaries.** Where two cells this run rewrote still
disagree at their shared edge by more than 8 but no more than 96 units, the lower-confidence side
(fewer road-sourced vertices) is snapped to match its neighbor - round-trip encode verified before
writing, same safety net used everywhere else in this tool. Closed 66 real seams on the real list in
testing. Anything larger is deliberately left alone and reported instead, to avoid the "floating
terrain chunk" regression class this same project hit once already when a repair pass didn't respect
a strict magnitude cap.

**Fixed a crash that could silently abort a full-map run partway through.** A single road-mask tile
that fails to load via `System.Drawing.Bitmap` (confirmed as GDI+ resource exhaustion after loading
100+ tiles in one process, not file corruption - every tile loads fine individually) used to take
down the entire generation run with no output at all. A failed tile load is now treated as "no road
data here" (same as a genuinely missing tile) with a one-time warning, so the run completes instead.

## v1.3.1 — 2026-09-15

**Fixed a real seam/height-drop bug, reported by a user with a real hand-authored compatibility
patch.** This tool never actually wired up its own masters-based "is this a genuine patch of my
road-source plugin?" detection (a setup call present in the sibling tools but missing here) - so a
hand-authored patch named the "wrong" way around (patched-mod-first, e.g.
`SomeMod - Northern Roads.esp` instead of `Northern Roads - SomeMod.esp`) was invisible to this tool
as a genuine Northern Roads patch, even though it correctly lists Northern Roads as a literal ESP
master. The practical effect: the tool sourced road height from a completely unrelated Northern Roads
patch instead of the user's own carefully hand-blended one, producing real seams/bumps on the road and
height drops exactly where the user had manually reconciled the two mods. Confirmed at the byte level
via a per-vertex height diagnostic before fixing.

**Also fixed**: this tool's "what wins here besides the road" resolution had no exclusion for this
toolkit's OWN other sibling tools' output plugins (only its own name) - so a stale prior run of one of
them could get picked up as the merge baseline instead of the real underlying mod data.

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
