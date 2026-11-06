using System.Reflection;

namespace ClipEngine.Desktop.Services;

// Where Clip Engine's online service is, and this PC's id with it.
internal static class Online
{
    public static string BuiltInServer { get; } =
        typeof(Online).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "OnlineServer")?.Value ?? "";

    public static string ServerUrl(DesktopSettings settings)
    {
        var url = settings.OnlineServer.Trim();
        if (url.Length == 0) url = BuiltInServer;
        return url.TrimEnd('/');
    }

    public static string DeviceId(DesktopSettings settings)
    {
        if (settings.DeviceId.Length < 8) settings.DeviceId = Guid.NewGuid().ToString("N");
        return settings.DeviceId;
    }

    // Tokens are kept encrypted for this Windows account, like the update token.
    public static string Protect(string token) => OperatingSystem.IsWindows() ? UpdateToken.Protect(token) : token;
    public static string? Unprotect(string stored) => OperatingSystem.IsWindows() ? UpdateToken.Unprotect(stored) : stored;
}
