// Resolves an MO2 instance/profile into (a) the active load order and (b)
// a physical file for each active plugin - without needing MO2's own VFS.
// Same idea as MO2's virtual file system, just done with plain file reads
// instead of a kernel driver, so it works from any process regardless of
// how it was launched.
//
// Format notes (validated against a real instance, C:\Wabbajack\TBA):
//   plugins.txt   - ONLY lists regular toggleable plugins. A line starting
//                    with '*' is explicitly active; a line with no '*' is
//                    explicitly INACTIVE. Master files (Skyrim.esm, DLC
//                    .esm's) and ALL Creation Club content (.esm/.esl) are
//                    never listed here at all - they're implicitly always
//                    active. So the correct active/inactive rule is: active
//                    unless explicitly listed WITHOUT '*'. Getting this
//                    backwards (treating "not mentioned" as inactive)
//                    silently drops every master and CC plugin - caught by
//                    testing against real data before shipping this.
//   loadorder.txt - full load order (top = loaded first = lowest priority),
//                    every plugin MO2 knows about regardless of active state.
//   modlist.txt   - mod folder priority order, top = HIGHEST priority
//                    (wins file conflicts). '+' prefix = enabled folder,
//                    '-' prefix = disabled (its files are never used even
//                    if present on disk).
//
// File resolution for a given active plugin filename: search enabled mod
// folders in priority order (top first) for a file with that exact name;
// first match wins. If no mod folder has it, fall back to the base game's
// Data folder (covers un-replaced vanilla masters).

namespace SeamFinder.Core;

public static class Mo2Resolver
{
    public record ResolvedPlugin(string FileName, string FilePath);

    public record Result(
        List<ResolvedPlugin> LoadOrder,
        List<string> MissingPlugins);

    public static Result Resolve(string instancePath, string profileName, string gameDataPath)
    {
        var profileDir = Path.Combine(instancePath, "profiles", profileName);
        var pluginsTxtPath = Path.Combine(profileDir, "plugins.txt");
        var loadOrderTxtPath = Path.Combine(profileDir, "loadorder.txt");
        var modlistTxtPath = Path.Combine(profileDir, "modlist.txt");
        return ResolveFromExplicitPaths(pluginsTxtPath, loadOrderTxtPath, modlistTxtPath, instancePath, gameDataPath);
    }

    /// Same as Resolve, but takes explicit paths to the three profile files
    /// instead of deriving them from instancePath+profileName - lets a UI
    /// override individual file locations if a profile is laid out
    /// differently than the standard <instance>\profiles\<name>\ structure.
    /// instancePath is still needed to locate the "mods" folder.
    public static Result ResolveFromExplicitPaths(
        string pluginsTxtPath, string loadOrderTxtPath, string modlistTxtPath,
        string instancePath, string gameDataPath)
    {
        var modsDir = Path.Combine(instancePath, "mods");

        if (!File.Exists(pluginsTxtPath)) throw new FileNotFoundException("plugins.txt not found - check the MO2 instance path and profile name.", pluginsTxtPath);
        if (!File.Exists(loadOrderTxtPath)) throw new FileNotFoundException("loadorder.txt not found - check the MO2 instance path and profile name.", loadOrderTxtPath);
        if (!File.Exists(modlistTxtPath)) throw new FileNotFoundException("modlist.txt not found - check the MO2 instance path and profile name.", modlistTxtPath);

        var explicitlyInactive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in File.ReadAllLines(pluginsTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith('*'))
                explicitlyInactive.Add(line.Trim());
        }

        var activeLoadOrder = new List<string>();
        foreach (var line in File.ReadAllLines(loadOrderTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            var name = line.Trim();
            if (!explicitlyInactive.Contains(name))
                activeLoadOrder.Add(name);
        }

        var enabledModsInPriorityOrder = new List<string>();
        foreach (var line in File.ReadAllLines(modlistTxtPath))
        {
            if (line.StartsWith('#') || string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith('+'))
                enabledModsInPriorityOrder.Add(line[1..].Trim());
        }

        var resolved = new List<ResolvedPlugin>();
        var missing = new List<string>();
        foreach (var fileName in activeLoadOrder)
        {
            string? path = null;
            foreach (var modName in enabledModsInPriorityOrder)
            {
                var candidate = Path.Combine(modsDir, modName, fileName);
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            path ??= Path.Combine(gameDataPath, fileName);

            if (File.Exists(path))
                resolved.Add(new ResolvedPlugin(fileName, path));
            else
                missing.Add(fileName);
        }

        return new Result(resolved, missing);
    }

    /// Copies every resolved plugin file into destFolder under its original
    /// filename, so a plain single-folder-scanning environment builder can
    /// load them as if they were all physically in one merged Data folder.
    /// Plain copy (not a symlink/hardlink) for reliability regardless of
    /// which drives the MO2 instance and destination happen to be on.
    public static void MaterializeMergedFolder(List<ResolvedPlugin> loadOrder, string destFolder)
    {
        Directory.CreateDirectory(destFolder);
        foreach (var plugin in loadOrder)
        {
            var dest = Path.Combine(destFolder, plugin.FileName);
            File.Copy(plugin.FilePath, dest, overwrite: true);
        }
    }
}
