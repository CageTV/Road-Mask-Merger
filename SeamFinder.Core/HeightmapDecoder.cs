// Shared VHGT (vertex heightmap) decode logic - was duplicated identically
// across SeamFixer.cs and SeamDetector.cs (the exact drift risk this file
// exists to remove, matching TrustResolver.cs's reasoning). Also home to
// the new bilinear interpolation FloatingObjectDetector needs: a placed
// reference's exact X/Y almost never lands on a heightmap vertex (vertices
// are 128 units apart within a 4096-unit cell), so a nearest-vertex lookup
// alone isn't accurate enough to judge "is this object floating."

using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace SeamFinder.Core;

public static class HeightmapDecoder
{
    // Resolves the winning Landscape sub-record for a cell. Note this can be
    // owned by a DIFFERENT plugin than the one that wins the Cell overall -
    // a later plugin can win the Cell (e.g. by adding an NPC) without ever
    // redeclaring Landscape, leaving an earlier plugin's Landscape as the
    // one actually in effect. Was duplicated identically in SeamFixer.cs and
    // TextureLayerFixer.cs before - same drift risk as everything else here.
    public static (ILandscapeGetter? Landscape, ModKey OwnerModKey) ResolveWinningLandscape(
        FormKey cellFormKey,
        ILinkCache<ISkyrimMod, ISkyrimModGetter> linkCache,
        Dictionary<ModKey, int> priorityIndex)
    {
        ILandscapeGetter? best = null;
        ModKey bestModKey = default;
        int bestIndex = -1;

        foreach (var ctx in linkCache.ResolveAllContexts<Cell, ICellGetter>(cellFormKey, ResolveTarget.Winner))
        {
            if (ctx.Record.Landscape is null) continue;
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

    public static float[,] DecodeHeights(ILandscapeVertexHeightMapGetter vhgt)
    {
        var heights = new float[33, 33];
        var map = vhgt.HeightMap;
        for (int y = 0; y <= 32; y++)
        {
            for (int x = 0; x <= 32; x++)
            {
                sbyte delta = map[x, y];
                if (x == 0)
                {
                    heights[0, y] = y == 0
                        ? vhgt.Offset + delta * 8f
                        : heights[0, y - 1] + delta * 8f;
                }
                else
                {
                    heights[x, y] = heights[x - 1, y] + delta * 8f;
                }
            }
        }
        return heights;
    }

    // True if every one of the 1089 vertices matches within a tiny epsilon -
    // "tiny" rather than exact-zero purely as float-safety margin, since both
    // sides were decoded through the same delta-step math and a genuinely
    // unedited/ITM copy reproduces the exact same bytes, not just similar
    // values. Real edits are never this close by accident - the smallest
    // legitimate correction SeamFixer itself makes is many units, and hand
    // authored terrain edits are larger still.
    public static bool HeightsMatch(float[,] a, float[,] b)
    {
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
            if (Math.Abs(a[x, y] - b[x, y]) > 0.5f) return false;
        return true;
    }

    // Diagnostic companion to HeightsMatch - finds the single worst-mismatched
    // vertex and where it is, so a failed ITM check can be understood instead
    // of just trusted as a bare "not identical" verdict.
    public static (float MaxDiff, int X, int Y) MaxHeightDiff(float[,] a, float[,] b)
    {
        float maxDiff = 0f;
        int atX = -1, atY = -1;
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
        {
            var d = Math.Abs(a[x, y] - b[x, y]);
            if (d > maxDiff) { maxDiff = d; atX = x; atY = y; }
        }
        return (maxDiff, atX, atY);
    }

    // Bilinear-interpolated terrain height at an arbitrary point WITHIN a
    // cell, given in LOCAL coordinates (0..4096, i.e. worldX/Y minus the
    // cell's own origin) - the accurate version of "what's the ground level
    // right here," vs. just reading the nearest one of the 33x33 vertices.
    // Vertices sit every 128 units (4096 / 32), so localX/128 and localY/128
    // give a fractional vertex-grid position to interpolate between.
    public static float GetInterpolatedHeight(float[,] heights, float localX, float localY)
    {
        var gx = Math.Clamp(localX / 128f, 0f, 32f);
        var gy = Math.Clamp(localY / 128f, 0f, 32f);
        var x0 = (int)Math.Floor(gx);
        var y0 = (int)Math.Floor(gy);
        var x1 = Math.Min(x0 + 1, 32);
        var y1 = Math.Min(y0 + 1, 32);
        var fx = gx - x0;
        var fy = gy - y0;

        var h00 = heights[x0, y0];
        var h10 = heights[x1, y0];
        var h01 = heights[x0, y1];
        var h11 = heights[x1, y1];

        var hx0 = h00 + (h10 - h00) * fx;
        var hx1 = h01 + (h11 - h01) * fx;
        return hx0 + (hx1 - hx0) * fy;
    }

    // Max-minus-min height among the 4 heightmap vertices surrounding this
    // point - a cheap "how rugged is it right here" signal. Bilinear
    // interpolation between 4 corners assumes a roughly flat quad; on a
    // steep alpine ridge those 4 corners can differ by thousands of units,
    // and the interpolated value in between is not a trustworthy estimate
    // of the true surface. Confirmed necessary in practice: real Throat-of-
    // -the-World-area terrain pieces showed 20,000+ unit apparent "floating"
    // deltas that were just steep terrain the grid under-samples, not bugs.
    public static float GetLocalRoughness(float[,] heights, float localX, float localY)
    {
        var gx = Math.Clamp(localX / 128f, 0f, 32f);
        var gy = Math.Clamp(localY / 128f, 0f, 32f);
        var x0 = (int)Math.Floor(gx);
        var y0 = (int)Math.Floor(gy);
        var x1 = Math.Min(x0 + 1, 32);
        var y1 = Math.Min(y0 + 1, 32);

        var h00 = heights[x0, y0];
        var h10 = heights[x1, y0];
        var h01 = heights[x0, y1];
        var h11 = heights[x1, y1];

        var min = Math.Min(Math.Min(h00, h10), Math.Min(h01, h11));
        var max = Math.Max(Math.Max(h00, h10), Math.Max(h01, h11));
        return max - min;
    }
}
