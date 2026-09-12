# Third-party assets: ACMOS Road Generator road masks

The `ACMOS-roads/` folder in this directory is copied, unmodified, from
[ACMOS Road Generator](https://github.com/DoubleYouC/ACMOS-Road-Generator)
by **DoubleYouC** (also distributed on
[Nexus Mods, mod 79205](https://www.nexusmods.com/skyrimspecialedition/mods/79205)).

These PNG files are the road/path network overlay masks ACMOS ships to paint
onto xLODGen-generated terrain LOD tiles. This tool (RoadMaskMerger) reuses
them for an unrelated purpose the original author never intended: sampling
them as a world-space "is this location a road or path" lookup, to decide
where a road-shaping mod's own terrain height should be merged into another
mod's landscape. The files themselves are untouched - only how they're read
is different.

## License

Per ACMOS Road Generator's own README: **CC BY-NC-SA** (Creative Commons
Attribution-NonCommercial-ShareAlike). This means:

- **Attribution required** - this file is that attribution. Keep it
  alongside the assets; don't strip it out in a refactor.
- **NonCommercial only** - fine here; this whole toolkit is a personal,
  non-commercial project, never sold or monetized.
- **ShareAlike** - any redistribution of these files must carry the same
  CC BY-NC-SA terms. As of the 2026-09-12 GitHub release, this project's
  own C# code is licensed CC BY-NC-SA too (see the repo's own `LICENSE`),
  so the whole download - code and bundled assets alike - now shares one
  consistent license. This NOTICE.md still travels alongside
  `ACMOS-roads/` regardless, since it's the specific attribution ACMOS's
  own author is owed, distinct from this project's own copyright notice.

Full license text: https://creativecommons.org/licenses/by-nc-sa/4.0/
