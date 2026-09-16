// Prototype: auto-generates a "patch" that merges a road-shaping mod's
// (Northern Roads') own terrain height into ANY other mod's landscape,
// restricted to wherever ACMOS Road Generator's own road-network mask says
// a road/path actually runs - without a hand-authored compatibility patch
// for that specific mod pair ever existing. See NOTES.md for the full
// design writeup, the coordinate-calibration evidence, and WHY the 3-pass
// repair below exists (the VHGT delta-encoding step limit).
//
// NOT YET TESTED IN-GAME. Built 2026-09-10; user is standing up a separate,
// isolated MO2 install to validate this before it touches their real list.

using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using SeamFinder.Core;

namespace RoadMaskMerger;

public record RoadMergeResult(int CellsMerged, int CellsFellBackToFullRoad, int CellsSkipped, string OutputPath);

public static class RoadTerrainMerger
{
    // Comfortably under the true 127*8=1016 hard limit, leaving rounding
    // headroom for VhgtEncoder's /8-then-round step (see its own comments).
    const float MaxEncodableStep = 1000f;

    // Same verified-against-real-game-data value TextureLayerFixer.cs uses
    // (7 = 1 mandatory base layer + up to 6 alpha layers, the true observed
    // per-quadrant maximum) - reused here for the road-texture preservation block
    // below so this tool never tries to write more layers than the engine
    // actually supports.
    const int MaxLayersPerQuadrant = 7;

    // Originally set to 40 on the (wrong) assumption that a single-vertex-
    // wide repair front only needs ~33 iterations to cross a 33-wide grid.
    // Confirmed wrong on the very first live test (H:\TB_test, 2026-09-10):
    // fixing one violating pair can retroactively re-break an EARLIER pair
    // already validated as clean earlier in the SAME pass (repair mutates
    // merged33 in place while the scan is still in progress), so the true
    // worst case is bounded by the number of VERTICES that can flip to
    // road-sourced (1089), not the grid's linear width. Each pass is a
    // trivial ~1089-comparison scan, so there's no real cost to being
    // generous - 1100 gives headroom above the theoretical worst case
    // (a vertex only ever flips road-sourced once, never back, so the
    // process is still guaranteed to terminate).
    const int MaxRepairIterations = 1100;

    // Tolerance for VERIFYING an encode round-trip - deliberately NOT the
    // same as HeightmapDecoder.HeightsMatch's 0.5-unit epsilon, which is
    // built for a different question ("is this an untouched ITM copy of
    // vanilla") and is far too tight here. Merging two different mods'
    // heights means their Offsets rarely align to the same multiple of 8,
    // so a few units of rounding error at encode time is NORMAL and
    // harmless - confirmed via a synthetic self-test (~3.2 units on
    // realistic mismatched-offset data) BEFORE this was ever run against
    // real data. Using the tight ITM epsilon here was a real bug found on
    // the first live test: it rejected the large majority of legitimate
    // merges as "failed," not just genuinely broken ones. 10 units (a bit
    // more than one raw 8-unit VHGT step) comfortably covers normal
    // rounding while still catching a genuinely broken encode (which shows
    // up as an error of hundreds to thousands of units, not single digits).
    const float RoundTripToleranceUnits = 10f;

    public static RoadMergeResult RunForResolvedPlugins(
        List<Mo2Resolver.ResolvedPlugin> loadOrder,
        string outputPluginName, string outputDirectory,
        Action<string> log,
        string roadSourcePlugin,
        string acmosRoadsFolder,
        string worldspaceFilter,
        bool pathsOnly = false)
    {
        var mergedFolder = Path.Combine(Path.GetTempPath(), "RoadMaskMerger-" + Guid.NewGuid().ToString("N"));
        log($"Staging {loadOrder.Count} plugin files into {mergedFolder} ...");
        Mo2Resolver.MaterializeMergedFolder(loadOrder, mergedFolder);
        try
        {
            var modKeys = loadOrder.Select(p => ModKey.FromFileName(p.FileName)).ToArray();
            using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
                .Create(GameRelease.SkyrimSE)
                .WithLoadOrder(modKeys)
                .WithTargetDataFolder(mergedFolder)
                .Build();
            var priorityIndex = modKeys.Select((k, idx) => (k, idx)).ToDictionary(x => x.k, x => x.idx);
            // FIXED 2026-09-15 (real user-reported bug): this call was
            // missing entirely, unlike SeamFixer.cs/TextureLayerFixer.cs
            // which both set it - so TrustResolver.IsPatchOfTrustedBase's
            // masters-based detection path (HasMasterRelationship) could
            // never fire here, only the "<stem> -" naming-convention check.
            // A real hand-authored patch named the OTHER way around (e.g.
            // "UniqueLocationsRiverwood - Northern Roads.esp", prefixed by
            // what it patches rather than by what it's a patch OF) was
            // therefore invisible to this tool as a genuine Northern Roads
            // patch, even though it has Northern Roads.esp as a literal
            // master - confirmed via a per-vertex height diagnostic showing
            // this tool sourced road height from a DIFFERENT, unrelated NR
            // patch instead of the user's own carefully hand-blended one.
            TrustResolver.SetMastersContext(BuildMastersByPlugin(env.LoadOrder.ListedOrder), outputPluginName);
            return GenerateCore(env.LinkCache, priorityIndex, mergedFolder, outputPluginName, outputDirectory,
                log, roadSourcePlugin, acmosRoadsFolder, worldspaceFilter, pathsOnly);
        }
        finally
        {
            try { Directory.Delete(mergedFolder, recursive: true); }
            catch (Exception ex) { log($"(could not clean up temp folder {mergedFolder}: {ex.Message})"); }
        }
    }

