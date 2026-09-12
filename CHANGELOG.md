# Road Mask Merger — Changelog

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
