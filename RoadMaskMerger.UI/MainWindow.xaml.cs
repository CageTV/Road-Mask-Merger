using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Environments;
using Mutagen.Bethesda.Skyrim;
using SeamFinder.Core;

namespace RoadMaskMerger.UI;

public partial class MainWindow : Window
{
    string? _lastFixPluginPath;
    string? _lastOutputFolder;
    string? _currentLogFilePath;

    bool _outputFolderAutoSet = true;
    bool _suppressOutputTextChanged;

    public MainWindow()
    {
        InitializeComponent();
        // Bundled alongside this app (Assets\ACMOS-roads, copied at build
        // time - see RoadMaskMerger's Assets\NOTICE.md for the license this
        // carries) so it works out of the box; the box stays editable if a
        // different/updated ACMOS road-mask set is ever needed. Set BEFORE
        // LoadPersistedSettings so a real saved value (from a prior run)
        // still wins over this default, exactly like SeamFinder.UI's own
        // settings-persistence pattern.
        AcmosRoadsBox.Text = Path.Combine(AppContext.BaseDirectory, "Assets", "ACMOS-roads");
        LoadPersistedSettings();
        RefreshOutputFolderDefault();
    }

    // --- Settings persistence ---
    //
    // Remembers everything typed into the form across app launches, same
    // pattern (and same reasoning - rebuilt/republished in place during
    // development, so settings must live outside the exe's own folder) as
    // SeamFinder.UI/TextureSeamFixer.UI/FloatingObjectFixer.UI already use.
    // Added 2026-09-11 once this tool graduated from prototype status.
    static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RoadMaskMerger", "settings.json");

