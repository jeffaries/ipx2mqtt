namespace Ipx2Mqtt.Service;

/// <summary>
/// Optional settings file in the appsettings.json format, kept outside the application so that a Docker volume
/// can provide it, e.g. <c>{ "Ipx": { "Labels": { "04-8R.05": "Lumières Z3" } } }</c> (it can hold any setting,
/// <c>Entities</c> included). Looked up, first match wins: the <c>IPX2MQTT_CONFIG</c> environment variable,
/// <c>/config/ipx2mqtt.json</c> (Docker volume), then <c>config/ipx2mqtt.json</c> in the current directory or
/// one of its parents (development). It overrides appsettings.json; environment variables and the command line
/// still override it.
/// </summary>
public static class ExternalConfig
{
    public const string FileName = "ipx2mqtt.json";
    public const string EnvironmentVariable = "IPX2MQTT_CONFIG";

    public static string? Locate(string? configured, string? currentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // An explicit path must exist: a typo should not silently drop the settings.
            return File.Exists(configured)
                ? configured
                : throw new FileNotFoundException($"{EnvironmentVariable} '{configured}' does not exist.");
        }

        var docker = Path.Combine(Path.DirectorySeparatorChar.ToString(), "config", FileName);
        if (File.Exists(docker))
        {
            return docker;
        }

        for (var dir = new DirectoryInfo(currentDirectory ?? Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config", FileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static void AddTo(ConfigurationManager configuration, string[] args)
    {
        var path = Locate(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (path is null)
        {
            return;
        }

        configuration.AddJsonFile(path, optional: false, reloadOnChange: false);
        // Restore the usual precedence: environment variables and command line win over any file.
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);
        Console.WriteLine($"Extra settings loaded from {path}");
    }
}
