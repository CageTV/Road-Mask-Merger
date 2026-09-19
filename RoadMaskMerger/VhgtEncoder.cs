// Encodes absolute per-vertex heights back into VHGT's Offset + signed-byte
// delta-per-vertex format - the inverse of HeightmapDecoder.DecodeHeights.
// Deliberately kept in THIS project rather than promoted into the shared
// SeamFinder.Core yet (see NOTES.md) - nothing else in this toolkit has
// ever needed to construct new terrain height data from scratch before;
// every existing tool only ever copies an already-valid record forward
// verbatim. This is new, unproven territory.

namespace RoadMaskMerger;

public static class VhgtEncoder
{
    // delta[0,0] is always encoded as 0, so Offset alone exactly reproduces
    // heights[0,0] with zero rounding error - the one vertex in the whole
    // grid we can always get byte-for-byte exact. Offset is stored in the
    // SAME 8-unit-per-step quantization as the delta bytes (confirmed
    // against the UESP VHGT spec and Mutagen's raw unscaled Float field) -
    // it must be divided by 8 here, mirroring the *8 the decoder applies
    // on read. Storing the raw world height here (pre-fix bug, see
    // github.com/CageTV/landscape-seam-fixer/issues/2) wrote an Offset 8x
    // too large into real plugin files.
    public static (float Offset, sbyte[,] Deltas) Encode(float[,] heights)
    {
        var deltas = new sbyte[33, 33];
        float offset = heights[0, 0] / 8f;
        deltas[0, 0] = 0;

        for (int y = 1; y <= 32; y++)
            deltas[0, y] = ClampStep((heights[0, y] - heights[0, y - 1]) / 8f);

        for (int y = 0; y <= 32; y++)
        for (int x = 1; x <= 32; x++)
            deltas[x, y] = ClampStep((heights[x, y] - heights[x - 1, y]) / 8f);

        return (offset, deltas);
    }

    // Only ever called on a grid that RoadTerrainMerger's repair pass has
    // already verified has no adjacent-vertex step beyond MaxEncodableStep,
    // so clamping here should never actually trigger in practice - it's a
    // defensive backstop, not the mechanism relied on to keep steps in
    // range. If DecodeForVerification below doesn't round-trip exactly,
    // that backstop firing is exactly how you'd find out.
    static sbyte ClampStep(float step)
    {
        var rounded = (int)Math.Round(step, MidpointRounding.AwayFromZero);
        return (sbyte)Math.Clamp(rounded, sbyte.MinValue, sbyte.MaxValue);
    }

    // Mirrors HeightmapDecoder.DecodeHeights's exact algorithm, but against
    // raw Offset+deltas rather than a Mutagen VertexHeightMap getter - used
    // to round-trip-verify an encode BEFORE it's ever written into a plugin
    // (catch corruption in tooling, not in the headset).
    public static float[,] DecodeForVerification(float offset, sbyte[,] deltas)
    {
        var heights = new float[33, 33];
        for (int y = 0; y <= 32; y++)
        for (int x = 0; x <= 32; x++)
        {
            sbyte delta = deltas[x, y];
            heights[x, y] = x == 0
                ? (y == 0 ? (offset + delta) * 8f : heights[0, y - 1] + delta * 8f)
                : heights[x - 1, y] + delta * 8f;
        }
        return heights;
    }
}