    record PersistedSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath, string OutputFolder,
        string RoadSourcePlugin, string Worldspace, string AcmosRoadsFolder, bool PathsOnly);

    void LoadPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsFilePath));
            if (s is null) return;

            (ModeMo2.IsChecked, ModeVortex.IsChecked, ModeDirect.IsChecked) = s switch
            {
                { IsMo2Mode: true } => (true, false, false),
                { IsVortexMode: true } => (false, true, false),
                _ => (false, false, true),
            };
            Mo2InstancePathBox.Text = s.Mo2InstancePath;
            Mo2GameDataPathBox.Text = s.Mo2GameDataPath;
            Mo2PluginsTxtBox.Text = s.Mo2PluginsTxt;
            Mo2LoadOrderTxtBox.Text = s.Mo2LoadOrderTxt;
            Mo2ModlistTxtBox.Text = s.Mo2ModlistTxt;
            VortexGameDataPathBox.Text = s.VortexGameDataPath;
            DirectGameDataPathBox.Text = s.DirectGameDataPath;
            if (!string.IsNullOrEmpty(s.RoadSourcePlugin)) RoadSourceBox.Text = s.RoadSourcePlugin;
            if (!string.IsNullOrEmpty(s.Worldspace)) WorldspaceBox.Text = s.Worldspace;
            if (!string.IsNullOrEmpty(s.AcmosRoadsFolder)) AcmosRoadsBox.Text = s.AcmosRoadsFolder;
            PathsOnlyCheck.IsChecked = s.PathsOnly;
            if (!string.IsNullOrEmpty(s.OutputFolder)) OutputFolderBox.Text = s.OutputFolder; // marks _outputFolderAutoSet false via its own TextChanged handler
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh rather than
            // block the app from opening at all.
        }
    }

    void SavePersistedSettings(RunSettings s)
    {
        try
        {
            var persisted = new PersistedSettings(
                s.IsMo2Mode, s.IsVortexMode,
                s.Mo2InstancePath, s.Mo2GameDataPath, s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt,
                s.VortexGameDataPath, s.DirectGameDataPath, OutputFolderBox.Text.Trim(),
                s.RoadSourcePlugin, s.Worldspace, s.AcmosRoadsFolder, s.PathsOnly);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, System.Text.Json.JsonSerializer.Serialize(persisted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - a locked/inaccessible AppData shouldn't stop the run itself.
        }
    }

    // Writes the exact settings a run used into that run's own output
    // folder too (alongside log.txt/esp), separate from the always-on-launch
    // copy above - a record of what config produced this particular output,
    // portable with it if the folder is shared/moved. Matches SeamFinder.UI's
    // own pattern (added there first; ported here 2026-09-15 for uniformity
    // across all 5 tools in this family, per the user's explicit request).
    void SaveSettingsSnapshotToOutputFolder(RunSettings s, string outputFolder)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            File.WriteAllText(Path.Combine(outputFolder, "settings-used.json"),
                System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - see SavePersistedSettings.
        }
    }

    // --- Mode switching ---

    void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (Mo2Panel is null) return;

        Mo2Panel.Visibility = ModeMo2.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        VortexPanel.Visibility = ModeVortex.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        DirectPanel.Visibility = ModeDirect.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RefreshOutputFolderDefault();
    }

    // --- Output folder: smart per-mode default, stays editable ---

    void ModeDataPathBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshOutputFolderDefault();

    void OutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOutputTextChanged) return;
        _outputFolderAutoSet = false;
    }

    void RefreshOutputFolderDefault()
    {
        if (OutputFolderBox is null) return;

        string? defaultPath = null;
        string hint = "";

        if (ModeMo2?.IsChecked == true)
        {
            var instancePath = Mo2InstancePathBox?.Text.Trim();
            if (!string.IsNullOrEmpty(instancePath))
            {
                defaultPath = Path.Combine(instancePath, "mods", "Road Mask Merge");
                hint = "Writes into your MO2 instance's mods folder, so it shows up as an installable mod (a meta.ini is added automatically). Review the log before installing, as always.";
            }
        }
        else if (ModeVortex?.IsChecked == true)
        {
            defaultPath = VortexGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder, matching where Vortex deploys mods by default.";
        }
        else if (ModeDirect?.IsChecked == true)
        {
            defaultPath = DirectGameDataPathBox?.Text.Trim();
            hint = "Writes directly into your game's Data folder.";
        }

        OutputHintText.Text = hint + " You can change this to any folder you like.";

        if (_outputFolderAutoSet && !string.IsNullOrEmpty(defaultPath))
        {
            _suppressOutputTextChanged = true;
            OutputFolderBox.Text = defaultPath;
            _suppressOutputTextChanged = false;
        }
    }

    // --- MO2 panel ---

    void Mo2BrowseInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your MO2 instance folder (where ModOrganizer.exe lives)" };
        if (dlg.ShowDialog() == true)
            Mo2InstancePathBox.Text = dlg.FolderName;
    }

    void Mo2BrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            Mo2GameDataPathBox.Text = dlg.FolderName;
    }

    void Mo2BrowsePluginsTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2PluginsTxtBox, "plugins.txt");
    void Mo2BrowseLoadOrderTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2LoadOrderTxtBox, "loadorder.txt");
    void Mo2BrowseModlistTxt_Click(object sender, RoutedEventArgs e) => BrowseForFile(Mo2ModlistTxtBox, "modlist.txt");

    static void BrowseForFile(TextBox target, string suggestedName)
    {
        var dlg = new OpenFileDialog { FileName = suggestedName, Filter = "Text files|*.txt|All files|*.*" };
        if (dlg.ShowDialog() == true)
            target.Text = dlg.FileName;
    }

    void Mo2InstancePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMo2Instance();
        RefreshOutputFolderDefault();
    }

    void RefreshMo2Instance()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath) || !Directory.Exists(instancePath)) return;

        var profilesDir = Path.Combine(instancePath, "profiles");
        Mo2ProfileCombo.Items.Clear();
        if (Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.GetDirectories(profilesDir))
                Mo2ProfileCombo.Items.Add(Path.GetFileName(dir));
        }

        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(iniPath))
        {
            var gamePath = ReadIniGamePath(iniPath);
            if (gamePath != null)
                Mo2GameDataPathBox.Text = Path.Combine(gamePath, "Data");

            var selectedProfile = ReadIniSelectedProfile(iniPath);
            if (selectedProfile != null && Mo2ProfileCombo.Items.Contains(selectedProfile))
                Mo2ProfileCombo.SelectedItem = selectedProfile;
        }

        if (Mo2ProfileCombo.SelectedItem is null && Mo2ProfileCombo.Items.Count > 0)
            Mo2ProfileCombo.SelectedIndex = 0;

        RefreshMo2ProfileFiles();
    }

    void RefreshMo2ProfileFiles()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        var profile = Mo2ProfileCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(instancePath) || string.IsNullOrEmpty(profile)) return;

        var profileDir = Path.Combine(instancePath, "profiles", profile);
        Mo2PluginsTxtBox.Text = Path.Combine(profileDir, "plugins.txt");
        Mo2LoadOrderTxtBox.Text = Path.Combine(profileDir, "loadorder.txt");
        Mo2ModlistTxtBox.Text = Path.Combine(profileDir, "modlist.txt");
    }

    static string? ReadIniGamePath(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("gamePath=")) continue;
            var value = line["gamePath=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value.Replace("\\\\", "\\");
        }
        return null;
    }

    static string? ReadIniSelectedProfile(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("selected_profile=")) continue;
            var value = line["selected_profile=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value;
        }
        return null;
    }

    // --- Vortex panel ---

    void VortexBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            VortexGameDataPathBox.Text = dlg.FolderName;
    }

    void VortexAutoDetect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var env = GameEnvironment.Typical.Construct<ISkyrimMod, ISkyrimModGetter>(GameRelease.SkyrimSE);
            VortexGameDataPathBox.Text = env.DataFolderPath.Path;
            AppendLog($"Auto-detected game Data folder: {env.DataFolderPath.Path}");
        }
        catch (Exception ex)
        {
            AppendLog("Auto-detect failed: " + ex.Message);
            MessageBox.Show(this, "Could not auto-detect your game install. Please browse to your Data folder manually.",
                "Auto-detect failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // --- Direct panel ---

    void DirectBrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            DirectGameDataPathBox.Text = dlg.FolderName;
    }

    // --- Output / ACMOS ---

    void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select an output folder" };
        if (dlg.ShowDialog() == true)
            OutputFolderBox.Text = dlg.FolderName;
    }

    void AcmosBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select ACMOS Road Generator's \"roads\" folder" };
        if (dlg.ShowDialog() == true)
            AcmosRoadsBox.Text = dlg.FolderName;
    }

    // --- Run ---

    record RunSettings(
        bool IsMo2Mode, bool IsVortexMode,
        string Mo2InstancePath, string Mo2GameDataPath, string Mo2PluginsTxt, string Mo2LoadOrderTxt, string Mo2ModlistTxt,
        string VortexGameDataPath, string DirectGameDataPath,
        string RoadSourcePlugin, string Worldspace, string AcmosRoadsFolder, bool PathsOnly);

    void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    RunSettings SnapshotSettings() => new(
        ModeMo2.IsChecked == true, ModeVortex.IsChecked == true,
        Mo2InstancePathBox.Text.Trim(), Mo2GameDataPathBox.Text.Trim(), Mo2PluginsTxtBox.Text.Trim(),
        Mo2LoadOrderTxtBox.Text.Trim(), Mo2ModlistTxtBox.Text.Trim(),
        VortexGameDataPathBox.Text.Trim(), DirectGameDataPathBox.Text.Trim(),
        RoadSourceBox.Text.Trim(), WorldspaceBox.Text.Trim(), AcmosRoadsBox.Text.Trim(),
        PathsOnlyCheck.IsChecked == true);

    async void RunButton_Click(object sender, RoutedEventArgs e)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenFixPluginButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);

        var settings = SnapshotSettings();
        var outputFolder = OutputFolderBox.Text.Trim();
        StartLogFile(outputFolder);
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, outputFolder);

        try
        {
            if (string.IsNullOrEmpty(outputFolder))
            {
                ShowValidation("Please choose an output folder.");
                return;
            }
            if (string.IsNullOrEmpty(settings.Worldspace))
            {
                ShowValidation("Please fill in a worldspace (e.g. \"Tamriel\") - the road mask is per-worldspace.");
                return;
            }

            var result = await Task.Run(() => GenerateForSelectedMode(settings, outputFolder));
            if (result is null) return;

            Dispatcher.Invoke(() => EnsureMo2MetaIni(outputFolder));

            _lastFixPluginPath = result.OutputPath;
            _lastOutputFolder = outputFolder;
            ResultText.Text = $"Merged {result.CellsMerged} cell(s) ({result.CellsFellBackToFullRoad} via full-road fallback, {result.CellsSkipped} skipped). " +
                "Review the log (especially [BOUNDARY] lines) before installing.";
            OpenFixPluginButton.IsEnabled = !string.IsNullOrEmpty(result.OutputPath);
            OpenFolderButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    RoadMergeResult? GenerateForSelectedMode(RunSettings s, string outputFolder)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));
        const string pluginName = "RoadMaskMerge.esp";

        if (s.IsMo2Mode)
        {
            if (string.IsNullOrEmpty(s.Mo2InstancePath) || string.IsNullOrEmpty(s.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return null;
            }

            Log($"MO2 instance: {s.Mo2InstancePath}");
            Log($"Game Data path: {s.Mo2GameDataPath}");
            Log($"Road-source plugin: {s.RoadSourcePlugin}");
            Log($"Worldspace: {s.Worldspace}");
            Log($"ACMOS roads folder: {s.AcmosRoadsFolder} ({(s.PathsOnly ? "Paths Only" : "Roads")} variant)");
            Log("");

            var resolved = Mo2Resolver.ResolveFromExplicitPaths(
                s.Mo2PluginsTxt, s.Mo2LoadOrderTxt, s.Mo2ModlistTxt, s.Mo2InstancePath, s.Mo2GameDataPath);
            Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
            if (resolved.MissingPlugins.Count > 0)
            {
                Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
                foreach (var m in resolved.MissingPlugins) Log("  " + m);
            }

            return RoadTerrainMerger.RunForResolvedPlugins(
                resolved.LoadOrder, pluginName, outputFolder, Log,
                s.RoadSourcePlugin, s.AcmosRoadsFolder, s.Worldspace, s.PathsOnly);
        }
        else
        {
            // FOUND AS A REAL REGRESSION: the Vortex/Direct UI panels and
            // RoadTerrainMerger.RunForDirectDataFolder both already existed
            // and worked - this hard block was the only piece never actually
            // wired up, so both modes refused to run regardless of what the
            // user filled in. Was fixed once in v1.4.0, but that fix got
            // reverted along with v1.4.0's separate, real texture-foundation
            // bug (see the "Revert to v1.3.1 baseline" commit) and never
            // re-applied on the v1.3.x line the revert kept. Mirrors the
            // sibling Landscape Seam Fixer tool's own
            // GenerateFixPluginForDirectDataFolder call shape.
            var dataFolder = s.IsVortexMode ? s.VortexGameDataPath : s.DirectGameDataPath;
            if (string.IsNullOrEmpty(dataFolder))
            {
                ShowValidation("Please fill in the game Data folder.");
                return null;
            }

            Log($"Game Data path: {dataFolder}");
            Log($"Road-source plugin: {s.RoadSourcePlugin}");
            Log($"Worldspace: {s.Worldspace}");
            Log($"ACMOS roads folder: {s.AcmosRoadsFolder} ({(s.PathsOnly ? "Paths Only" : "Roads")} variant)");
            Log("");

            return RoadTerrainMerger.RunForDirectDataFolder(
                dataFolder, pluginName, outputFolder, Log,
                s.RoadSourcePlugin, s.AcmosRoadsFolder, s.Worldspace, s.PathsOnly);
        }
    }

    void ShowValidation(string message)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    void EnsureMo2MetaIni(string outputFolder)
    {
        if (ModeMo2.IsChecked != true) return;

        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath)) return;

        var modsDir = Path.Combine(instancePath, "mods") + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(outputFolder) + Path.DirectorySeparatorChar;
        if (!fullOutput.StartsWith(Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase)) return;

        // Defensive: the merge itself already creates this folder on
        // success, but don't assume it - a caller reaching this point with
        // a folder that doesn't exist yet should get a directory, not a
        // confusing "could not find a part of the path" crash.
        Directory.CreateDirectory(outputFolder);

        var metaPath = Path.Combine(outputFolder, "meta.ini");
        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\nversion=1.0.0\r\ninstalled=true\r\n");
            AppendLog($"Wrote meta.ini so this shows up as an MO2 mod: {metaPath}");
        }
    }

    // Points AppendLog's file mirror at <outputFolder>/log.txt and starts it
    // fresh (matching LogBox.Clear() for the UI copy) - same folder the esp
    // for this run lands in. Matches SeamFinder.UI's own pattern.
    void StartLogFile(string outputFolder)
    {
        if (string.IsNullOrEmpty(outputFolder)) { _currentLogFilePath = null; return; }
        try
        {
            Directory.CreateDirectory(outputFolder);
            _currentLogFilePath = Path.Combine(outputFolder, "log.txt");
            File.WriteAllText(_currentLogFilePath, "");
        }
        catch
        {
            // Best-effort - a locked/inaccessible output folder shouldn't
            // stop the run itself, just the file mirror of its log.
            _currentLogFilePath = null;
        }
    }

    void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        if (_currentLogFilePath is not null)
        {
            try { File.AppendAllText(_currentLogFilePath, line + Environment.NewLine); }
            catch { /* best-effort, see StartLogFile */ }
        }
    }

    // --- Result bar ---

    void OpenFixPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastFixPluginPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastFixPluginPath}\"") { UseShellExecute = true });
    }

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }
}
