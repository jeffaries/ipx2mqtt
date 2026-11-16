using NetEscapades.Configuration.Yaml;

namespace Ipx2Mqtt.Service;

/// <summary>
/// The two YAML files that hold the whole configuration, kept outside the application so that a Docker volume
/// can provide them: <c>config.yml</c> (MQTT, IPX, device...) and <c>entities.yml</c> (labels and declared entities).
/// The folder is looked up, first match wins: the <c>IPX2MQTT_CONFIG_DIR</c> environment variable, <c>/config</c>
/// (Docker volume), then <c>config/</c> in the current directory or one of its parents (development).
/// The files override appsettings.json; environment variables and the command line still override the files.
/// </summary>
public static class ExternalConfig
{
    public static readonly string[] FileNames = { "config.yml", "entities.yml" };
    public const string EnvironmentVariable = "IPX2MQTT_CONFIG_DIR";

    public static string? LocateDirectory(string? configured, string? currentDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            // An explicit folder must exist: a typo should not silently drop the configuration.
            return Directory.Exists(configured)
                ? configured
                : throw new DirectoryNotFoundException($"{EnvironmentVariable} '{configured}' does not exist.");
        }

        var docker = Path.Combine(Path.DirectorySeparatorChar.ToString(), "config");
        if (HasConfigFile(docker))
        {
            return docker;
        }

        for (var dir = new DirectoryInfo(currentDirectory ?? Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "config");
            if (HasConfigFile(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool HasConfigFile(string directory) =>
        FileNames.Any(name => File.Exists(Path.Combine(directory, name)));

    public static void AddTo(ConfigurationManager configuration, string[] args)
    {
        var directory = LocateDirectory(Environment.GetEnvironmentVariable(EnvironmentVariable));
        if (directory is null)
        {
            return;
        }

        foreach (var name in FileNames)
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                configuration.AddYamlFile(path, optional: false, reloadOnChange: false);
                Console.WriteLine($"Configuration loaded from {path}");
            }
        }

        // Restore the usual precedence: environment variables and command line win over any file.
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);
    }
}