    // Vortex (default hardlink-deployment layout - the Data folder itself
    // already reflects the merged load order, no separate resolution step
    // needed) and Direct-game-path both land here: no MO2 profile to parse,
    // just point Mutagen straight at whatever Data folder is already there
    // and let it resolve plugins.txt itself. Mirrors
    // SeamFixer.GenerateFixPluginForDirectDataFolder in the sibling
    // Landscape Seam Fixer tool - same reasoning, same shape. Ported from
    // the GitHub packaging repo's own v1.1.0 (2026-09-12, "Wire up Vortex
    // and Direct game-path modes") - built directly against that repo, not
    // originally present here, merged back in 2026-09-14 so this dev tree
    // stays the single source of truth going forward.
    public static RoadMergeResult RunForDirectDataFolder(
        string dataFolderPath,
        string outputPluginName, string outputDirectory,
        Action<string> log,
        string roadSourcePlugin,
        string acmosRoadsFolder,
        string worldspaceFilter,
        bool pathsOnly = false)
    {
        using var env = GameEnvironmentBuilder<ISkyrimMod, ISkyrimModGetter>
            .Create(GameRelease.SkyrimSE)
            .WithTargetDataFolder(dataFolderPath)
            .Build();

        var priorityIndex = env.LoadOrder.ListedOrder
            .Select((listing, idx) => (listing.ModKey, idx))
            .ToDictionary(x => x.ModKey, x => x.idx);

        TrustResolver.SetMastersContext(BuildMastersByPlugin(env.LoadOrder.ListedOrder), outputPluginName);
        return GenerateCore(env.LinkCache, priorityIndex, dataFolderPath, outputPluginName, outputDirectory,
            log, roadSourcePlugin, acmosRoadsFolder, worldspaceFilter, pathsOnly);
    }

    // ESP masters, keyed by filename (not ModKey - matches how every other
    // trust check in this file compares plugins) - the data-driven signal
    // HasMasterRelationship uses instead of guessing prefixes. Mirrors
    // SeamFixer.cs's own BuildMastersByPlugin exactly.
    static Dictionary<string, HashSet<string>> BuildMastersByPlugin(IEnumerable<Mutagen.Bethesda.Plugins.Order.IModListingGetter<ISkyrimModGetter>> listedOrder)
    {
        var result = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var listing in listedOrder)
        {
            if (listing.Mod is null) continue;
            result[listing.ModKey.FileName] = listing.Mod.ModHeader.MasterReferences
                .Select(m => m.Master.FileName.String)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        return result;
    }

    // Working state for one cell that Northern Roads genuinely edited
    // SOMEWHERE (the necessary condition for it to ever become road-
    // sourced - see Pass A below). Kept as a mutable class, not a record:
    // Pass B's flood-fill mutates RoadSourced in place as growth reaches a
    // vertex, sometimes long after the cell was first collected in Pass A,
    // and sometimes from a neighboring cell's own BFS step (see GrowInto).
    class EligibleCell
    {
        public required IModContext<ISkyrimMod, ISkyrimModGetter, Cell, ICellGetter> Context { get; init; }
        public required string WsName { get; init; }
        public required float[,] NrHeights { get; init; }
        public float[,]? OtherHeights { get; init; }
        public ILandscapeGetter? OtherLandscape { get; init; }
        public ModKey OtherModKey { get; init; }
        // The actual road-source-(or-its-patch) Landscape record height was
        // sourced from - kept (not just the decoded NrHeights float array) so
        // the merge step can also read its OWN texture layers, see the
        // road-texture preservation block in GenerateCore below.
        public required ILandscapeGetter NrLandscape { get; init; }
        public required bool[,] NrEdited { get; init; }
        public required bool[,] RoadSourced { get; init; }
    }

