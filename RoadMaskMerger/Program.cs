// RoadMaskMerger CLI - PROTOTYPE, not deployed, not tested in-game as of
// 2026-09-10. See NOTES.md for the full design writeup.
//
// Usage:
//   RoadMaskMerger.exe --mo2 <instancePath> <profileName> [gameDataPath]
//       [--road-source="Northern Roads.esp"] [--worldspace="Tamriel"]
//       [--acmos-roads="E:\Tabula Rasa\tools\ACMOS\roads"] [--paths-only]
//
// Always writes a NEW plugin (default name RoadMaskMerge.esp); never
// touches anything else. Does not require running detection first - there
// is no separate detection mode yet for this prototype.

using SeamFinder.Core;
using RoadMaskMerger;

if (args.Length == 0 || args[0] != "--mo2")
{
    Console.WriteLine("Usage:");
    Console.WriteLine("  RoadMaskMerger.exe --mo2 <instancePath> <profileName> [gameDataPath]");
    Console.WriteLine("      [--road-source=\"Northern Roads.esp\"] [--worldspace=\"Tamriel\"]");
    Console.WriteLine("      [--acmos-roads=\"<path>\"] [--paths-only]");
    Console.WriteLine("  (--acmos-roads defaults to the bundled Assets\\ACMOS-roads next to this exe)");
    Pause();
    return;
}

if (args.Length < 3)
{
    Console.WriteLine("Usage: RoadMaskMerger.exe --mo2 <instancePath> <profileName> [gameDataPath] [flags]");
    Pause();
    return;
}

var instancePath = args[1];
var profileName = args[2];
var gameDataPath = args.Length > 3 && !args[3].StartsWith("--") ? args[3] : ReadGamePathFromIni(instancePath);
var roadSource = StringArg(args, "--road-source=") ?? "Northern Roads.esp";
var worldspace = StringArg(args, "--worldspace=") ?? "Tamriel";
// Bundled alongside this exe (Assets/ACMOS-roads, copied at build time -
// see the .csproj and Assets/NOTICE.md for the license/attribution this
// carries) so the tool works out of the box without depending on an
// external ACMOS install path - --acmos-roads= still overrides it.
var acmosRoads = StringArg(args, "--acmos-roads=") ?? Path.Combine(AppContext.BaseDirectory, "Assets", "ACMOS-roads");
var pathsOnly = args.Contains("--paths-only");

Console.WriteLine($"MO2 instance: {instancePath}");
Console.WriteLine($"Profile: {profileName}");
Console.WriteLine($"Game Data path: {gameDataPath}");
Console.WriteLine($"Road-source plugin: {roadSource}");
Console.WriteLine($"Worldspace: {worldspace}");
Console.WriteLine($"ACMOS roads folder: {acmosRoads}" + (pathsOnly ? " (Paths Only variant)" : " (Roads variant)"));
Console.WriteLine();

try
{
    var resolved = Mo2Resolver.Resolve(instancePath, profileName, gameDataPath);
    Console.WriteLine($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
    if (resolved.MissingPlugins.Count > 0)
    {
        Console.WriteLine($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
        foreach (var m in resolved.MissingPlugins) Console.WriteLine("  " + m);
    }

    var result = RoadTerrainMerger.RunForResolvedPlugins(
        resolved.LoadOrder, "RoadMaskMerge.esp", AppContext.BaseDirectory, Console.WriteLine,
        roadSource, acmosRoads, worldspace, pathsOnly);

    Console.WriteLine();
    Console.WriteLine($"Merged {result.CellsMerged} cell(s), {result.CellsFellBackToFullRoad} via full-road fallback, {result.CellsSkipped} skipped.");
    if (!string.IsNullOrEmpty(result.OutputPath))
        Console.WriteLine($"Output: {result.OutputPath}");
}
catch (Exception ex)
{
    Console.WriteLine();
    Console.WriteLine("ERROR: " + ex);
}

Pause();

static string? StringArg(string[] a, string prefix)
{
    var arg = a.FirstOrDefault(x => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    return arg?[prefix.Length..].Trim('"');
}

static void Pause()
{
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    try { Console.ReadKey(); } catch (InvalidOperationException) { }
}

static string ReadGamePathFromIni(string instancePath)
{
    var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
    if (!File.Exists(iniPath))
        throw new FileNotFoundException("No gameDataPath given and ModOrganizer.ini not found to read it from.", iniPath);

    foreach (var line in File.ReadAllLines(iniPath))
    {
        if (!line.StartsWith("gamePath=")) continue;
        var value = line["gamePath=".Length..].Trim();
        var start = value.IndexOf('(');
        var end = value.LastIndexOf(')');
        if (start >= 0 && end > start)
            value = value[(start + 1)..end];
        value = value.Replace("\\\\", "\\");
        return Path.Combine(value, "Data");
    }

    throw new InvalidOperationException("Could not find gamePath= in ModOrganizer.ini - pass gameDataPath explicitly instead.");
}
