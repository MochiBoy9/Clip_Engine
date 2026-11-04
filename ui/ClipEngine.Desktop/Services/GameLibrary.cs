using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClipEngine.Desktop.Services;

// A game found on this PC: where it is, which store put it there, and its Steam app id when it has one
// (the art comes from Steam even for games bought elsewhere).
internal sealed record GameInstall(string Name, string Store, string? Folder, string? Exe, int? SteamAppId);

// Which supported games are installed. Off Windows (the preview, tests) nothing can be looked up, so Known is false
// and every game counts as installed.
internal sealed class GameScan(IReadOnlyDictionary<string, GameInstall> installed, string? steamPath, bool known)
{
    public static GameScan Unknown { get; } = new(new Dictionary<string, GameInstall>(), null, false);

    public IReadOnlyDictionary<string, GameInstall> Installed { get; } = installed;
    public string? SteamPath { get; } = steamPath;
    public bool Known { get; } = known;

    public bool IsInstalled(string game) => !Known || Installed.ContainsKey(game);
    public GameInstall? Find(string game) => Installed.GetValueOrDefault(game);
}

// A game to look for: its name, and the programs it runs as.
internal sealed record GameTarget(string Name, IReadOnlyList<string> Executables);

// Finds installed games from the records each store and Windows keep, without searching whole drives:
// Steam's library folders, Epic's manifests, the Windows list of installed programs (Battle.net, EA, Ubisoft, Riot
// and most launchers add their games to it), Xbox app folders, Microsoft Store packages and Roblox's own folder.
internal static partial class GameLibrary
{
    // The programs each built-in game runs as, as in src/game/GameDetector.cpp.
    public static readonly IReadOnlyDictionary<string, string[]> Executables =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["The Finals"] = ["Discovery.exe"],
            ["Overwatch"] = ["Overwatch.exe"],
            ["Battlefield 6"] = ["bf6.exe"],
            ["Counter-Strike 2"] = ["cs2.exe"],
            ["Dota 2"] = ["dota2.exe"],
            ["League of Legends"] = ["League of Legends.exe"],
            ["Rocket League"] = ["RocketLeague.exe"],
            ["Team Fortress 2"] = ["tf_win64.exe", "tf.exe"],
            ["Valorant"] = ["VALORANT-Win64-Shipping.exe"],
            ["Fortnite"] = ["FortniteClient-Win64-Shipping.exe"],
            ["Apex Legends"] = ["r5apex.exe", "r5apex_dx12.exe"],
            ["PUBG"] = ["TslGame.exe"],
            ["Marvel Rivals"] = ["Marvel-Win64-Shipping.exe"],
            ["Rainbow Six Siege"] = ["RainbowSix.exe", "RainbowSix_Vulkan.exe", "RainbowSix_DX11.exe"],
            ["Call of Duty"] = ["cod.exe", "cod24-cod.exe", "cod25-cod.exe"],
            ["Delta Force"] = ["DeltaForceClient-Win64-Shipping.exe"],
            ["Deadlock"] = ["deadlock.exe", "project8.exe"],
            ["Halo Infinite"] = ["HaloInfinite.exe"],
            ["Halo MCC"] = ["MCC-Win64-Shipping.exe", "MCCWinStore-Win64-Shipping.exe"],
            ["Battlefield 2042"] = ["BF2042.exe"],
            ["Battlefield V"] = ["bfv.exe"],
            ["Battlefield 1"] = ["bf1.exe"],
            ["Battlefield 4"] = ["bf4.exe", "bf4_x86.exe"],
            ["Paladins"] = ["Paladins.exe"],
            ["Splitgate"] = ["PortalWars2-Win64-Shipping.exe", "PortalWars-Win64-Shipping.exe"],
            ["Roblox"] = ["RobloxPlayerBeta.exe"],
            ["Minecraft"] = ["Minecraft.Windows.exe"],
        };

    // Steam app ids, for art from Steam whichever store the game came from.
    public static readonly IReadOnlyDictionary<string, int> SteamIds =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["The Finals"] = 2073850,
            ["Overwatch"] = 2357570,
            ["Battlefield 6"] = 2807960,
            ["Counter-Strike 2"] = 730,
            ["Dota 2"] = 570,
            ["Rocket League"] = 252950,
            ["Apex Legends"] = 1172470,
            ["PUBG"] = 578080,
            ["Marvel Rivals"] = 2767030,
            ["Rainbow Six Siege"] = 359550,
            ["Call of Duty"] = 1938090,
            ["Delta Force"] = 2507950,
            ["Deadlock"] = 1422450,
            ["Halo Infinite"] = 1240440,
            ["Team Fortress 2"] = 440,
            ["Halo MCC"] = 976730,
            ["Battlefield 2042"] = 1517290,
            ["Battlefield V"] = 1238810,
            ["Battlefield 1"] = 1238840,
            ["Battlefield 4"] = 1238860,
            ["Paladins"] = 444090,
            ["Splitgate"] = 2918300,
        };

    // Other names stores list a game under, compared after Normalize.
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Overwatch"] = ["Overwatch 2"],
        ["Counter-Strike 2"] = ["Counter-Strike Global Offensive"],
        ["Rainbow Six Siege"] = ["Tom Clancy's Rainbow Six Siege", "Tom Clancy's Rainbow Six Siege X", "Rainbow Six Siege X"],
        ["PUBG"] = ["PUBG: Battlegrounds", "PlayerUnknown's Battlegrounds"],
        ["Call of Duty"] = ["Call of Duty HQ", "Call of Duty Modern Warfare III", "Call of Duty Black Ops 6", "Call of Duty Black Ops 7", "Call of Duty Warzone"],
        ["Delta Force"] = ["Delta Force Hawk Ops"],
        ["Halo MCC"] = ["Halo: The Master Chief Collection", "Halo The Master Chief Collection"],
        ["Battlefield 1"] = ["Battlefield 1 Revolution"],
        ["Battlefield 4"] = ["Battlefield 4 Premium Edition"],
        ["Splitgate"] = ["Splitgate 2", "Splitgate: Arena Reloaded", "Splitgate Arena Reloaded"],
        ["Roblox"] = ["Roblox Player"],
        ["Minecraft"] = ["Minecraft for Windows", "Minecraft Bedrock"],
    };

    // Lower case letters and digits only, so "Battlefield™ 6" and "battlefield 6" match.
    public static string Normalize(string name) =>
        string.Concat(name.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    // Which of the games a store's entry is: by the program it runs, or by its name.
    internal static string? Match(IReadOnlyList<GameTarget> games, string? displayName, string? exe = null)
    {
        var exeName = string.IsNullOrEmpty(exe) ? null : exe.Split('\\', '/')[^1];
        if (exeName is not null)
        {
            foreach (var g in games)
                if (g.Executables.Any(e => string.Equals(e, exeName, StringComparison.OrdinalIgnoreCase))) return g.Name;
        }
        if (string.IsNullOrWhiteSpace(displayName)) return null;
        var key = Normalize(displayName);
        if (key.Length == 0) return null;
        foreach (var g in games)
        {
            if (Normalize(g.Name) == key) return g.Name;
            if (Aliases.TryGetValue(g.Name, out var names) && names.Any(n => Normalize(n) == key)) return g.Name;
        }
        return null;
    }

    public static GameScan Scan(IReadOnlyList<GameTarget> games)
    {
        if (!OperatingSystem.IsWindows()) return GameScan.Unknown;
        var found = new Dictionary<string, GameInstall>(StringComparer.OrdinalIgnoreCase);
        var steam = SteamPath();

        void Add(GameInstall install)
        {
            if (found.TryGetValue(install.Name, out var known))
            {
                // Keep the first store that found it, filling in what it didn't know.
                found[install.Name] = known with
                {
                    Folder = known.Folder ?? install.Folder,
                    Exe = known.Exe ?? install.Exe,
                    SteamAppId = known.SteamAppId ?? install.SteamAppId,
                };
            }
            else found[install.Name] = install;
        }

        foreach (var step in new Action[]
                 {
                     () => { foreach (var i in ScanSteam(steam, games)) Add(i); },
                     () => { foreach (var i in ScanEpic(games)) Add(i); },
                     () => { foreach (var i in ScanUninstallEntries(games)) Add(i); },
                     () => { foreach (var i in ScanXbox(games)) Add(i); },
                     () => { foreach (var i in ScanRoblox(games)) Add(i); },
                     () => { foreach (var i in ScanStorePackages(games)) Add(i); },
                 })
        {
            try { step(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException or JsonException) { }
        }

        // The program itself, for its icon, when the store only said which folder the game is in.
        foreach (var (name, install) in found.ToList())
        {
            if (install.Exe is { } exe && File.Exists(exe)) continue;
            var target = games.FirstOrDefault(g => g.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (target is null || install.Folder is null) continue;
            found[name] = install with { Exe = FindExe(install.Folder, target.Executables) };
        }
        foreach (var (name, install) in found.ToList())
        {
            if (install.SteamAppId is null && SteamIds.TryGetValue(name, out var id)) found[name] = install with { SteamAppId = id };
        }
        return new GameScan(found, steam, true);
    }

    // ---------------------------------------------------------------------------------------------------- Steam

    [SupportedOSPlatform("windows")]
    private static string? SteamPath()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var path = key?.GetValue("SteamPath") as string;
        if (string.IsNullOrEmpty(path))
        {
            using var machine = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
            path = machine?.GetValue("InstallPath") as string;
        }
        return string.IsNullOrEmpty(path) ? null : Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }

    private static IEnumerable<GameInstall> ScanSteam(string? steam, IReadOnlyList<GameTarget> games)
    {
        if (steam is null) yield break;
        var libraries = new List<string> { steam };
        var folders = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (File.Exists(folders)) libraries.AddRange(ParseLibraryFolders(File.ReadAllText(folders)));

        var byId = SteamIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps)) continue;
            foreach (var manifest in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                if (ParseAppManifest(File.ReadAllText(manifest)) is not { } app) continue;
                var name = byId.TryGetValue(app.Id, out var known) && games.Any(g => g.Name.Equals(known, StringComparison.OrdinalIgnoreCase))
                    ? known
                    : Match(games, app.Name);
                if (name is null) continue;
                var folder = app.InstallDir.Length > 0 ? Path.Combine(apps, "common", app.InstallDir) : null;
                yield return new GameInstall(name, "Steam", folder is not null && Directory.Exists(folder) ? folder : null, null, app.Id);
            }
        }
    }

    internal static IReadOnlyList<string> ParseLibraryFolders(string vdf) =>
        LibraryPath().Matches(vdf).Select(m => m.Groups[1].Value.Replace(@"\\", @"\")).ToList();

    internal static (int Id, string Name, string InstallDir)? ParseAppManifest(string acf)
    {
        var id = AcfValue(acf, "appid");
        if (!int.TryParse(id, out var appId)) return null;
        return (appId, AcfValue(acf, "name") ?? "", AcfValue(acf, "installdir") ?? "");
    }

    private static string? AcfValue(string acf, string key)
    {
        var m = Regex.Match(acf, "\"" + Regex.Escape(key) + "\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Replace(@"\\", @"\") : null;
    }

    [GeneratedRegex("\"path\"\\s+\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();

    // ---------------------------------------------------------------------------------------------------- Epic

    private static IEnumerable<GameInstall> ScanEpic(IReadOnlyList<GameTarget> games)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                               "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(dir)) yield break;
        foreach (var file in Directory.EnumerateFiles(dir, "*.item"))
        {
            if (ParseEpicManifest(File.ReadAllText(file)) is not { } item) continue;
            if (Match(games, item.Name, item.Exe) is not { } name) continue;
            var exe = item.Exe.Length > 0 ? Path.Combine(item.Location, item.Exe) : null;
            yield return new GameInstall(name, "Epic Games", Directory.Exists(item.Location) ? item.Location : null,
                                         exe is not null && File.Exists(exe) ? exe : null, null);
        }
    }

    internal static (string Name, string Location, string Exe)? ParseEpicManifest(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        string Text(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        var location = Text("InstallLocation");
        if (location.Length == 0) return null;
        return (Text("DisplayName"), location, Text("LaunchExecutable").Replace('/', Path.DirectorySeparatorChar));
    }

    // ---------------------------------------------------------------------------------------------------- Windows

    // Battle.net, the EA app, Ubisoft Connect, Riot, Steam and most other launchers list their games here.
    private static IEnumerable<GameInstall> ScanUninstallEntries(IReadOnlyList<GameTarget> games)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var entry in UninstallEntries())
        {
            var icon = IconPath(entry.Icon) is { } p && File.Exists(p) ? p : null;
            if (Match(games, entry.Name, icon) is not { } name) continue;
            var folder = entry.Location.Length > 0 && Directory.Exists(entry.Location) ? entry.Location : null;
            if (folder is null && icon is not null) folder = Path.GetDirectoryName(icon);
            yield return new GameInstall(name, StoreFor(entry.Publisher, entry.Key), folder, icon, null);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IEnumerable<(string Key, string Name, string Location, string Icon, string Publisher)> UninstallEntries()
    {
        const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        var roots = new[]
        {
            (Microsoft.Win32.Registry.LocalMachine, path),
            (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Microsoft.Win32.Registry.CurrentUser, path),
        };
        foreach (var (hive, sub) in roots)
        {
            using var key = hive.OpenSubKey(sub);
            if (key is null) continue;
            foreach (var name in key.GetSubKeyNames())
            {
                using var app = key.OpenSubKey(name);
                if (app?.GetValue("DisplayName") is not string display) continue;
                yield return (name, display, app.GetValue("InstallLocation") as string ?? "",
                              app.GetValue("DisplayIcon") as string ?? "", app.GetValue("Publisher") as string ?? "");
            }
        }
    }

    // "C:\Games\Overwatch\Overwatch.exe",0 → the program, when it's one.
    internal static string? IconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return null;
        var path = displayIcon.Trim();
        var comma = path.LastIndexOf(',');
        if (comma > 0 && int.TryParse(path[(comma + 1)..].Trim(), out _)) path = path[..comma];
        path = path.Trim().Trim('"');
        return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    internal static string StoreFor(string publisher, string key)
    {
        if (key.StartsWith("Steam App", StringComparison.OrdinalIgnoreCase)) return "Steam";
        if (key.StartsWith("Uplay Install", StringComparison.OrdinalIgnoreCase)) return "Ubisoft Connect";
        if (key.StartsWith("Riot Game", StringComparison.OrdinalIgnoreCase)) return "Riot";
        var p = publisher.ToLowerInvariant();
        if (p.Contains("blizzard")) return "Battle.net";
        if (p.Contains("activision")) return "Battle.net";
        if (p.Contains("electronic arts")) return "EA app";
        if (p.Contains("riot")) return "Riot";
        if (p.Contains("ubisoft")) return "Ubisoft Connect";
        if (p.Contains("battlestate")) return "BSG Launcher";
        if (p.Contains("roblox")) return "Roblox";
        return "Installed";
    }

    // The Xbox app puts games in X:\XboxGames\<game>\Content on any drive.
    private static IEnumerable<GameInstall> ScanXbox(IReadOnlyList<GameTarget> games)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
            var root = Path.Combine(drive.RootDirectory.FullName, "XboxGames");
            if (!Directory.Exists(root)) continue;
            foreach (var dir in Directory.EnumerateDirectories(root))
            {
                var content = Path.Combine(dir, "Content");
                var folder = Directory.Exists(content) ? content : dir;
                var byName = Match(games, Path.GetFileName(dir));
                var exe = FindExe(folder, games.SelectMany(g => g.Executables).ToList(), depth: 2);
                var name = (exe is not null ? Match(games, null, exe) : null) ?? byName;
                if (name is not null) yield return new GameInstall(name, "Xbox app", folder, exe, null);
            }
        }
    }

    // Roblox installs itself for the user, and so do the launchers people use in its place.
    private static IEnumerable<GameInstall> ScanRoblox(IReadOnlyList<GameTarget> games)
    {
        var target = games.FirstOrDefault(g => g.Name.Equals("Roblox", StringComparison.OrdinalIgnoreCase));
        if (target is null) yield break;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        foreach (var launcher in new[] { "Roblox", "Bloxstrap", "Fishstrap" })
        {
            var versions = Path.Combine(local, launcher, "Versions");
            if (!Directory.Exists(versions)) continue;
            var exe = Directory.EnumerateDirectories(versions)
                               .Select(d => Path.Combine(d, "RobloxPlayerBeta.exe"))
                               .Where(File.Exists)
                               .OrderByDescending(File.GetLastWriteTimeUtc)
                               .FirstOrDefault();
            if (exe is null) continue;
            yield return new GameInstall(target.Name, "Roblox", Path.GetDirectoryName(exe), exe, null);
            yield break;
        }
    }

    // Microsoft Store games keep their files out of reach; their package names say they're there.
    private static IEnumerable<GameInstall> ScanStorePackages(IReadOnlyList<GameTarget> games)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        if (!games.Any(g => g.Name.Equals("Minecraft", StringComparison.OrdinalIgnoreCase))) yield break;
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (key is null) yield break;
        if (key.GetSubKeyNames().Any(n => n.StartsWith("Microsoft.MinecraftUWP_", StringComparison.OrdinalIgnoreCase)))
            yield return new GameInstall("Minecraft", "Microsoft Store", null, null, null);
    }

    // One of the game's programs in its folder, a few levels down at most.
    internal static string? FindExe(string folder, IReadOnlyCollection<string> names, int depth = 4)
    {
        if (names.Count == 0 || !Directory.Exists(folder)) return null;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = depth,
            IgnoreInaccessible = true,
            MatchCasing = MatchCasing.CaseInsensitive,
        };
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*.exe", options))
            {
                var name = Path.GetFileName(file);
                if (names.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return file;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }
}