    static RoadMergeResult GenerateCore(
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string dataFolderForWrite,
        string outputPluginName,
        string outputDirectory,
        Action<string> log,
        string roadSourcePlugin,
        string acmosRoadsFolder,
        string worldspaceFilter,
        bool pathsOnly)
    {
        using var roadMask = new RoadMaskSampler(acmosRoadsFolder, worldspaceFilter, pathsOnly);
        if (!roadMask.IsAvailable)
        {
            // Throwing here (rather than returning a "successful" empty
            // result) matters: a caller that only checks "is the result
            // null" would otherwise sail past this into code that assumes
            // the output folder was actually created - which it wasn't,
            // since we never got as far as the Directory.CreateDirectory
            // call below. Confirmed as a real bug on RoadMaskMerger.UI's
            // first real use (2026-09-10): this path silently produced a
            // result object, which then let EnsureMo2MetaIni try to write
            // meta.ini into a folder that was never created, surfacing as a
            // confusing "Could not find a part of the path" crash instead
            // of the actual, already-logged reason.
            var expectedPath = Path.Combine(acmosRoadsFolder, pathsOnly ? "Paths Only" : "Roads", worldspaceFilter.ToLowerInvariant());
            throw new InvalidOperationException(
                $"No ACMOS road mask folder found for worldspace '{worldspaceFilter}' - looked under:\n{expectedPath}\n\n" +
                "Check the ACMOS roads folder path and the worldspace name (case-insensitive, but must match a folder ACMOS actually generated - e.g. \"Tamriel\").");
        }

        var outputModKey = ModKey.FromNameAndExtension(outputPluginName);
        var patchMod = new SkyrimMod(outputModKey, SkyrimRelease.SkyrimSE);
        int merged = 0, fellBack = 0, skipped = 0, totalRoadTextureLayersPreserved = 0;

        // Populated for every cell in the target worldspace (merged or not)
        // so the cross-cell continuity check after the main loop can look up
        // ANY neighbor, not just ones this run happened to touch.
        var cellByCoord = new Dictionary<(int X, int Y), FormKey>();
        // Populated only for cells actually merged - "Before" is what would
        // have won without the road-source plugin at all (pre-merge),
        // "After" is the final written heights (post-merge/repair) - used to
        // tell whether THIS merge made a boundary worse, not just whether a
        // boundary mismatch exists at all (which could be a pre-existing,
        // unrelated seam this tool had nothing to do with).
        var mergedCellData = new Dictionary<(int X, int Y), (float[,] Before, float[,] After)>();

        // ---- Pass A: collect every cell in the target worldspace and work
        // out which ones Northern Roads genuinely edited SOMEWHERE (its own
        // Landscape record differs from vanilla at at least one vertex).
        // This used to happen inline, one cell at a time, immediately
        // followed by that SAME cell's merge/repair/write in one big loop -
        // which meant the region-growing flood-fill could never see past a
        // single cell's own 33x33 grid, and two independently-merged
        // neighbor cells could disagree at their shared edge (the v0
        // cross-cell boundary gap - see NOTES.md). Splitting collection out
        // into its own pass is what makes Pass B able to treat the WHOLE
        // worldspace as one connected vertex graph instead of 33x33 islands
        // - see Pass B's comment for why that's the actual fix.
        var eligibleCells = new Dictionary<(int X, int Y), EligibleCell>();
        foreach (var context in linkCache.WinningContextOverrides<Cell, ICellGetter>(linkCache))
        {
            var cell = context.Record;
            if (cell.Grid is null) continue;
            if (!context.TryGetParentSimpleContext<IWorldspaceGetter>(out var wsContext)) continue;
            var wsName = wsContext.Record.EditorID ?? wsContext.Record.FormKey.ToString();
            if (!wsName.Equals(worldspaceFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var coord = (cell.Grid.Point.X, cell.Grid.Point.Y);
            cellByCoord[coord] = cell.FormKey;

            // Find Northern Roads' own Landscape and vanilla's own Landscape
            // for this specific cell (both needed: NR's to source road
            // vertices from, vanilla's to prove NR's data here is a genuine
            // edit and not just an inert ITM carry-forward). Also finds the
            // HIGHEST-PRIORITY genuine patch of the road-source plugin (its
            // own "<RoadSource> - ..." compatibility-patch family) and
            // prefers it over the plain road-source plugin when one exists -
            // same "a patch beats its base, unconditionally" rule
            // SeamFixer.ResolveTrustedOnlyLandscape already uses, and for
            // the identical reason: a patch exists specifically to
            // reconcile the road mod against whatever it's patching, so its
            // road height is the more authoritative one where they overlap.
            // Confirmed as a REAL, previously-hidden bug 2026-09-11: this
            // tool was hardcoded to read road height from literally
            // "Northern Roads.esp", ignoring e.g. "Northern Roads -
            // Skyking's Granite Hill Patch.esp" entirely - producing a
            // genuine ~72-unit seam between the merged road strip and the
            // correctly-patched surrounding terrain in a real cell
            // (-13,-8). Previously masked by SeamFixer's own (blunter, now-
            // fixed) restoration of the patch's data over this tool's
            // output; visible once SeamFixer stopped touching
            // RoadMaskMerge.esp's cells (see TrustResolver.cs's sibling-
            // tool-trust comment for that separate, correct fix).
            ILandscapeGetter? nrLandscape = null;
            ILandscapeGetter? vanillaLandscape = null;
            ILandscapeGetter? nrPatchLandscape = null;
            ModKey nrPatchOwner = default;
            int nrPatchIndex = -1;
            foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cell.FormKey, ResolveTarget.Winner))
            {
                if (ctx.Record.Landscape is null) continue;
                string fileName = ctx.ModKey.FileName;
                if (fileName.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                    vanillaLandscape = ctx.Record.Landscape;
                if (fileName.Equals(roadSourcePlugin, StringComparison.OrdinalIgnoreCase))
                    nrLandscape = ctx.Record.Landscape;
                if (TrustResolver.IsPatchOfTrustedBase(fileName, roadSourcePlugin))
                {
                    var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
                    if (idx > nrPatchIndex)
                    {
                        nrPatchIndex = idx;
                        nrPatchLandscape = ctx.Record.Landscape;
                        nrPatchOwner = ctx.ModKey;
                    }
                }
            }
            if (nrPatchLandscape is not null)
            {
                nrLandscape = nrPatchLandscape;
                log($"  [{wsName}] ({cell.Grid.Point.X},{cell.Grid.Point.Y}): sourcing road height from {nrPatchOwner.FileName} (patch of {roadSourcePlugin}) instead of the plain plugin.");
            }
            if (nrLandscape?.VertexHeightMap is null) continue; // road-source plugin (or its patch family) doesn't touch this cell at all
            if (vanillaLandscape?.VertexHeightMap is null) continue; // no vanilla baseline to test genuineness against

            var nrHeights = HeightmapDecoder.DecodeHeights(nrLandscape.VertexHeightMap);
            var vanillaHeights = HeightmapDecoder.DecodeHeights(vanillaLandscape.VertexHeightMap);
            if (HeightmapDecoder.HeightsMatch(nrHeights, vanillaHeights)) continue; // NR's copy here is an unedited ITM - nothing to merge

            // Per-vertex "did NR genuinely edit this vertex" grid - drives
            // both the seed selection and the cross-cell growth in Pass B.
            var nrEdited = new bool[33, 33];
            bool anyEdited = false;
            for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 32; x++)
            {
                nrEdited[x, y] = Math.Abs(nrHeights[x, y] - vanillaHeights[x, y]) > 0.5f;
                anyEdited |= nrEdited[x, y];
            }
            if (!anyEdited) continue; // decoded arrays differ overall (HeightsMatch is whole-grid) but not at any single vertex past epsilon - nothing to seed from

            // Whatever mod currently wins this cell's terrain, EXCLUDING the
            // road-source plugin itself - the "everything except the road"
            // base this prototype is patching against. Also excludes THIS
            // tool's own output plugin name - a real bug class elsewhere in
            // this project (SeamFixer once mistook its own prior output for
            // ground truth): re-running against a profile that already has
            // an earlier RoadMaskMerge.esp installed must not let that old,
            // possibly-buggy output be picked up as the "other mod" base.
            var (otherLandscape, otherModKey) = ResolveWinningLandscapeExcluding(
                cell.FormKey, linkCache, priorityIndex, roadSourcePlugin, outputPluginName);
            float[,]? otherHeights = otherLandscape?.VertexHeightMap is null
                ? null // nothing else to merge against here - SeamFixer's own machinery is the right tool instead; still tracked as eligible so Pass B can grow THROUGH this cell into a neighbor that does have an "other" base
                : HeightmapDecoder.DecodeHeights(otherLandscape.VertexHeightMap);

            eligibleCells[coord] = new EligibleCell
            {
                Context = context,
                WsName = wsName,
                NrHeights = nrHeights,
                OtherHeights = otherHeights,
                OtherLandscape = otherLandscape,
                OtherModKey = otherModKey,
                NrLandscape = nrLandscape,
                NrEdited = nrEdited,
                RoadSourced = new bool[33, 33],
            };
        }

        // ---- Pass B: global region-growing (flood fill) across every
        // eligible cell in the worldspace AT ONCE. Seeds are unchanged from
        // the old single-cell version - a vertex only starts a fill if the
        // ACMOS mask confirms a real road AND Northern Roads genuinely
        // edited it (see the 2026-09-10 Helgen fix in NOTES.md for why
        // growth beyond the mask line is needed at all). What's new: when
        // the flood front reaches a cell's edge (a local coordinate steps
        // to -1 or 33), it now WRAPS into the mirrored vertex of the
        // neighboring cell via GrowInto - continuing the fill there if that
        // neighbor is ALSO eligible (Northern Roads genuinely edited that
        // exact vertex too) - instead of stopping dead at the cell boundary
        // the way the old per-cell BFS had to. Two adjacent merged cells now
        // necessarily AGREE at every shared vertex they both mark road-
        // sourced, because both are reading the exact same absolute height
        // out of Northern Roads' own Landscape record at that shared world
        // position - there's no opportunity for them to disagree, unlike the
        // old design where each cell's fill (and therefore its choice of NR
        // vs. "other mod" at that vertex) was decided in total isolation
        // from its neighbors. Where a boundary still can't be reconciled
        // (the OTHER side has no genuine NR edit at all to grow into), that
        // is now a real, irreducible limit - not a bug - and gets reported
        // by CheckCrossCellContinuity below, same as before.
        var seedQueue = new Queue<(int X, int Y, int Lx, int Ly)>();
        foreach (var (coord, ec) in eligibleCells)
        {
            var cellOriginX = coord.X * 4096f;
            var cellOriginY = coord.Y * 4096f;
            for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 32; x++)
            {
                if (!ec.NrEdited[x, y]) continue;
                var wx = cellOriginX + x * 128f;
                var wy = cellOriginY + y * 128f;
                if (roadMask.IsRoad(wx, wy) && !ec.RoadSourced[x, y])
                {
                    ec.RoadSourced[x, y] = true;
                    seedQueue.Enqueue((coord.X, coord.Y, x, y));
                }
            }
        }

