namespace ClipEngine.Desktop.Services;

// How the engine finds moments in each game it supports (src/game/GameDetector.cpp and
// assets/detection.ini). Games not listed here are read off the screen.
internal static class GameCatalog
{
    public enum Method { Screen, Killfeed, GameApi, ConsoleLog, Hotkey }

    private static readonly Dictionary<string, Method> Methods = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Counter-Strike 2"] = Method.GameApi,
        ["Dota 2"] = Method.GameApi,
        ["League of Legends"] = Method.GameApi,
        ["Rocket League"] = Method.GameApi,
        ["Team Fortress 2"] = Method.ConsoleLog,
        ["Valorant"] = Method.Killfeed,
        ["Marvel Rivals"] = Method.Killfeed,
        ["Rainbow Six Siege"] = Method.Killfeed,
        ["Delta Force"] = Method.Killfeed,
        ["Deadlock"] = Method.Killfeed,
        ["Halo Infinite"] = Method.Killfeed,
        ["Paladins"] = Method.Killfeed,
        ["Splitgate"] = Method.Killfeed,
        ["Roblox"] = Method.Hotkey,
        ["Minecraft"] = Method.Hotkey,
    };

    public static Method For(string game) => Methods.GetValueOrDefault(game, Method.Screen);

    public static string Describe(string game) => For(game) switch
    {
        Method.GameApi => "Official game events",
        Method.ConsoleLog => "Reads the game's console log",
        Method.Killfeed => "Reads the killfeed",
        Method.Hotkey => "Records, clips on the hotkey",
        _ => "Reads the screen",
    };
}
