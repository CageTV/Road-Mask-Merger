# Road Mask Merger — Changelog

## v1.3.3 — 2026-09-16

**Real bug found via the user's own xEdit screenshots**, comparing this tool's output against the
actual winning Northern Roads patch side by side at 3 random cells: v1.3.2's per-vertex texture
merge only copied a vertex's texture where the HEIGHT-based flood-fill (`RoadSourced`) also flagged
that exact vertex — but Northern Roads' own road-texture paint covers a much wider footprint than
the narrower set of vertices where height needed reconciling. Under that gate, only the sliver
where both footprints happened to overlap ever got copied — most of the real road texture was
silently dropped, which is what "not copying the winning layers" looked like in xEdit.

**Fix**: merge NR's (or its genuine patch's) own texture data wherever it actually painted
something, with no height-based gate — the trust boundary is the existing provenance check (is
this texture owned by the road-source plugin or a genuine patch of it), not `RoadSourced`.

Verified against the real live profile: merged texture footprint at the user's own reported cell
went from a sparse handful of positions to 50 real painted positions, matching the scale of the
actual winning patch. Still 855/855 cells merged clean, 0 skipped.

## v1.3.2 — 2026-09-16

**Real per-vertex road-texture merging, on top of the v1.3.1 baseline (v1.4.x is reverted — see
below).** The texture-preservation logic only ever added a Northern Roads texture layer when the
"Other" mod had *zero* trace of that exact texture anywhere in the whole quadrant — so the moment
Other's own data touched that texture ID anywhere in the quadrant (even one unrelated corner), NR's
actual road paint was skipped for the entire quadrant, including the vertices the road genuinely
runs through. User-reported: "1.3.1 is not doing the Northern Roads textures."

Rewritten to merge per-vertex using the exact same `RoadSourced` grid the height merge already
computes and trusts: wherever this run already decided NR's own height wins at a vertex, NR's own
real per-vertex texture opacity now wins there too, merged into the matching quadrant/texture layer
(creating it if absent, respecting the existing 7-layer-per-quadrant cap) instead of being skipped
outright. Untouched vertices keep Other's own alpha data exactly as before — still additive/
selective, never a wholesale quadrant repaint, never touches Base layers.

Verified against the real live profile before release: 1,379 Northern Roads texture layers merged
across 647 of 855 cells this run touches (up from the old presence-only check). Confirmed via a
byte-level check that a cell showing 0 merged layers (ChillfurrowFarmEdge) correctly has no
available road-texture data to merge — both the road-source patch and plain Northern Roads.esp
carry only vanilla Skyrim.esm textures there, byte-identical; Northern Roads never painted a road
texture at that specific cell, so there was nothing to merge.

**Note on v1.4.0/v1.4.1**: those versions (Vortex/Direct-mode dispatch fix, tile-load crash fix,
texture-foundation-from-genuine-patch fix, and the boundary-residual auto-snap feature) were
reverted after the boundary-residual auto-snap caused a real in-game regression (dropped/pit
quadrants at multiple cells) that a follow-up patch (v1.4.1) did not fully resolve. This release is
built directly on the v1.3.1 baseline, not on v1.4.x. The three unrelated fixes from v1.4.0 are not
included here and would need to be re-applied separately if wanted.

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