        while (seedQueue.Count > 0)
        {
            var (cx, cy, lx, ly) = seedQueue.Dequeue();
            GrowInto(eligibleCells, seedQueue, cx, cy, lx - 1, ly);
            GrowInto(eligibleCells, seedQueue, cx, cy, lx + 1, ly);
            GrowInto(eligibleCells, seedQueue, cx, cy, lx, ly - 1);
            GrowInto(eligibleCells, seedQueue, cx, cy, lx, ly + 1);
        }

        // ---- Pass C: for every cell the flood-fill actually touched
        // (RoadSourced true anywhere), run the same 3-pass merge/repair/
        // encode/verify/write as the old single-cell version, unchanged -
        // within-cell VHGT step-limit repair is an orthogonal concern to
        // cross-cell agreement (Pass B already guarantees agreement at
        // shared vertices; this pass only has to keep each cell's OWN
        // encoding internally valid).
        foreach (var (coord, ec) in eligibleCells)
        {
            var roadVertexCount = 0;
            for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 32; x++)
                if (ec.RoadSourced[x, y]) roadVertexCount++;
            if (roadVertexCount == 0) continue; // fill never reached this cell, directly or by propagation from a neighbor

            if (ec.OtherHeights is null || ec.OtherLandscape is null)
            {
                log($"  [{ec.WsName}] ({coord.X},{coord.Y}): road-sourced by cross-cell propagation but this cell has no other-mod terrain to merge onto - skipped.");
                skipped++;
                continue;
            }

