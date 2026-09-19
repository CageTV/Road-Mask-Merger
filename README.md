# Road Mask Merger

**Current version: 1.4.3** — see [CHANGELOG.md](CHANGELOG.md) for what's new.

A standalone tool for Skyrim Special Edition / Anniversary Edition that
auto-generates a compatibility patch between a road-shaping mod (Northern
Roads by default) and *any other mod* — no hand-authored patch required,
and no Creation Kit work.

It works entirely offline against your mod manager's own config files — it
does **not** need MO2 or Vortex running.

## What it does

Normally, fixing a road mod's terrain conflict with another landscaping mod
means hand-building a compatibility patch for that specific pair — a
different one for every mod the road mod might collide with. This tool
generates that patch automatically, for any mod pair, using a real
road-network map instead of guesswork:

1. **[ACMOS Road Generator](https://www.nexusmods.com/skyrimspecialedition/mods/79205)'s** own road-mask images (bundled with this tool) mark exactly
   where a road or path runs, at a finer resolution than the game's own
   terrain grid.
2. For every cell where the road-shaping plugin **genuinely** edited the
   terrain (compared against vanilla — an untouched copy doesn't count), the
   tool merges the road's own height data into whatever mod currently wins
   that cell, restricted to the road mask's footprint.
3. A **region-growing** pass extends that footprint to cover the road mod's
   *entire* contiguous edit, not just the pixels the mask happens to mark —
   a mod like Northern Roads sometimes regrades a whole cell around a
   junction, not just a road-width strip, and the mask alone can miss most
   of it.
4. Where merging the two sources would exceed what Skyrim's terrain format
   can actually encode in one step, a repair pass grows the road's footprint
   further rather than ever write a broken/clamped step — the height data
   always leans toward the road mod when the two truly can't be reconciled.
5. **Recognizes the road mod's own compatibility patches** (e.g. "Northern
   Roads - Some Other Mod Patch.esp") and prefers a patch's data over the
   plain road plugin wherever one exists, the same "a patch beats its base"
   rule Landscape Seam Fixer already uses.
6. Everything else about the cell — water, placed objects — is carried
   forward completely unchanged from whichever mod already owned it.
   Texture layers mostly work this way too, with one narrow exception (v1.2.0):
   the road-source plugin's *own* texture work (whatever it or a genuine
   patch of it actually painted — checked by who defined the texture
   record, not by name) is additionally preserved wherever the merge would
   otherwise have silently dropped it.

## What it doesn't (yet) do

- **General texture blending.** Beyond preserving the road-source plugin's
  own texture layers (see above), the road mask footprint isn't used to
  blend textures the way it's used for height — a cell with correctly
  merged terrain can still show the *other* mod's ground texture underneath
  it wherever the road source didn't paint anything there itself.
- Cross-cell boundary continuity is handled where both neighboring cells
  have genuine road-mod data to agree on; where the road mod has *no* data
  at all on one side of a boundary, a seam can still show there — there's
  no data to safely extend into without inventing terrain.

## Requirements

- Windows
- To just **run** the pre-built release: nothing extra — self-contained,
  bundles its own .NET runtime and the ACMOS road-mask assets.
- To **build from source**: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

## Building from source

```
git clone <this repo's URL>
cd "Road Mask Merger"
dotnet build
```

Publish the desktop UI as a standalone folder:

```
dotnet publish RoadMaskMerger.UI -c Release -r win-x64 --self-contained true -o RoadMaskMerger.UI/publish
```

## Usage — desktop app (recommended)

1. Launch `RoadMaskMerger.UI.exe`.
2. Pick how your mods are managed:
   - **Mod Organizer 2** — point it at your MO2 instance folder and game
     Data folder.
   - **Vortex** — point it at your game's Data folder (this is where Vortex
     deploys mods by default), or click "Auto-detect from Vortex."
   - **Direct game path** — no mod manager; just your game's Data folder.
3. Fill in the **road-source plugin** (defaults to "Northern Roads.esp") and
   the **worldspace** (defaults to "Tamriel").
4. The bundled ACMOS road-mask folder is used by default — only change it if
   you have your own, updated copy.
5. Only tick **"Paths Only"** if your road-source plugin's real edits are
   themselves scoped to footpaths — pairing it with a full road-carving mod
   produces a patchwork of merged/un-merged cells (see the in-app warning).
6. Click **Generate Merge Plugin**. Review the log — especially any
   `[BOUNDARY]` lines — before installing.

## Usage — command line

```
RoadMaskMerger.exe --mo2 <instancePath> <profileName> [gameDataPath]
    [--road-source="Northern Roads.esp"] [--worldspace="Tamriel"]
    [--acmos-roads="<path>"] [--paths-only]
```

## Project layout

- `SeamFinder.Core/` — shared library: only the pieces this tool needs
  (`Mo2Resolver`, `HeightmapDecoder`, `TrustResolver`)
- `RoadMaskMerger/` — console CLI + the merge/repair logic + bundled ACMOS
  road-mask assets
- `RoadMaskMerger.UI/` — WPF desktop app

`SeamFinder.Core` here is a trimmed copy shared with three sibling tools
(Landscape Seam Fixer, Landscape Texture Fixer, Floating Object Fixer) that
live in their own separate repos.

**Third-party assets:** the bundled ACMOS road-mask images are licensed
CC BY-NC-SA by their own author — see `RoadMaskMerger/Assets/NOTICE.md`
for attribution. This project's own code is licensed CC BY-NC-SA too (see
below) — free to use and share, not for commercial resale.

## Contributing

Issues and PRs welcome, especially real in-game reports of a cell that
still shows a seam after merging (a screenshot plus the cell coordinates
from the console is the most useful bug report).

## License

CC BY-NC-SA 4.0 — see [LICENSE](LICENSE). Free to use, modify, and share
(with attribution and under the same license), but not for commercial
purposes — no selling this tool or a modified version of it. The bundled
ACMOS assets carry their own attribution notice under the same license
family — see `RoadMaskMerger/Assets/NOTICE.md`.
