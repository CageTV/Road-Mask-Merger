// Samples ACMOS Road Generator's own road-network overlay PNGs
// (E:\Tabula Rasa\tools\ACMOS\roads\<Roads|Paths Only>\<worldspace>\
// <worldspace>.32.<tileX>.<tileY>.png) as a world-space "is this point on a
// road" mask. See NOTES.md for how the coordinate convention was verified
// (empirically, against a real Skyrim.esm landmark - RiverwoodFastTravelMarker).
//
// Tile naming is Bethesda's standard LOD-tile convention: tileX/tileY are
// the tile's MINIMUM cell coordinate corner, each tile spans 32 cells
// (32*4096 = 131072 world units) per side. Pixel X is unflipped (increasing
// column = increasing world X); pixel Y is FLIPPED (row 0 = the tile's
// NORTH edge, i.e. maximum world Y within the tile).

using System.Drawing;

namespace RoadMaskMerger;

public sealed class RoadMaskSampler : IDisposable
{
    const int CellsPerTile = 32;
    const float WorldUnitsPerCell = 4096f;
    const float WorldUnitsPerTile = CellsPerTile * WorldUnitsPerCell;

    readonly string _tilesRoot; // .../roads/<Roads|Paths Only>/<worldspace lowercase>
    readonly Dictionary<(int TileX, int TileY), Bitmap?> _tileCache = new();

    // alphaThreshold: ACMOS's road overlay carries the road shape mostly in
    // the ALPHA channel (the RGB itself is closer to a neutral asphalt
    // grey everywhere, with alpha fading to 0 off-road) - confirmed via the
    // calibration sampling in NOTES.md, where on-road pixels showed
    // alpha 200-255 and off-road/edge-fringe pixels showed low/zero alpha.
    // 128 sits comfortably in the middle of that observed range.
    public const int DefaultAlphaThreshold = 128;

    public RoadMaskSampler(string acmosRoadsFolder, string worldspaceEditorId, bool pathsOnly = false)
    {
        var variant = pathsOnly ? "Paths Only" : "Roads";
        _tilesRoot = Path.Combine(acmosRoadsFolder, variant, worldspaceEditorId.ToLowerInvariant());
    }

    public bool IsAvailable => Directory.Exists(_tilesRoot);

    public bool IsRoad(float worldX, float worldY, int alphaThreshold = DefaultAlphaThreshold)
    {
        var tileX = (int)Math.Floor(worldX / WorldUnitsPerTile) * CellsPerTile;
        var tileY = (int)Math.Floor(worldY / WorldUnitsPerTile) * CellsPerTile;

        var bmp = GetTile(tileX, tileY);
        if (bmp is null) return false;

        var tileOriginX = tileX * WorldUnitsPerCell;
        var tileOriginY = tileY * WorldUnitsPerCell;

        var px = (int)((worldX - tileOriginX) / WorldUnitsPerTile * bmp.Width);
        // Y flipped: row 0 = north edge = the tile's MAXIMUM world Y (tileOriginY + WorldUnitsPerTile).
        var py = (int)(((tileOriginY + WorldUnitsPerTile) - worldY) / WorldUnitsPerTile * bmp.Height);

        if (px < 0 || px >= bmp.Width || py < 0 || py >= bmp.Height) return false;

        var pixel = bmp.GetPixel(px, py);
        return pixel.A >= alphaThreshold;
    }

    Bitmap? GetTile(int tileX, int tileY)
    {
        var key = (tileX, tileY);
        if (_tileCache.TryGetValue(key, out var cached)) return cached;

        // ACMOS's own filename casing lowercases the worldspace but keeps
        // signed tile coordinates as plain integers (e.g. "tamriel.32.-32.0.png").
        var wsName = Path.GetFileName(_tilesRoot);
        var path = Path.Combine(_tilesRoot, $"{wsName}.32.{tileX}.{tileY}.png");
        Bitmap? bmp = File.Exists(path) ? new Bitmap(path) : null;
        _tileCache[key] = bmp;
        return bmp;
    }

    public void Dispose()
    {
        foreach (var bmp in _tileCache.Values) bmp?.Dispose();
        _tileCache.Clear();
    }
}