            // Pass 1: raw per-vertex merge.
            var merged33 = new float[33, 33];
            for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 32; x++)
                merged33[x, y] = ec.RoadSourced[x, y] ? ec.NrHeights[x, y] : ec.OtherHeights[x, y];

            // Passes 2..N: repair any adjacent-vertex step beyond the
            // encodable limit by growing the road-sourced footprint - i.e.
            // lean toward Northern Roads' own edits whenever the two
            // sources can't be reconciled cleanly. See NOTES.md for why
            // this direction, not the other one.
            bool clean = false;
            int iterationsUsed = 0;
            for (int iter = 0; iter < MaxRepairIterations && !clean; iter++)
            {
                iterationsUsed = iter + 1;
                clean = true;
                for (int y = 0; y <= 32; y++)
                for (int x = 0; x <= 32; x++)
                {
                    // Only a MIXED-source pair (one road-sourced, one not)
                    // can ever be a real problem - two vertices sourced from
                    // the SAME mod's own decoded data are mathematically
                    // guaranteed to already satisfy the format's true step
                    // limit (that's how they got encoded in the first
                    // place), even if the gap happens to sit between
                    // MaxEncodableStep and the true 1016-unit hard limit.
                    // Confirmed as a real bug on the first live test
                    // (H:\TB_test, 2026-09-10): checking same-sourced pairs
                    // too could flag an unfixable "violation" - both sides
                    // already resolved, nothing left to pull toward - which
                    // never converged no matter how many passes ran.
                    if (x > 0 && ec.RoadSourced[x, y] != ec.RoadSourced[x - 1, y]
                        && Math.Abs(merged33[x, y] - merged33[x - 1, y]) > MaxEncodableStep)
                    {
                        clean = false;
                        if (!ec.RoadSourced[x, y]) { merged33[x, y] = ec.NrHeights[x, y]; ec.RoadSourced[x, y] = true; }
                        else { merged33[x - 1, y] = ec.NrHeights[x - 1, y]; ec.RoadSourced[x - 1, y] = true; }
                    }
                    if (y > 0 && ec.RoadSourced[x, y] != ec.RoadSourced[x, y - 1]
                        && Math.Abs(merged33[x, y] - merged33[x, y - 1]) > MaxEncodableStep)
                    {
                        clean = false;
                        if (!ec.RoadSourced[x, y]) { merged33[x, y] = ec.NrHeights[x, y]; ec.RoadSourced[x, y] = true; }
                        else { merged33[x, y - 1] = ec.NrHeights[x, y - 1]; ec.RoadSourced[x, y - 1] = true; }
                    }
                }
            }

            bool fullRoadFallback = false;
            if (!clean)
            {
                // Should be unreachable given MaxRepairIterations' margin
                // above the grid's max possible propagation distance - if
                // this ever fires, it means something about the repair
                // logic's assumptions (see NOTES.md) doesn't hold and needs
                // investigating, not just papering over.
                log($"  [{ec.WsName}] ({coord.X},{coord.Y}): repair did not converge after {MaxRepairIterations} passes - " +
                    "falling back to full road-source terrain for this cell as the ultimate fail-safe.");
                merged33 = (float[,])ec.NrHeights.Clone();
                fullRoadFallback = true;
            }

            var (offset, deltas) = VhgtEncoder.Encode(merged33);
            var roundTrip = VhgtEncoder.DecodeForVerification(offset, deltas);
            var roundTripError = MaxAbsDiff(roundTrip, merged33);
            if (roundTripError > RoundTripToleranceUnits)
            {
                log($"  [{ec.WsName}] ({coord.X},{coord.Y}): SKIPPED - round-trip verification failed after encoding " +
                    $"(max error {roundTripError:F0} units, tolerance {RoundTripToleranceUnits:F0}). Investigate before trusting this cell's output.");
                skipped++;
                continue;
            }

            var writableCell = ec.Context.GetOrAddAsOverride(patchMod);
            foreach (var p in ec.Context.Record.Persistent) writableCell.Persistent.Add((IPlaced)p.DeepCopy());
            foreach (var t in ec.Context.Record.Temporary) writableCell.Temporary.Add((IPlaced)t.DeepCopy());

            // GetOrAddAsOverride only carries forward a Cell's simplest
            // fields - Water/WaterHeight/Flags come back at their DEFAULT
            // (empty) state, the exact same "silently reset" risk
            // Persistent/Temporary already needed explicit preservation
            // for, just for a different field group. Confirmed as a REAL,
            // serious bug 2026-09-11 (user report, cross-checked via
            // houseCARL): a cell with genuine cell-level water (Half Moon
            // Creek.esp's own creek, WaterHeight=1000, a real Water link)
            // had that water SILENTLY DELETED by this tool's own output -
            // nothing here ever copied it forward, so it fell back to the
            // Cell setter's blank default regardless of what was actually
            // winning before this patch existed. This tool only ever means
            // to change LAND height - water (and every other plain Cell
            // field) must always pass through completely unchanged from
            // whichever cell is ACTUALLY winning right now, the same
            // "preserve what we don't mean to touch" principle already
            // applied to Persistent/Temporary above.
            //
            // FIXED 2026-09-15 (real user report, confirmed via xEdit + houseCARL
            // on cell 007159 in the Reach): unconditionally assigning Water even
            // when the winning cell has none forces Mutagen's writer to emit an
            // explicit XCWT subrecord with a null FormID (visible in xEdit as
            // "NULL - Null Reference") where NO override before this one had that
            // subrecord at all - Skyrim.esm's own base record has no XCWT here
            // either. Functionally harmless (explicit-null and absent both mean
            // "no water"), but pure noise this tool has no reason to introduce,
            // and every downstream tool that builds on top of this cell (Texture
            // Fixer, PatchForeman) then carries the bogus subrecord forward too.
            // Only assign when the winning cell genuinely has a water link -
            // mirrors SeamFixer.cs's own water-restoration code, which already
            // gets this right (`if (genuineWater.Value.HasWaterLink) writableCell.Water = ...`).
            if (ec.Context.Record.Water.FormKeyNullable.HasValue)
                writableCell.Water = ec.Context.Record.Water.AsSetter().AsNullable();
            writableCell.WaterHeight = ec.Context.Record.WaterHeight;
            writableCell.Flags = ec.Context.Record.Flags;

            // Based on the OTHER mod's own Landscape record so its texture
            // layers/quadrant data carry forward untouched - only the
            // height grid itself is replaced. General texture-layer merging
            // along the road mask stays deliberately out of scope for this
            // prototype (see NOTES.md).
            var newLandscape = ec.OtherLandscape.DeepCopy();
            newLandscape.VertexHeightMap!.Offset = offset;
            for (int y = 0; y <= 32; y++)
            for (int x = 0; x <= 32; x++)
                newLandscape.VertexHeightMap!.HeightMap[x, y] = deltas[x, y];

            // FIXED 2026-09-14 (real user-found bug): "Other" is resolved by
            // ResolveWinningLandscapeExcluding, which - correctly, for the
            // HEIGHT merge above - excludes not just the plain road-source
            // plugin but any TrustResolver.IsPatchOfTrustedBase match too
            // (a pure "<road-source stem> -" filename-prefix test). That
            // exclusion has a side effect nothing here used to correct for:
            // an NR-family patch can ALSO carry texture-layer edits that
            // have nothing to do with roads (confirmed real case: "Northern
            // Roads - Landscape Fixes for Grass Mods patch.esp" painting a
            // COTN_LDirtDry alpha layer) - since this tool's texture data
            // comes ENTIRELY from "Other", never from the road-source side,
            // that layer silently vanished from every cell this tool
            // touched.
            //
            // GENERALIZED 2026-09-14 (same day, user request): the first cut
            // of this fix matched by a hardcoded "COTN" EditorID prefix -
            // Northern Roads' own naming convention, confirmed via houseCARL
            // against all 22 of its LTEX records, but useless for anyone
            // running this tool with a DIFFERENT `--road-source=` plugin
            // (already a fully generic setting for the height merge above).
            // Replaced with a PROVENANCE check instead of a naming
            // convention: preserve an alpha layer if its texture record was
            // ITSELF originally defined by the road-source plugin or a
            // genuine patch of it (same TrustResolver.IsPatchOfTrustedBase
            // family test the height side already uses) - this is not just
            // more general, it's more CORRECT than a prefix ever was: it
            // can't false-positive on an unrelated texture that happens to
            // share a naming convention, and doesn't silently do nothing for
            // a road mod that names its textures differently (or not at
            // all). Deliberately does NOT attempt the general (much riskier,
            // previously tried-and-reverted - see the 2026-09-14
            // TextureSeamFixer "untrusted-vs-untrusted" entry in NOTES.md)
            // job of merging arbitrary texture disagreements. Deliberately
            // narrow and ADDITIVE ONLY: only adds a layer "Other" doesn't
            // already carry for that (quadrant, texture) pair - never
            // overwrites or removes anything from Other's own texture set,
            // and never touches Base layers (swapping a whole quadrant's
            // base texture is a far bigger, riskier change than one accent
            // alpha layer, and not what "the road textures" refers to).
            int roadTextureLayersPreserved = 0;
            foreach (var nrLayer in ec.NrLandscape.Layers)
            {
                if (nrLayer is not IAlphaLayerGetter nrAlpha) continue;
                string textureOwner = nrAlpha.Header.Texture.FormKey.ModKey.FileName;
                var isRoadSourceOwnTexture = textureOwner.Equals(roadSourcePlugin, StringComparison.OrdinalIgnoreCase)
                    || TrustResolver.IsPatchOfTrustedBase(textureOwner, roadSourcePlugin);
                if (!isRoadSourceOwnTexture) continue;

                var alreadyPresent = newLandscape.Layers.Any(l =>
                    l.Header.Quadrant == nrAlpha.Header.Quadrant &&
                    l.Header.Texture.FormKey == nrAlpha.Header.Texture.FormKey);
                if (alreadyPresent) continue;

                var quadrantCount = newLandscape.Layers.Count(l => l.Header.Quadrant == nrAlpha.Header.Quadrant);
                if (quadrantCount >= MaxLayersPerQuadrant)
                {
                    log($"  [{ec.WsName}] ({coord.X},{coord.Y}): could not preserve {nrAlpha.Header.Texture.FormKey} in " +
                        $"{nrAlpha.Header.Quadrant} - quadrant already at the {MaxLayersPerQuadrant}-layer cap.");
                    continue;
                }

                var preservedAlphaData = new ExtendedList<AlphaLayerData>();
                foreach (var d in nrAlpha.AlphaLayerData)
                    preservedAlphaData.Add(new AlphaLayerData { Position = d.Position, Opacity = d.Opacity });

                newLandscape.Layers.Add(new AlphaLayer
                {
                    Header = new LayerHeader
                    {
                        Texture = new FormLink<ILandscapeTextureGetter>(nrAlpha.Header.Texture.FormKey),
                        Quadrant = nrAlpha.Header.Quadrant,
                        LayerNumber = (ushort)quadrantCount,
                    },
                    AlphaLayerData = preservedAlphaData,
                });
                roadTextureLayersPreserved++;
            }
            totalRoadTextureLayersPreserved += roadTextureLayersPreserved;

            writableCell.Landscape = newLandscape;

            log($"  [{ec.WsName}] ({coord.X},{coord.Y}): merged {ec.OtherModKey.FileName} + {roadSourcePlugin} " +
                $"({roadVertexCount}/1089 vertices road-sourced, {iterationsUsed} repair pass(es)" +
                $"{(roadTextureLayersPreserved > 0 ? $", {roadTextureLayersPreserved} {roadSourcePlugin} texture layer(s) preserved" : "")}" +
                $"{(fullRoadFallback ? ", FULL ROAD FALLBACK" : "")}).");
            merged++;
            if (fullRoadFallback) fellBack++;
            mergedCellData[coord] = (ec.OtherHeights, merged33);
        }

        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, outputPluginName);

        var eslResult = EslEligibility.CheckAndFlag(patchMod);
        log(eslResult.Summary);

        log($"Writing patch plugin to {outputPath} ...");
        SkyrimMod.WriteBuilder(SkyrimRelease.SkyrimSE)
            .ToPath(outputPath, fileSystem: null)
            .WithNoLoadOrder()
            .WithDataFolder(dataFolderForWrite)
            .WithAllParentMasters()
            .Write(patchMod);

        log($"Merged {merged} cell(s) ({fellBack} via full-road fallback), skipped {skipped}, " +
            $"{totalRoadTextureLayersPreserved} {roadSourcePlugin} texture layer(s) preserved that would otherwise have been dropped.");

        var worsenedBoundaries = CheckCrossCellContinuity(mergedCellData, cellByCoord, linkCache, priorityIndex, log);
        if (worsenedBoundaries == 0)
            log("Cross-cell boundary continuity: no boundary was made worse by this merge.");
        else
            log($"Cross-cell boundary continuity: {worsenedBoundaries} shared edge(s) got WORSE because of this merge - " +
                "see the [BOUNDARY] lines above for exactly which cells. Unlike the v0 prototype, this is no longer " +
                "expected for a boundary between two cells Northern Roads genuinely edited on both sides - the " +
                "region-growing flood-fill now crosses cell boundaries and forces agreement there (NOTES.md, \"Pass " +
                "B\"). A residual warning here means Northern Roads has NO genuine edit at all on the OTHER side of " +
                "that specific boundary, so there is no NR data left to safely extend into - review it in-game.");

        return new RoadMergeResult(merged, fellBack, skipped, outputPath);
    }

    // Extends the region-growing flood-fill in Pass B across a cell
    // boundary. Called with a local coordinate that may have stepped just
    // outside [0,32] (one BFS step in any direction from a valid vertex);
    // wraps it into the neighboring cell's mirrored vertex instead of
    // dropping it - the one behavioral change from the old within-cell-only
    // version. Growth still only ever follows Northern Roads' own genuine
    // edits (NrEdited), so it can never fabricate NR data in a cell NR never
    // touched - it can only reveal agreement that already exists in NR's own
    // (presumably internally consistent, shipped, working) Landscape data.
    static void GrowInto(
        Dictionary<(int X, int Y), EligibleCell> eligibleCells,
        Queue<(int X, int Y, int Lx, int Ly)> seedQueue,
        int cx, int cy, int lx, int ly)
    {
        if (lx < 0) { cx -= 1; lx = 32; }
        else if (lx > 32) { cx += 1; lx = 0; }
        if (ly < 0) { cy -= 1; ly = 32; }
        else if (ly > 32) { cy += 1; ly = 0; }

        if (!eligibleCells.TryGetValue((cx, cy), out var nec)) return; // neighbor cell has no genuine NR edit anywhere - nothing to extend into
        if (nec.RoadSourced[lx, ly] || !nec.NrEdited[lx, ly]) return; // already grown here, or NR never touched this exact vertex
        nec.RoadSourced[lx, ly] = true;
        seedQueue.Enqueue((cx, cy, lx, ly));
    }

    // Pass B's flood-fill forces agreement at any shared vertex BOTH
    // neighboring cells mark road-sourced, but a boundary can still get
    // worse where only ONE side has a genuine NR edit to grow into (see
    // GrowInto) - the remaining, now-irreducible v1 gap (was: every
    // boundary, independently repaired per cell, in the v0 prototype - see
    // NOTES.md). Only flags a boundary as a problem if THIS merge made it
    // WORSE than it already was pre-merge - a pre-existing mismatch this
    // tool had nothing to do with isn't this tool's fault to report.
    static int CheckCrossCellContinuity(
        Dictionary<(int X, int Y), (float[,] Before, float[,] After)> mergedCellData,
        Dictionary<(int X, int Y), FormKey> cellByCoord,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        Action<string> log)
    {
        const float WorseningToleranceUnits = 8f; // one raw VHGT step - anything smaller is encoding noise, not a real new seam
        int worsened = 0;
        var neighborHeightCache = new Dictionary<(int X, int Y), float[,]>();

        float[,]? GetActualHeights(int x, int y)
        {
            if (neighborHeightCache.TryGetValue((x, y), out var cached)) return cached;
            if (!cellByCoord.TryGetValue((x, y), out var formKey)) return null;
            var (landscape, _) = HeightmapDecoder.ResolveWinningLandscape(formKey, linkCache, priorityIndex);
            var heights = landscape?.VertexHeightMap is null ? null : HeightmapDecoder.DecodeHeights(landscape.VertexHeightMap);
            neighborHeightCache[(x, y)] = heights!;
            return heights;
        }

        foreach (var ((x, y), (before, after)) in mergedCellData)
        {
            // East neighbor (x+1, y)
            CheckEdge(x, y, x + 1, y, "East", before, after, mergedCellData, GetActualHeights, log, ref worsened, WorseningToleranceUnits);
            // North neighbor (x, y+1)
            CheckEdge(x, y, x, y + 1, "North", before, after, mergedCellData, GetActualHeights, log, ref worsened, WorseningToleranceUnits);
        }

        return worsened;
    }

    static void CheckEdge(
        int ax, int ay, int bx, int by, string edgeName,
        float[,] aBefore, float[,] aAfter,
        Dictionary<(int X, int Y), (float[,] Before, float[,] After)> mergedCellData,
        Func<int, int, float[,]?> getActualHeights,
        Action<string> log, ref int worsened, float tolerance)
    {
        float[,] bBefore, bAfter;
        if (mergedCellData.TryGetValue((bx, by), out var neighborMerged))
        {
            // Both sides merged - only check this pair once (from the lower
            // coordinate side) to avoid reporting the same edge twice.
            bBefore = neighborMerged.Before;
            bAfter = neighborMerged.After;
        }
        else
        {
            var actual = getActualHeights(bx, by);
            if (actual is null) return; // neighbor cell doesn't exist (map edge) or has no landscape data at all
            bBefore = actual;
            bAfter = actual; // untouched - "before" and "after" are the same
        }

        float maxBefore = 0f, maxAfter = 0f;
        for (int i = 0; i <= 32; i++)
        {
            float aB, aA, bB, bA;
            if (edgeName == "East")
            {
                aB = aBefore[32, i]; aA = aAfter[32, i];
                bB = bBefore[0, i]; bA = bAfter[0, i];
            }
            else
            {
                aB = aBefore[i, 32]; aA = aAfter[i, 32];
                bB = bBefore[i, 0]; bA = bAfter[i, 0];
            }
            maxBefore = Math.Max(maxBefore, Math.Abs(aB - bB));
            maxAfter = Math.Max(maxAfter, Math.Abs(aA - bA));
        }

        if (maxAfter > maxBefore + tolerance)
        {
            worsened++;
            log($"  [BOUNDARY] {edgeName} edge ({ax},{ay})|({bx},{by}): mismatch went from {maxBefore:F0} to {maxAfter:F0} units because of this merge.");
        }
    }

    static float MaxAbsDiff(float[,] a, float[,] b)
    {
        float max = 0f;
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
            max = Math.Max(max, Math.Abs(a[x, y] - b[x, y]));
        return max;
    }

    // Same walk as HeightmapDecoder.ResolveWinningLandscape, but skipping
    // any context owned by the road-source plugin, ANY genuine patch of it
    // (see the "sourcing road height from a patch" comment above - now that
    // a patch's data can BE the road source, it must also be excluded from
    // "other," or it could end up picked as both at once), and this tool's
    // own output - "what would this cell's terrain be if the road-shaping
    // mod (and its patch family) didn't exist at all."
    static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscapeExcluding(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex,
        string excludePlugin,
        string alsoExcludePlugin)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
            string ctxFileName = ctx.ModKey.FileName;
            if (ctxFileName.Equals(excludePlugin, StringComparison.OrdinalIgnoreCase)) continue;
            if (ctxFileName.Equals(alsoExcludePlugin, StringComparison.OrdinalIgnoreCase)) continue;
            if (TrustResolver.IsPatchOfTrustedBase(ctxFileName, excludePlugin)) continue;
            // FIXED 2026-09-15: `alsoExcludePlugin` only ever carried THIS
            // run's own output name - none of the OTHER 4 sibling tools'
            // outputs were excluded, so a stale prior LandscapeTextureFixes.esp
            // (itself downstream of RoadMaskMerge) could get picked up as the
            // "Other" baseline on a re-run instead of real underlying mod
            // data. See TrustResolver.SiblingToolOutputs for the full story.
            if (TrustResolver.SiblingToolOutputs.Contains(ctxFileName)) continue;
            var idx = priorityIndex.GetValueOrDefault(ctx.ModKey, -1);
            if (idx > bestIndex)
            {
                bestIndex = idx;
                best = ctx.Record.Landscape;
                bestModKey = ctx.ModKey;
            }
        }

        return (best, bestModKey);
    }
}
