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
        List<string> MissingPlugins)
    {
        // Set when the given path was the game's install folder and the
        // resolver stepped into its Data subfolder - callers log it.
        public string? GameDataPathCorrectedFrom { get; init; }
    }

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
        // MO2's overwrite folder outranks every mod folder - generated
        // plugins (Synthesis.esp, Bashed Patch, tool output) often live there.
        var overwriteDir = Path.Combine(instancePath, "overwrite");
        var givenGameDataPath = gameDataPath;
        gameDataPath = NormalizeGameDataPath(gameDataPath);

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
            var overwriteCandidate = Path.Combine(overwriteDir, fileName);
            string? path = File.Exists(overwriteCandidate) ? overwriteCandidate : null;
            foreach (var modName in enabledModsInPriorityOrder)
            {
                var candidate = Path.Combine(modsDir, modName, fileName);
                if (path is not null) break;
                if (File.Exists(candidate)) { path = candidate; break; }
            }
            path ??= Path.Combine(gameDataPath, fileName);

            if (File.Exists(path))
                resolved.Add(new ResolvedPlugin(fileName, path));
            else
                missing.Add(fileName);
        }

        ThrowIfLoadOrderUnusable(resolved, missing, gameDataPath);
        return new Result(resolved, missing)
        {
            GameDataPathCorrectedFrom = !string.Equals(gameDataPath, givenGameDataPath?.Trim().Trim('"').TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ? givenGameDataPath : null,
        };
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

    // The game Data folder must be the folder that actually holds Skyrim.esm.
    // A common mistake is pointing it at the game's install folder instead
    // (...\Skyrim Special Edition rather than ...\Skyrim Special Edition\Data):
    // every vanilla/DLC/Creation Club master then goes "missing", the run
    // carries on for minutes, and Mutagen only fails at the very end when it
    // writes the output and can't find Dawnguard.esm. Accept the install
    // folder by stepping into its Data subfolder.
    public static string NormalizeGameDataPath(string gameDataPath)
    {
        if (string.IsNullOrWhiteSpace(gameDataPath)) return gameDataPath;
        var trimmed = gameDataPath.Trim().Trim('"').TrimEnd('\\', '/');
        if (File.Exists(Path.Combine(trimmed, "Skyrim.esm"))) return trimmed;
        var dataSubfolder = Path.Combine(trimmed, "Data");
        if (File.Exists(Path.Combine(dataSubfolder, "Skyrim.esm"))) return dataSubfolder;
        return trimmed;
    }

    // Stops the run up front, with a message that says what to fix, when the
    // load order can't be built: Skyrim.esm itself missing (wrong Data folder),
    // or a found plugin listing a missing plugin as one of its masters. Either
    // one makes Mutagen throw a bare FileNotFoundException later - usually only
    // after the whole scan has already run.
    static void ThrowIfLoadOrderUnusable(List<ResolvedPlugin> resolved, List<string> missing, string gameDataPath)
    {
        if (missing.Count == 0) return;
        var missingSet = new HashSet<string>(missing, StringComparer.OrdinalIgnoreCase);

        if (missingSet.Contains("Skyrim.esm"))
            throw new InvalidOperationException(
                $"Skyrim.esm was not found in the game Data folder \"{gameDataPath}\". " +
                "Set the Game Data path to the game's Data folder - the one that contains Skyrim.esm, " +
                @"e.g. ...\steamapps\common\Skyrim Special Edition\Data.");

        var broken = new List<string>();
        foreach (var plugin in resolved)
        {
            List<string> masters;
            try { masters = ReadMasterNames(plugin.FilePath); }
            catch { continue; } // unreadable header - let the normal load report it
            foreach (var master in masters)
                if (missingSet.Contains(master))
                    broken.Add($"{plugin.FileName} needs {master}");
        }
        if (broken.Count == 0) return;

        var shown = string.Join(Environment.NewLine, broken.Take(20).Select(b => "  " + b));
        var more = broken.Count > 20 ? $"{Environment.NewLine}  ...and {broken.Count - 20} more" : "";
        throw new InvalidOperationException(
            $"{broken.Count} active plugin(s) need a master that could not be found in any enabled mod folder, " +
            $"MO2's overwrite folder, or the game Data folder \"{gameDataPath}\":{Environment.NewLine}{shown}{more}" +
            $"{Environment.NewLine}Check that the Game Data path is the game's Data folder, and that the mods providing these masters are enabled.");
    }

    // Master list straight from the TES4 header (MAST subrecords) - only the
    // header bytes are read.
    static List<string> ReadMasterNames(string pluginPath)
    {
        using var stream = File.OpenRead(pluginPath);
        using var reader = new BinaryReader(stream);
        var masters = new List<string>();
        if (System.Text.Encoding.ASCII.GetString(reader.ReadBytes(4)) != "TES4") return masters;
        var dataSize = reader.ReadUInt32();
        stream.Seek(16, SeekOrigin.Current); // rest of the 24-byte record header
        var header = reader.ReadBytes((int)dataSize);
        int pos = 0;
        uint? bigSize = null;
        while (pos + 6 <= header.Length)
        {
            var type = System.Text.Encoding.ASCII.GetString(header, pos, 4);
            int size = BitConverter.ToUInt16(header, pos + 4);
            pos += 6;
            if (type == "XXXX") { bigSize = BitConverter.ToUInt32(header, pos); pos += size; continue; }
            if (bigSize is not null) { size = (int)bigSize.Value; bigSize = null; }
            if (pos + size > header.Length) break;
            if (type == "MAST")
                masters.Add(System.Text.Encoding.Latin1.GetString(header, pos, size).TrimEnd('\0'));
            pos += size;
        }
        return masters;
    }
}
